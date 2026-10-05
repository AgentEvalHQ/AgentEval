# Trace Fidelity

**Does what the agent framework *reports* it did match what the model *actually saw*?** Trace Fidelity (Glass Box) reconciles two traces — the **agent-boundary** trace (the framework's self-report, built with `AgentBoundaryTraceBuilder.FromAgentResponse` from the run's final messages, aggregate usage and finish reason) and the **chat-boundary** trace (ground truth at the model interface, from [`TraceRecordingChatClient`](../tracing.md)) — and flags where they diverge. What it checks is the framework's account of the run, not the agent's answer: tool calls the framework omitted or reported without the model requesting them, tool arguments that differ between the two layers, retries it did not report, token totals it under-reports, and `content_filter`/`length` finish reasons it did not pass on.

> A trace recorded by `TraceRecordingAgent` can stand as the agent-boundary account, with one gap. On the non-streaming path it records the agent's finish reason and the tool calls the response carries in its messages (arguments included; approval-gated calls are not recorded as calls). On the streaming path it records tool calls with their arguments but **no finish reason**, because a streamed chunk carries none, so reconciling a streaming trace reports every `content_filter`/`length` chat turn as `suppressed_finish_reason`. That finding then says nothing about the framework.

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
| `token_under_reporting` | the agent-layer total is lower than the per-turn chat-boundary sum by more than 2% of that sum (over-reporting is not flagged) | Low |
| `suppressed_finish_reason` | a chat turn ended with `content_filter`/`length` and the agent boundary did not report that reason (it reported `stop`, another reason, or none) | Critical |

> Reconciliation compares tool **calls**, finish reasons, and token usage — never tool *definition schemas* — so tool-definition de-dup (Smoke/Standard presets) never affects the result. Argument comparison is by serialized-string set equality (a documented v1 heuristic, so a retry of the same args is counted by `hidden_retries`, not `argument_drift`).

> **Finish reasons.** The two layers cannot be paired turn by turn (the agent boundary usually has one entry per invocation, the chat boundary one per model round-trip), so `suppressed_finish_reason` is counted per reason: the number of chat turns that ended with `content_filter` (or `length`) minus the number of agent-boundary response entries that report it, floored at zero. A faithful report, where the agent boundary passes the same reason through, is not flagged. Reasons are compared as reported strings, case-insensitively, so a framework that reports the same reason under another label (for example `MaxTokens` for `length`) is counted as not reporting it. An agent boundary that reports a reason the chat boundary never saw is not counted by this class.

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

Writes a run manifest + `report-native.json` under `.agenteval/`. Exit code `0` = clean (score ≥ 0.99, PASS), `10` = minor discrepancies (0.80–0.99, WARN), `9` = discrepancies (below 0.80, FAIL), `11` = nothing to reconcile (the chat trace has no model responses, SKIPPED), `1` = setup/IO error.

## The upstream loop with Microsoft Agent Framework

When the evaluator finds a mismatch, you hold two things no MAF maintainer has: a reproducible scenario and two parallel evidence streams (reported vs. observed). That is the ideal shape of a bug report — file a `trace-fidelity` report against `microsoft/agent-framework` directly.

## Related

- [Tracing — two recording layers](../tracing.md)
- [Benchmarks overview](../benchmarks.md)
