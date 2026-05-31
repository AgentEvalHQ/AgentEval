# ADR-020: AgentTrace v1.1 Schema (Glass Box additive fields)

## Status

**Accepted** — delivered as Glass Box v0.11 on branch `feat/glass-box-v0.11-chat-boundary-tracing`. Companion to [ADR-019](019-chat-boundary-two-layer-recording.md); extends the trace model of [ADR-004](004-trace-recording-replay.md).

## Date

2026-05-31

## Context

Chat-boundary recording ([ADR-019](019-chat-boundary-two-layer-recording.md)) needs to persist evidence the v1.0 `AgentTrace` schema cannot carry: which recording layer an entry came from, a per-invocation correlation id, the verbatim system prompt, the tool definitions sent to the model, the per-turn finish reason, the request options, and provider metadata. The change must be **purely additive** — existing v1.0 traces must continue to load and round-trip unchanged, no existing recorder behaviour may change, and no public API may break. The serializer is reflection-based `System.Text.Json` (`PropertyNamingPolicy = CamelCase`, `DefaultIgnoreCondition = WhenWritingNull`), and entries are a flat `List<TraceEntry>`.

Two distinct `AgentTrace` types exist and must not be confused: `AgentEval.Tracing.AgentTrace` (the working trace model, in Core) and `AgentEval.Output.AgentTrace` (an IOutputStore pipeline record, in Abstractions). This ADR concerns the Core one.

## Decision

1. **Bump `AgentTrace.Version` `"1.0"` → `"1.1"`.** Version denotes the highest schema version of any content the file *may* contain; individual entries may still be v1.0-shaped. Consumers branch on per-entry data (e.g. `TraceEntry.EffectiveScope`), never on the header string.

2. **Add additive, nullable fields to `TraceEntry`:** `Scope` (`TraceEntryScope?`), `CorrelationId`, `SystemPrompt`, `ToolDefinitions` (`List<TraceToolDefinition>?`), `FinishReason`, `RequestOptions`, `ProviderMetadata`, plus a non-serialized `EffectiveScope` convenience (null ⇒ `AgentInvocation`). Add the `TraceEntryScope` enum (`AgentInvocation = 0 | ChatTurn | ToolExecution`, `[JsonStringEnumConverter]`) and the `TraceToolDefinition` type.

3. **`Scope` is nullable.** With `WhenWritingNull`, agent-boundary entries (Scope = null) serialize **byte-identically** to v1.0 — no snapshot churn — while chat-boundary entries explicitly carry `"scope":"ChatTurn"`. Back-compat is therefore free: v1.0 files load with `Scope == null` (`EffectiveScope == AgentInvocation`); no serializer code change is required (proven by a round-trip regression test against an inline v1.0 fixture).

4. **No subclassing.** Because entries serialize as a flat `List<TraceEntry>`, layer-specific entries are built by **static factory methods** on `TraceEntry` (`ForChatRequest` / `ForChatResponse` / `ForChatError` / `ForToolExecution`) that return a populated base `TraceEntry`.

5. **`Index` vs `CorrelationId`.** `Index` is the request/response pairing key (`TraceReplayingAgent.BuildRequestResponsePairs` groups by it, filtering by `Type`); a round-trip's Request and Response share one Index and each round-trip gets a distinct one. `CorrelationId` is the per-invocation grouping key. Runtime gate verdicts are recorded into trace-level `Metadata` (not as entries) precisely so they never enter the Index pairing/replay path.

6. **`migrate` v1.0→v1.1** bumps the header on existing files (cosmetic, since the change is additive); `doctor` warns on double-wrapping.

## Consequences

**Positive**
- Existing v1.0 traces, exporters (JUnit/Markdown/JSON/SARIF/PDF — schema-driven), `TraceReplayingAgent`, and Mission Control all keep working with no change; richer evidence flows automatically once v1.1 fields are populated.
- One schema carries agent-boundary, chat-boundary, and tool-execution entries.

**Negative / costs**
- A v1.1 header now appears on traces produced by the new recorders (and on `migrate`d files); documented as "max schema version present."
- Consumers must treat `null` scope as `AgentInvocation` (the `EffectiveScope` helper standardises this).

## Alternatives Considered

- **A new parallel trace type for chat-boundary data.** Rejected: would fork the serializer, exporters, replay, and Mission Control; additive fields keep one model.
- **Non-nullable `Scope` defaulting to `AgentInvocation`.** Rejected: a value-type field always serialises, changing existing agent-boundary trace JSON (snapshot churn). Nullable + `WhenWritingNull` keeps v1.0 output identical.
- **A `SchemaVersion` integer / breaking rename.** Rejected: the field is `Version` (string) and must stay back-compatible.
