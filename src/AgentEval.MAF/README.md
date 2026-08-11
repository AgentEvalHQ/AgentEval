# AgentEval.MAF

The **Microsoft Agent Framework (MAF) integration layer** for [AgentEval](../../README.md). This is where
AgentEval stops being an offline evaluation harness and plugs into a live agent pipeline.

> **Packaging:** this project is `IsPackable=false`. Its DLL ships **bundled inside the `AgentEval` NuGet
> package** (there is no separate `AgentEval.MAF` package on nuget.org) — so `dotnet add package AgentEval` gives
> you everything here.

## What it provides

- **🚪 Gatekeeper — runtime fail-closed enforcement** (`AgentEval.MAF.Gatekeeper`). Wrap a MAF agent so a
  forbidden tool call, a poisoned argument, a dangerous tool *sequence*, an unauthorized operator, or a
  compromised session is **blocked before it happens** — no rewrite. Every decision is recorded as honest
  `gate.*` trace evidence.
  - **Tool gates** — `UseAgentEvalToolGate` over the function-invocation seam: `ForbiddenToolGate`,
    `ArgumentPatternGate`, `SequenceGate`, enforced by `ToolGatePolicy` (WarnOnly / ReplaceResult / Terminate).
  - **Run gates** — `UseAgentEvalGate` inspects the run's input (incoming attack) and output (leak) text.
  - **Session gates** — `OperatorAuthGate`, `RateLimitGate`, `QuarantineGate` (fail-closed).
  - **Shadow judge** — `UseAgentEvalShadowJudge` runs the expensive LLM/network checks the inline gates reject,
    off the hot path, arming quarantine for a *later* run.
  - **Tool approval** — `UseAgentEvalToolApproval` routes a *borderline* call to a human over MAF's native
    `UseToolApproval` (stable since MAF 1.14.0).
  - The **moat** (red-team probes as gates) lives in the sibling `AgentEval.RedTeam.Gatekeeper`.
- **Agent adapters** — bridge a MAF `AIAgent` to AgentEval's evaluable-agent surface so the same evaluation
  suites run against a real MAF agent.
- **Light path** — surface AgentEval's metrics as a `Microsoft.Extensions.AI` `IEvaluator`, so they drop into
  MAF's own evaluation pipeline (`agent.EvaluateAsync(...)`).

## Provider-agnostic by design

This layer intentionally does **not** reference `Microsoft.Agents.AI.OpenAI` — add it in your own project for
Azure/OpenAI scenarios. If you use `[MessageHandler]` executors, add
`Microsoft.Agents.AI.Workflows.Generators` yourself (a dev-time tooling dependency).

## Docs

- [Gatekeeper — Introduction](../../docs/gatekeeper/introduction.md) · [Gate Reference (ranked)](../../docs/gatekeeper/gate-reference.md) · [Examples](../../docs/gatekeeper/examples.md)
- [Glass Box tracing](../../docs/glass-box.md) — where the `gate.*` evidence is recorded.
