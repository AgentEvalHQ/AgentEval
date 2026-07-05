# Gatekeeper

**Glass Box tells you what your agent *did*. Gatekeeper stops it from doing the wrong thing — at runtime, fail‑closed.**

Evaluation and red‑teaming find problems *after the fact*. Gatekeeper is the other half: it puts the same
checks **in the request path** so a forbidden tool call, a poisoned argument, or a compromised conversation is
**blocked before it happens**. It plugs into the Microsoft Agent Framework agent pipeline — no rewrite — and
every gate is **fail‑closed**: if a gate cannot prove an action safe, it does not run it.

## The design principle: fail closed, and prove it

A security control that quietly does nothing is worse than none — it gives false assurance. Gatekeeper takes
two hard positions:

- **Cannot‑inspect ⇒ deny.** A gate that throws, times out, or can't read its context **blocks**. A gate is
  never allowed to fail *open*.
- **Honest evidence.** Every decision is recorded into the same [`AgentTrace`](glass-box.md) the evaluators
  read, under `gate.*` keys. A gate that only *warns* records `action="Warn"` — it is **never** counted as a
  block, so the evidence can never claim it stopped a call that actually ran.

## The layers

Gatekeeper wraps a MAF agent at the seams where an action can be stopped. Register the gates you need; each is
independent and writes to one shared trace.

| Layer | Seam | Blocks | Cost budget |
|---|---|---|---|
| **Tool gate** | each tool call, pre‑execution | a forbidden / poisoned / out‑of‑sequence tool call | pure‑code / bounded |
| **Run gate** | the run's input and output text | an incoming attack (run‑pre) or a leaking response (run‑post) | pure‑code / bounded |
| **Session gates** | before a run | an unauthorized operator, a rate‑limit breach, a quarantined session | pure‑code |
| **The moat** | a tool call | anything your red‑team probes/canaries catch | pure‑code / bounded |
| **Shadow judge** | *after* the run, async | arms quarantine for a **later** run | **anything — LLM, network** |

The cost column is the load‑bearing constraint. Inline gates run on the hot path, so they **reject** network /
LLM work at construction — an LLM judge on every tool call would stall the agent and risk a fabricated verdict.
The **shadow judge** is where that expensive judgment goes.

## Tool gates

A tool gate inspects one live tool call and returns Allow / Block / Mutate. How a block is **enforced** is a
separate `ToolGatePolicy` — mirroring the shipped chat‑gate split — so adding a gate never silently changes
behavior:

- `WarnOnly` (default) — record only; the tool still runs. Safe to add to any agent.
- `ReplaceResult` — block the call and return a refusal as the tool result.
- `Terminate` — block and stop the tool‑calling loop.

```csharp
using AgentEval.MAF.Gatekeeper;

var agent = baseAgent.AsBuilder()
    .UseAgentEvalToolGate(
        [new ForbiddenToolGate("delete_database", "wire_transfer")],
        ToolGatePolicy.Terminate,
        trace)
    .Build();
```

Built‑in tool gates:

| Gate | Blocks |
|---|---|
| `ForbiddenToolGate` | a call to any tool on a deny‑list (case‑insensitive) |
| `ArgumentPatternGate` | a call whose arguments match a forbidden pattern (regex, bounded timeout) |
| `SequenceGate` | a guarded tool called *after* a trigger tool (e.g. `read_secrets` → `send_email`), scoped per run |

> **Enforcement floor.** A gate whose purpose is to *stop* an action can declare a `MinimumPolicy`. A honeypot
> gate refuses to be registered `WarnOnly` — silently observing a breach is not an option — so
> `UseAgentEvalToolGate` throws rather than let it be downgraded to observe‑only.

## Run gates

A run gate inspects the run's **input** text (run‑pre — assess incoming attacks) and **output** text (run‑post),
reusing the shipped guardrail gates (`IChatGate`). Register it outermost:

```csharp
var agent = baseAgent.AsBuilder()
    .UseAgentEvalGate(
        pre:  [new OperatorAuthGate("alice", "bob"), incomingAttackGate],
        post: [responseLeakGate],
        policy: EvalGatePolicy.ThrowOnFail)
    .Build();
```

Under a blocking policy a run‑pre attack refusal is returned *without ever calling the model*. Streaming runs
inspect the input only; a blocking run‑post gate on a stream fails closed at stream start rather than letting
unbuffered output through.

## Session gates

Session gates run before a run and read the run's session — **fail‑closed when the session context is absent**:

- `OperatorAuthGate` — allow‑list authorization over a caller‑supplied operator identity.
- `RateLimitGate` — N runs per window per session, with a race‑safe in‑process counter.
- `QuarantineGate` — blocks a session flagged by the shadow judge (below).

## The moat — your red‑team probes become gates

The most direct expression of the whole toolkit: the **same** oracle that scores an attack offline now **blocks
it at runtime**. This lives in `AgentEval.RedTeam.Gatekeeper`.

```csharp
using AgentEval.RedTeam.Gatekeeper;
using AgentEval.RedTeam.Evaluators;

// The ContainsToken evaluator you red-team with, now guarding a live call:
var gate = new ProbeEvaluatorGate(new ContainsTokenEvaluator("PWNED"), GateCost.PureCode);
```

`ProbeEvaluatorGate` is **fail‑closed on the enforcement path**: only a clear *Resisted* verdict allows the call
— *Succeeded* (attack detected) **and** *Inconclusive* (can't tell) both block. This deliberately inverts the
[grading](llm-as-judge.md) convention (where abstention must never be scored as a failure), because a runtime
gate that cannot prove a call safe must not run it. It accepts only deterministic, self‑contained content
evaluators — an LLM‑backed evaluator is rejected at construction (detected by an `IChatClient` reachable
anywhere in the evaluator tree).

`CanaryToolGate` graduates a red‑team **canary** into a production **honeypot**: `CanaryLure.Tools(...)`
advertises it to the model as a visible tool, and the model *emitting* a call to it is the compromise signal —
blocked before the body ever runs.

## The shadow judge — expensive checks, off the hot path

The inline gates reject LLM/network cost. The **shadow judge** is the release valve: it runs **after** the run
returns, on a background pump, so it can be as expensive as you like. It never blocks the run it observed —
instead an adverse verdict **arms quarantine**, and a `QuarantineGate` refuses to resume the session on the
**next** run.

```csharp
await using var pump = new ShadowJudgePump(myLlmJudge, onVerdict: store.Record);

var agent = baseAgent.AsBuilder()
    .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
    .UseAgentEvalShadowJudge(pump)
    .Build();
```

The pump is an **owned** object (create it with `await using`) with a **bounded** queue: if it fills under load,
items are dropped and reported — the returning run is never slowed. Verdicts go to a **sink**, never the live
trace, so the shadow task can never race the returning run's trace serialization. A hung network judge cannot
hang disposal — the drain is bounded and cancels in‑flight work.

## Doctor — a double‑gating check

`agenteval doctor` warns when the **same** policy recorded verdicts at both a chat seam (`pre`/`post`) and an
agent seam (`tool`/`run-*`) — a sign the same policy is gating twice. Register it once.

## A complete, credential‑free walkthrough

The [`SafetyAndSecurity/04_GatekeeperEnforcement`](../samples/AgentEval.Samples/SafetyAndSecurity/04_GatekeeperEnforcement.cs)
sample runs all of the above against a scripted model, so every outcome is deterministic and needs no API key:
a forbidden tool blocked, a poisoned argument caught by a red‑team evaluator, a canary honeypot held, and a
shadow verdict quarantining the next run.

## See also

- [Glass Box](glass-box.md) — the dual‑boundary trace Gatekeeper records its evidence into.
- [Guardrails](guardrails.md) — the chat‑gate primitives (`IChatGate`, `EvalGatePolicy`) the run gate reuses.
- [Red Team](redteam.md) — the probes and canaries the moat gates are built from.
