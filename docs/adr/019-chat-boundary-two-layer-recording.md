# ADR-019: Chat-Boundary Tracing and the Two-Layer Recording Model

## Status

**Accepted** — delivered as Glass Box v0.11 on branch `feat/glass-box-v0.11-chat-boundary-tracing`. Builds on [ADR-004](004-trace-recording-replay.md) (trace record/replay) and the modularization of [ADR-016](016-monolith-modularization.md).

## Date

2026-05-31

## Context

AgentEval recorded only what the agent framework *reported*: `TraceRecordingAgent` wraps an `IEvaluableAgent` and captures one request/response per invocation — exactly what the agent boundary chooses to surface. When a MAF agent runs an internal plan-act-observe loop that hits the model N times, the recorder sees one top-level call and whatever subset of the loop the framework exposes. Retried turns, intermediate tool failures, injected system prompts, per-turn finish reasons, and provider verdicts are invisible by design. For black-box behavioural tests this is fine; for audit-grade compliance evidence and for detecting framework-level honesty violations it is not.

We need a second capture layer one level down — at the `Microsoft.Extensions.AI.IChatClient` boundary — where every LLM round-trip is visible verbatim before any agent layer summarises, retries, or filters it. MEAI 10.5.0 ships the required base types (`DelegatingChatClient` with virtual `GetResponseAsync`/`GetStreamingResponseAsync`; `ChatClientBuilder.Use`), confirmed by reflection, so no hand-rolled shim is needed.

## Decision

1. **Add `TraceRecordingChatClient : DelegatingChatClient`** in `AgentEval.Core` (`AgentEval.Tracing`). It records one `TraceEntry` per LLM round-trip (`Scope = ChatTurn`), capturing the verbatim system prompt, tool definitions, per-turn text, finish reason, request options, provider metadata, token usage, and latency. Placing it in Core (MEAI-only, no MAF dependency) makes the capability reach **any** `IChatClient` — Semantic Kernel, custom orchestration, raw loops — not just MAF.

2. **Composition rule (load-bearing):** because `ChatClientBuilder` makes the first `.Use(...)` the outermost layer and `FunctionInvokingChatClient` (FICC) calls its inner client once per round-trip, `UseTraceRecording` **must be composed inner of `UseFunctionInvocation`** to observe every round-trip. Composing it outer records one entry for the whole loop.

3. **Tool-execution seam:** add `EvaluatingAIFunction : AIFunction` in `AgentEval.MAF` to capture what `IChatClient` middleware structurally cannot — the function execution (timing, args, result, exception) — as a `ToolExecution`-scoped entry. It calls the inner function's **public** `InvokeAsync` (not the `protected` `InvokeCoreAsync`, which CS1540 forbids through a base-typed reference).

4. **Correlation:** a caller-established `ToolCorrelationScope` (AsyncLocal, in **Core** so both the Core recorder and the MAF tool wrapper can read it) stamps every entry of one invocation with a shared `CorrelationId`. `Index` remains the request/response pairing key; `CorrelationId` is the grouping key.

5. **The two recorders are complementary, not competing.** `ChatTraceRecorder` (conversation replay, one entry per user turn) is unchanged; `TraceRecordingChatClient` answers "what did the model see on every call." `agenteval doctor` warns when both wrap the same agent (double-wrapping).

6. **Trace Fidelity** (a Shape-B benchmark family on `BenchmarkFamilyRegistry`, per [ADR-017](017-unified-benchmarks-namespace.md)) reconciles the two layers and flags missing/phantom calls, hidden retries, argument drift, token under-reporting, and suppressed finish reasons — auditing the framework's honesty.

## Consequences

**Positive**
- White-box, audit-grade evidence: every round-trip, tool schema, finish reason, and provider verdict is observable and hash-anchorable.
- Framework-agnostic by construction (recorder is MEAI-only in Core).
- A structurally novel capability (Trace Fidelity) that no two-layer-less competitor can offer, plus an upstream-bug-report loop to `microsoft/agent-framework`.
- A runtime policy gate ([guardrails](../guardrails.md)) reuses the same `IChatClient` seam.

**Negative / costs**
- Trace volume grows (~N× for an N-turn loop); mitigated by tool-definition de-dup in Smoke/Standard presets (off in AuditGrade).
- Composition order is load-bearing and easy to get wrong; mitigated by docs, the `doctor` check, and a pinned composition test.
- Per-turn↔tool correlation is best-effort (per-invocation grouping + tool `CallId`); precise under sequential tools, documented caveat under parallel tools.

## Alternatives Considered

- **Agent-boundary only (status quo).** Rejected: cannot surface hidden retries, suppressed finish reasons, or per-turn evidence.
- **A hand-rolled `IChatClient` decorator shim.** Rejected: MEAI 10.5.0 already ships `DelegatingChatClient`/`ChatClientBuilder.Use` (verified by reflection), so the shim is unnecessary.
- **Recorder in `AgentEval.MAF`.** Rejected: would tie chat-boundary capture to MAF and forfeit SK/raw-loop reach; Core placement keeps it universal.
- **Subclassing `TraceEntry` for chat-turn/tool entries.** Rejected: the trace serializes a flat `List<TraceEntry>` with reflection-based STJ; subclasses would break it. Additive nullable fields + static factory methods are used instead (see [ADR-020](020-agenttrace-v1_1-schema.md)).
