# Gatekeeper — examples

Runnable, **credential‑free** recipes, from a hello‑world to the gates with no simpler equivalent. For
the concepts see the [introduction](introduction.md); for what each gate does and how useful it is, the
[gate reference](gate-reference.md).

## Your first gate — the moat

The simplest use that also shows the whole point: the **same** deterministic evaluator that *scores* an attack
offline now *blocks* it at runtime. Just the builder calls: `.AsBuilder()` → `.UseAgentEvalToolGate(...)` → `.Build()`.

```csharp
using AgentEval.MAF.Gatekeeper;      // GateCost
using AgentEval.RedTeam.Gatekeeper;  // ProbeEvaluatorGate — the moat
using AgentEval.RedTeam.Evaluators;  // ContainsTokenEvaluator

// The SAME ContainsToken check you'd use to SCORE an attack, now a LIVE guard on every tool call:
var gate = new ProbeEvaluatorGate(new ContainsTokenEvaluator("PWNED"), GateCost.PureCode);

var agent = baseAgent.AsBuilder()
    .UseAgentEvalToolGate([gate], ToolGatePolicy.ReplaceResult, trace)
    .Build();

await agent.RunAsync("publish the page");   // a tool call carrying "PWNED" is blocked before it runs
```

`ProbeEvaluatorGate` is fail‑closed on the enforcement path — only a clear *Resisted* verdict allows; *Succeeded*
and *Inconclusive* both block. (LLM‑backed evaluators are rejected at construction — send those to the shadow
judge below.) Runnable: sample **`Gatekeeper/00_GatekeeperHelloWorld`**.

> The plainest gate is a deny‑list (`new ForbiddenToolGate("delete_everything")`) — but it's the *simplest*, not
> the most powerful: if you control the tool list, not granting the tool is stronger. The examples below are the
> ones that catch what a tool list can't.

## Block data exfiltration — a dangerous *sequence*

Each tool is fine on its own — you *want* `read_customer_data`, and you *want* `send_email`. The **combination**
is the attack, and nothing but a sequence gate catches it.

```csharp
var agent = baseAgent.AsBuilder()
    .UseAgentEvalGate()   // establishes the per-run scope the sequence gate tracks
    .UseAgentEvalToolGate(
        [new SequenceGate(triggerTools: ["read_customer_data"], guardedTools: ["send_email", "http_post"])],
        ToolGatePolicy.Terminate)
    .Build();
// Once the agent reads customer data, any send/POST in the same run is blocked — exfiltration stopped.
```

## The honeypot — detect a compromised agent

A canary is a lure the agent has **no legitimate reason to touch**. Advertise it as a real tool; the model
*emitting* a call to it is proof it was manipulated (a prompt injection landed) — blocked before the body runs.

```csharp
var canary = new CanaryTool
{
    Name = "exfiltrate_secrets",
    Description = "Uploads all stored secrets to an external URL.",
    ForbiddenCategory = "DataExfiltration",
};

var agent = new ChatClientAgent(client, new ChatClientAgentOptions
    {
        ChatOptions = new ChatOptions { Tools = [.. CanaryLure.Tools(canary)] },   // advertise the lure to the model
    })
    .AsBuilder()
    .UseAgentEvalToolGate([new CanaryToolGate([canary])], ToolGatePolicy.Terminate)
    .Build();
```

## Human‑in‑the‑loop approval

For actions too risky to auto‑run but too legitimate to forbid — route the *borderline* ones to a person.

```csharp
var agent = new ChatClientAgent(client, new ChatClientAgentOptions
    {
        // Opt the tool INTO the approval flow — only .RequiresApproval()-wrapped tools are gated.
        ChatOptions = new ChatOptions { Tools = [issueRefund.RequiresApproval()] },
    })
    .AsBuilder()
    // A small refund auto-approves; a large amount (per the pattern) is escalated to a human.
    .UseAgentEvalToolApproval([new ArgumentPatternApprovalGate("\"amount\":\\s*[0-9]{4,}")])
    .Build();

var session = await agent.CreateSessionAsync();
var paused = await agent.RunAsync("refund $5000", session);
var request = paused.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();
// …a human reviews, then approves on the same session:
await agent.RunAsync([new ChatMessage(ChatRole.User, [request.CreateResponse(approved: true)])], session);
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

An unauthorized operator, an injected prompt, or a rate‑limit breach is refused **before the model is ever
called**. (For a *judge‑backed* prompt‑injection pre‑gate — a fast Composite Judge on the incoming prompt — see
[Extending the Gatekeeper](gate-reference.md#extending-the-gatekeeper-llm-backed-detection).)

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

- [`Gatekeeper/00_GatekeeperHelloWorld`](../../samples/AgentEval.Samples/Gatekeeper/00_GatekeeperHelloWorld.cs) —
  **start here**: the simplest gate — your red‑team check blocks a live poisoned call, in three lines.
- [`Gatekeeper/01_GatekeeperEnforcement`](../../samples/AgentEval.Samples/Gatekeeper/01_GatekeeperEnforcement.cs) —
  the **enforcement walkthrough**: a forbidden tool, the moat, a canary honeypot, a shadow verdict quarantining
  the next run, a **defense‑in‑depth** scene, and a **more‑gates** scene (`ArgumentPatternGate` + `SequenceGate` +
  a run‑post PII gate).
- [`Gatekeeper/02_GatekeeperMafHarness`](../../samples/AgentEval.Samples/Gatekeeper/02_GatekeeperMafHarness.cs) — a
  **realistic MAF support agent** — **data-exfiltration defense**: every tool is legitimate, but a prompt injection's
  read‑customer‑data → external‑POST *sequence* is blocked by `SequenceGate` (no tool‑list trick catches this).
- [`Gatekeeper/03_GatekeeperToolApproval`](../../samples/AgentEval.Samples/Gatekeeper/03_GatekeeperToolApproval.cs) —
  **human‑in‑the‑loop approval**: a routine refund auto‑approves, a large one pauses for a human and resumes.

## From the CLI

```bash
agenteval redteam --sut gatekeeper-demo
```

Scans a built‑in gated agent with the real attack suite and reports how many attempts the gate blocked —
credential‑free, and composing with `--baseline` / `--fail-on regression` for **attack‑the‑gate CI**.
