# Trace Fidelity

**Does what the agent framework *reports* it did match what the model *actually saw*?** Trace Fidelity (Glass Box) reconciles two traces — the **agent-boundary** trace (the framework's self-report, from `TraceRecordingAgent`) and the **chat-boundary** trace (ground truth at the model interface, from [`TraceRecordingChatClient`](../tracing.md)) — and flags where they diverge. It is the only .NET capability that audits the *framework's honesty*, not just the agent.

It is a **Shape-B benchmark family** (`trace-fidelity`, `CostTier.Free` — pure code, no LLM tokens).

> See **[Glass Box](../glass-box.md)** for the full two-layer model, the per-executor
> [`workflow-trace-fidelity`](workflow-trace-fidelity.md) variant, and the `glass-box-diagnostics` evaluators.

## The six discrepancy classes

| Class | Detected when | Severity |
|---|---|---|
| `missing_tool_calls` | the model called a tool the agent's account omits | High |
| `phantom_tool_calls` | the agent reports a call the model never requested | High |
| `argument_drift` | the same tool was called with genuinely different args on the two layers | Medium |
| `hidden_retries` | the chat boundary saw more invocations of a tool than the agent reported (silent retries) | High |
| `token_under_reporting` | per-turn token sums disagree with the agent-layer totals beyond 2% | Low |
| `suppressed_finish_reason` | the chat boundary saw `content_filter`/`length` but the agent boundary reported `stop`/none | Critical |

> Reconciliation compares tool **calls**, finish reasons, and token usage — never tool *definition schemas* — so tool-definition de-dup (Smoke/Standard presets) never affects the result. Argument comparison is by serialized-string set equality (a documented v1 heuristic, so a retry of the same args is counted by `hidden_retries`, not `argument_drift`).

## Scoring rubric (pinned)

Each class produces a child score in `[0,1]`: `childValue = 1 − min(severityPenalty × count, 1)`, with `severityPenalty` = Critical 1.00, High 0.50, Medium 0.25, Low 0.10. The root fidelity score is severity-weighted: `root = 1 − Σ(weight × (1 − childValue))`, with weights `missing 0.20, phantom 0.20, hidden_retries 0.20, suppressed 0.15, drift 0.15, token 0.10` (sum = 1.00).

**Worked example** (pinned by a divergence test): one hidden retry (High) → child `0.50`, root `1 − 0.20×0.50 = 0.90`.

The runner emits an `EvalResult` tree — one sub-result per class (`trace_fidelity.<class>`), each scored 0–1 with the 0–100 figure in `Details.Dimensions["score100"]` — so the existing audit chain, output store, and Mission Control render it with no bespoke wiring.

## CLI

```bash
agenteval bench trace-fidelity \
  --agent-trace ./agent.trace.json \
  --chat-trace  ./chat.trace.json \
  --preset standard \
  --subject MyAgent
```

Writes a run manifest + `report-native.json` under `.agenteval/`. Exit code `0` = clean, `2` = discrepancies, `1` = setup/IO error.

## The upstream loop with Microsoft Agent Framework

When the evaluator finds a mismatch, you hold two things no MAF maintainer has: a reproducible scenario and two parallel evidence streams (reported vs. observed). That is the ideal shape of a bug report — file a `trace-fidelity` report against `microsoft/agent-framework` directly.

## Related

- [Tracing — two recording layers](../tracing.md)
- [Benchmarks overview](../benchmarks.md)
