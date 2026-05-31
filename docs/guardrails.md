# Runtime Policy Gate (Glass Box)

AgentEval's behavioural policies (`NeverCallTool`, `NeverPassArgumentMatching`, `MustConfirmBefore`) are **post-hoc** fluent assertions on `result.ToolUsage` — they run *after* execution and are perfect for CI tests. The **runtime policy gate** applies the same intent **inline**, around the live model call, so it can refuse bad input or scrub bad output *before* damage is done.

| | Post-hoc assertions | Runtime gate (`EvalGatingChatClient`) |
|---|---|---|
| When | After the run, in a test | Around every live model call |
| Surface | `result.ToolUsage.Should()…` | `IChatClient` middleware |
| Outcome | Raises `BehavioralPolicyViolationException` in CI | Warns / throws / redacts on production traffic |

> The gate is **not** a complete safety layer on its own — pair it with provider-side moderation. Its value is being an inline, auditable checkpoint whose every decision is recorded into the trace.

## Components

- **`IChatGate`** — `ValueTask<GateVerdict> InspectAsync(string text, …)`. A pre- or post-flight check.
- **`GateVerdict`** — `Allow` or `Block` (binary finding), with an optional `RedactedText` (a masked replacement the client may apply) and `Matches`.
- **`EvalGatePolicy`** — how a `Block` is enforced: `WarnOnly` (default), `ThrowOnFail`, `Redact`.
- **`EvalGatingChatClient`** — the `DelegatingChatClient` that runs the gates.
- Built-in gates: **`RegexPiiGate`** (Email/Phone/SSN/CreditCard/IP, ReDoS-bounded; supplies redacted text), **`TokenInjectionGate`** (configurable injection markers), **`SafetyMetricGate`** (adapts any `ISafetyMetric`, e.g. `ToxicityMetric`).

## Policies

- **`WarnOnly`** (default) — record the verdict, let the call through. Graduates safely from CI to production observability.
- **`ThrowOnFail`** — throw `EvalGateRefusalException` before/after the call. Opt-in.
- **`Redact`** — apply the gate's `RedactedText` (pre: rewrite the user message; post: replace the response text) and proceed. Opt-in. **Rejected at runtime for streaming + post-gates** (output bytes already in flight cannot be redacted).

## Usage & composition order

`UseEvalGate(pre: …)` goes **outermost** (it must see the original user input). Place `UseTraceRecording` **inner** of `UseFunctionInvocation` (see [tracing](tracing.md)). A post-gate placed inner of FICC inspects each per-turn output; place it outer of `UseFunctionInvocation` if you specifically need the final aggregated answer only.

```csharp
using AgentEval.Guardrails;
using AgentEval.Guardrails.Gates;

var trace = new AgentTrace();
var client = rawChatClient
    .AsBuilder()
    .UseEvalGate(pre: new IChatGate[] { new TokenInjectionGate() }, policy: EvalGatePolicy.ThrowOnFail, trace: trace)
    .UseFunctionInvocation()
    .UseTraceRecording("agent", trace)
    .UseEvalGate(post: new IChatGate[] { new RegexPiiGate() }, policy: EvalGatePolicy.Redact, trace: trace)
    .Build();
```

## Where verdicts are recorded

Every gate decision is written to **`AgentTrace.Metadata`** (trace-level), keyed `gate.{pre|post}.{seq}.{policyName}`, with value `{ action, reason, matches, correlationId }`. Gate verdicts are deliberately **not** `TraceEntry` rows, so they never collide with the per-round-trip `Index` pairing used by replay. Compliance evidence packs and Mission Control read gate verdicts from here.

## Related

- [Tracing — two recording layers](tracing.md)
- [Assertions (post-hoc behavioural policies)](assertions.md)
