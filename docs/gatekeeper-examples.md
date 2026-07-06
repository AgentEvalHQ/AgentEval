# Gatekeeper — examples

Runnable, **credential‑free** recipes. For the concepts see the [introduction](gatekeeper.md); for what each
gate does and how useful it is, the [gate reference](gatekeeper-gates.md).

## Block a forbidden / destructive tool

```csharp
using AgentEval.MAF.Gatekeeper;

var agent = baseAgent.AsBuilder()
    .UseAgentEvalToolGate(
        [new ForbiddenToolGate("delete_database", "wire_transfer")],
        ToolGatePolicy.Terminate,   // block AND stop the tool-calling loop
        trace)
    .Build();
```

## Catch a dangerous sequence, or a poisoned argument

```csharp
var agent = baseAgent.AsBuilder()
    .UseAgentEvalGate()   // establishes the run scope so SequenceGate state is per-run
    .UseAgentEvalToolGate(
        [
            new ArgumentPatternGate("&&|\\brm\\s+-rf\\b"),      // block shell chaining in any arg
            new SequenceGate(["read_secrets"], ["send_email"]), // block read → exfiltrate
        ],
        ToolGatePolicy.Terminate)
    .Build();
```

## The moat — a red‑team oracle guarding a live call

```csharp
using AgentEval.MAF.Gatekeeper;      // GateCost
using AgentEval.RedTeam.Gatekeeper;  // ProbeEvaluatorGate
using AgentEval.RedTeam.Evaluators;  // ContainsTokenEvaluator

// The ContainsToken evaluator you red-team with, now guarding a live call:
var gate = new ProbeEvaluatorGate(new ContainsTokenEvaluator("PWNED"), GateCost.PureCode);
var agent = baseAgent.AsBuilder().UseAgentEvalToolGate([gate], ToolGatePolicy.ReplaceResult).Build();
```

## Defense in depth — session + run gates before the model runs

```csharp
var agent = baseAgent.AsBuilder()
    .UseAgentEvalGate(
        pre:  [new OperatorAuthGate("alice", "bob"), new TokenInjectionGate(), new RateLimitGate(maxRuns: 5, window: TimeSpan.FromMinutes(1))],
        post: [new RegexPiiGate()],   // redact / block a leaking response
        policy: EvalGatePolicy.ThrowOnFail)
    .Build();
```

Order matters: an unauthorized operator, an injected prompt, or a rate‑limit breach is refused **before the
model is ever called**.

## Human‑in‑the‑loop approval

```csharp
var agent = new ChatClientAgent(client, new ChatClientAgentOptions
    {
        // Opt the tool INTO the approval flow — only .RequiresApproval()-wrapped tools are gated.
        ChatOptions = new ChatOptions { Tools = [issueRefund.RequiresApproval()] },
    })
    .AsBuilder()
    // Small refunds auto-approve; a large amount (per the pattern) is escalated to a human.
    .UseAgentEvalToolApproval([new ArgumentPatternApprovalGate("\"amount\":\\s*[0-9]{4,}")])
    .Build();

var session = await agent.CreateSessionAsync();
var paused = await agent.RunAsync("refund $5000", session);
var request = paused.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();
// …a human reviews, then approves on the same session:
await agent.RunAsync([new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)])], session);
```

## Expensive checks off the hot path — the shadow judge

```csharp
await using var pump = new ShadowJudgePump(myLlmJudge, onVerdict: store.Record);

var agent = baseAgent.AsBuilder()
    .UseAgentEvalGate(pre: [new QuarantineGate()], policy: EvalGatePolicy.ThrowOnFail)
    .UseAgentEvalShadowJudge(pump)
    .Build();
```

The judge runs *after* the run returns; an adverse verdict arms quarantine so the `QuarantineGate` refuses the
session's **next** run.

## Credential‑free demos

The **Gatekeeper** sample group (`AgentEval.Samples`, menu group **J**) runs everything above against a scripted
model, so every outcome is deterministic and needs no API key:

- [`Gatekeeper/01_GatekeeperEnforcement`](../samples/AgentEval.Samples/Gatekeeper/01_GatekeeperEnforcement.cs) —
  the **enforcement walkthrough**: a forbidden tool blocked, a poisoned argument caught by a red‑team evaluator, a
  canary honeypot held, a shadow verdict quarantining the next run, a **defense‑in‑depth** scene, and a
  **more‑gates** scene (`ArgumentPatternGate` + `SequenceGate` + a run‑post PII gate).
- [`Gatekeeper/02_GatekeeperMafHarness`](../samples/AgentEval.Samples/Gatekeeper/02_GatekeeperMafHarness.cs) — a
  **realistic MAF support agent**: a legit request flows normally, and a prompt‑injection attack that turns the
  model destructive is blocked at the tool boundary.
- [`Gatekeeper/03_GatekeeperToolApproval`](../samples/AgentEval.Samples/Gatekeeper/03_GatekeeperToolApproval.cs) —
  **human‑in‑the‑loop approval**: a routine refund auto‑approves, a large one pauses for a human and resumes once
  approved.

## From the CLI

```bash
agenteval redteam --sut gatekeeper-demo
```

Scans a built‑in gated agent with the real attack suite and reports how many attempts the gate blocked —
credential‑free, and composing with `--baseline` / `--fail-on regression` for **attack‑the‑gate CI**.
