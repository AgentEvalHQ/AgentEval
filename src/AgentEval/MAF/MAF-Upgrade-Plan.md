# MAF Upgrade Plan — Release Candidate (`1.0.0-rc1`)

## Executive Summary

**From:** `Microsoft.Agents.AI` `1.0.0-preview.251110.2`  
**To:** `Microsoft.Agents.AI` `1.0.0-rc1` (build `260219.1`, Feb 19, 2026)  
**Maturity:** Release Candidate — API surface is stabilized  
**Risk Level:** **HIGH** — Pervasive breaking renames, event hierarchy restructuring, method renames  
**Files Requiring Code Changes:** 3 source files + 1 test file + 1 config file (`Directory.Packages.props`) + 8 doc files  
**Files Requiring Re-Verification Only (0 code changes):** 18 sample files — adapter abstraction layer insulates all samples from internal MAF API changes  
**Files Unaffected:** 4 source files (MAFGraphExtractor, MAFWorkflowAdapter, MAFEvaluationHarness, WorkflowEvaluationHarness — stable APIs or zero MAF coupling) + 5 test files

### Verification Methodology

All API changes have been verified by comparing the **current** MAF source (`MAF/dotnet/src/`)
against the **RC candidate** source (`MAFvnext/dotnet/src/`). Every claim below is backed by
reading the actual `.cs` files in both directories.

**Key RC source files verified:**
- `MAFvnext/dotnet/src/Microsoft.Agents.AI.Abstractions/AIAgent.cs` — `CreateSessionAsync`, `RunAsync` signatures
- `MAFvnext/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSession.cs` — replaces `AgentThread`
- `MAFvnext/dotnet/src/Microsoft.Agents.AI.Abstractions/AgentResponse.cs` — replaces `AgentRunResponse`
- `MAFvnext/dotnet/src/Microsoft.Agents.AI.Workflows/AgentResponseUpdateEvent.cs` — now inherits `WorkflowOutputEvent`
- `MAFvnext/dotnet/src/Microsoft.Agents.AI.Workflows/InProcessExecution.cs` — `RunStreamingAsync`
- `MAFvnext/dotnet/src/Microsoft.Agents.AI.Workflows/WorkflowBuilder.cs` — `AddFanInBarrierEdge`

### Impact At a Glance

| Category | Count | Severity |
|----------|:-----:|----------|
| Type renames | 5 | 🔴 Breaking — every reference must update |
| Method renames (core) | 2 | 🔴 Breaking — sync→async, name changes |
| Method renames (workflow) | 3 | 🔴 Breaking — `StreamAsync` family → `RunStreamingAsync` |
| Event hierarchy change | 1 | 🔴 **CRITICAL** — `AgentResponseUpdateEvent` parent changed |
| Builder method renames | 1 | 🔴 Breaking — `AddFanInEdge` → `AddFanInBarrierEdge` |
| Naming conflict | 1 | 🔴 Breaking — MAF `AgentResponse` vs AgentEval `AgentResponse` |
| Architectural pattern changes | 2 | 🟡 Medium — abstract→protected Core (no direct AgentEval impact) |
| Removed APIs | 4+ | 🟡 Medium — replaced by new concepts |
| New APIs (additive) | 15+ | 🟢 Non-breaking — new capabilities |
| Dependency version bumps | 2 | 🟡 Medium — M.E.AI 10.0.0→10.3.0, M.E.AI.OpenAI preview→10.3.0 stable |

### What Changed and Why

MAF RC introduces a **major conceptual shift** across both the agent core and the workflow subsystem:

1. **Thread → Session**: `AgentThread` → `AgentSession` with `StateBag` for typed state. `GetNewThread()` (sync) → `CreateSessionAsync()` (async).
2. **"Run" Infix Removed**: `AgentRunResponse` → `AgentResponse`, `AgentRunResponseUpdate` → `AgentResponseUpdate`, `AgentRunUpdateEvent` → `AgentResponseUpdateEvent`.
3. **Event Hierarchy Restructured**: `AgentResponseUpdateEvent` now inherits from `WorkflowOutputEvent` instead of `ExecutorEvent`. This breaks pattern matching logic that checks `case ExecutorEvent when ... is AgentRunUpdateEvent`.
4. **Workflow Streaming Renames**: `InProcessExecution.StreamAsync()` → `RunStreamingAsync()`, `OpenStreamAsync()` → `OpenStreamingAsync()`, `ResumeStreamAsync()` → `ResumeStreamingAsync()`.
5. **Builder Renames**: `AddFanInEdge()` → `AddFanInBarrierEdge()` (clarifies semantics).
6. **Public abstract → Protected Core**: `RunAsync`/`RunStreamingAsync` on `AIAgent` are no longer directly overridable — new pattern uses `RunCoreAsync`/`RunCoreStreamingAsync`.
7. **Structured Output**: New `RunAsync<T>()` overloads for type-safe deserialization.
8. **Session parameter now optional**: `RunAsync(string, AgentSession?, AgentRunOptions?, CancellationToken)` — session defaults to `null`.

---

## 1. Breaking Changes — Detailed Analysis

### 1.1 Type Renames (Search & Replace)

These are mechanical renames verified by comparing `MAF/dotnet/src/` (current) against `MAFvnext/dotnet/src/` (RC).

| Old Type (MAF/) | New Type (MAFvnext/) | Impact on AgentEval |
|----------|----------|---------------------|
| `AgentRunResponse` | `AgentResponse` | `MAFAgentAdapter`, `MAFIdentifiableAgentAdapter` — return type of `RunAsync()` |
| `AgentRunResponseUpdate` | `AgentResponseUpdate` | Both adapters — streaming return type |
| `AgentThread` | `AgentSession` | Both adapters — field type, constructor param, method params |
| `AgentRunUpdateEvent` | `AgentResponseUpdateEvent` | `MAFWorkflowEventBridge` — pattern match case |
| `AgentRunResponseEvent` *(if used)* | `AgentResponseEvent` | Not currently used in AgentEval |

**Verification:**
- `MAF/dotnet/src/.../AgentThread.cs` → `MAFvnext/dotnet/src/.../AgentSession.cs`
- `MAF/dotnet/src/.../AgentRunResponse.cs` → `MAFvnext/dotnet/src/.../AgentResponse.cs`
- `MAF/dotnet/src/.../AgentRunUpdateEvent.cs` → `MAFvnext/dotnet/src/.../AgentResponseUpdateEvent.cs`

**Properties preserved on `AgentResponse` (confirmed in `MAFvnext/dotnet/src/.../AgentResponse.cs`):**
- `.Text` ✅ — computed property
- `.Messages` ✅ — `IList<ChatMessage>`
- `.Usage` ✅ — `UsageDetails?` (same type, same access)
- `.Usage.InputTokenCount` / `.OutputTokenCount` ✅
- `.ToString()` ✅ — returns `.Text`

### 1.2 🔴 CRITICAL: Naming Conflict — MAF `AgentResponse` vs AgentEval `AgentResponse`

The rename of MAF's `AgentRunResponse` → `AgentResponse` creates a **naming conflict** with AgentEval's own `AgentEval.Core.AgentResponse` type (defined in `src/AgentEval/Core/IEvaluableAgent.cs` line 49).

**Where this hits:**
- `MAFAgentAdapter.cs` imports both `using Microsoft.Agents.AI;` and `using AgentEval.Core;`
- After the rename, both `Microsoft.Agents.AI.AgentResponse` (MAF) and `AgentEval.Core.AgentResponse` (AgentEval) are in scope
- The `var response = await _agent.RunAsync(...)` line uses `var` so the MAF type is inferred
- But the return statement `return new AgentResponse { ... }` refers to `AgentEval.Core.AgentResponse`
- The compiler will report **CS0104: ambiguous reference**

**Blast radius analysis:**
- Only **2 source files** import both namespaces: `MAFAgentAdapter.cs` and `MAFIdentifiableAgentAdapter.cs`
- `MAFWorkflowAdapter.cs` is **safe** — it uses `using MAFWorkflows = Microsoft.Agents.AI.Workflows;` (alias) and does NOT import `Microsoft.Agents.AI` directly. Its `return new AgentResponse { ... }` at line 345 unambiguously resolves to `AgentEval.Core.AgentResponse`
- **Zero sample files** reference `AgentResponse` directly (they only use `ChatClientAgent`, `ChatClientAgentOptions`)

**Resolution (recommended):** Add a using alias that **takes precedence** over namespace imports (per C# spec §14.5.3/14.5.4) in the 2 affected files:
```csharp
using AgentResponse = AgentEval.Core.AgentResponse;  // alias takes precedence — resolves CS0104
```
This works because C# gives using alias directives priority over using namespace directives for name resolution. The unqualified name `AgentResponse` now unambiguously refers to `AgentEval.Core.AgentResponse`. Since `var` is used for all MAF type inferences (`var response = await _agent.RunAsync(...)`), the MAF `AgentResponse` type name is never written explicitly — no additional alias needed.

### 1.3 Method Signature Changes

| Method | Old Signature (MAF/) | New Signature (MAFvnext/) | AgentEval Impact |
|--------|--------------|--------------|------------------|
| `AIAgent.GetNewThread()` | `public abstract AgentThread GetNewThread()` | **REMOVED** → `public ValueTask<AgentSession> CreateSessionAsync(ct)` | 🔴 Both adapters call `_agent.GetNewThread()` — must await |
| `AIAgent.RunAsync(string, ...)` | `(string, AgentThread?, CancellationToken)` | `(string, AgentSession?, AgentRunOptions?, CancellationToken)` | 🔴 Param type change; new optional `AgentRunOptions?` param inserted |
| `AIAgent.RunStreamingAsync(string, ...)` | `(string, AgentThread?, CancellationToken)` | `(string, AgentSession?, AgentRunOptions?, CancellationToken)` | 🔴 Same pattern |

**Verified in MAFvnext:** `CreateSessionAsync()` is a **concrete** method on `AIAgent` that delegates to `protected abstract CreateSessionCoreAsync()`. This is the template method pattern — public API is stable, subclasses override Core.

**Backwards compatibility of `RunAsync`:** AgentEval's call pattern uses named `cancellationToken:` parameter:
```csharp
await _agent.RunAsync(prompt, thread, cancellationToken: cancellationToken);
```
After upgrade: `prompt` → `message`, `thread` → `session` (type change from `AgentThread?` to `AgentSession?`), skips `options` (defaults to null), `cancellationToken:` → `cancellationToken`. The `AgentThread?` → `AgentSession?` type change means the field and variable types must be updated.

**Note on `ResetThread()` / `GetNewThread()`:** These are NOT on any interface (`IEvaluableAgent`, `IStreamableAgent`). They are concrete methods only on the adapter classes. No external callers exist (verified by grep across entire codebase — only found in the adapter source files themselves). Impact is confined to the 2 adapter files.

### 1.4 🔴 CRITICAL: Event Hierarchy Restructuring

**This is the single most dangerous change in the RC upgrade.**

In the current MAF (`MAF/dotnet/src/.../AgentRunUpdateEvent.cs`):
```
ExecutorEvent
├── ExecutorInvokedEvent
├── ExecutorCompletedEvent
├── ExecutorFailedEvent
└── AgentRunUpdateEvent  ← inherits from ExecutorEvent
```

In the RC (`MAFvnext/dotnet/src/.../AgentResponseUpdateEvent.cs`):
```
WorkflowOutputEvent
└── AgentResponseUpdateEvent  ← NOW inherits from WorkflowOutputEvent, NOT ExecutorEvent
                                  (also now sealed — cannot be subclassed)

ExecutorEvent
├── ExecutorInvokedEvent
├── ExecutorCompletedEvent
└── ExecutorFailedEvent
```

**Note:** `AgentResponseUpdateEvent` is `sealed` in the RC (was non-sealed in preview). This has no AgentEval impact since we only pattern-match against it, never subclass it.

**Current AgentEval code (MAFWorkflowEventBridge.cs, line 148):**
```csharp
case MAFWorkflows.ExecutorEvent executorEvent
    when executorEvent is MAFWorkflows.AgentRunUpdateEvent agentUpdate:
```

**Why this BREAKS:** In the RC, `AgentResponseUpdateEvent` no longer inherits from `ExecutorEvent`. The pattern match `case ExecutorEvent when ... is AgentResponseUpdateEvent` will **never match** because `AgentResponseUpdateEvent` is a `WorkflowOutputEvent`. The event will instead fall through to:
```csharp
case MAFWorkflows.WorkflowOutputEvent workflowOutput:
```

This means **all streaming agent updates (text, tool calls, tool results) will be silently lost**, with the event bridge only seeing the final `WorkflowOutputEvent` completion signal.

**Required fix — restructure the switch statement:**
```csharp
// MUST ADD: New case arm BEFORE both ExecutorEvent and WorkflowOutputEvent cases
case MAFWorkflows.AgentResponseUpdateEvent agentUpdate:
    var updateExecutorId = NormalizeId(agentUpdate.ExecutorId ?? currentExecutorId ?? "unknown");
    // ... (same tool call + text extraction logic)
    break;

// MUST UPDATE: Remove the AgentRunUpdateEvent sub-check from ExecutorEvent catch-all
case MAFWorkflows.ExecutorEvent genericExecutorEvent
    when genericExecutorEvent is not MAFWorkflows.ExecutorInvokedEvent
    && genericExecutorEvent is not MAFWorkflows.ExecutorCompletedEvent
    && genericExecutorEvent is not MAFWorkflows.ExecutorFailedEvent:
    // No longer need to exclude AgentRunUpdateEvent — it's not an ExecutorEvent anymore
    break;
```

**Key details:**
- `AgentResponseUpdateEvent` has `.ExecutorId` property (inherited from `WorkflowOutputEvent`) — same data, different inheritance path
- `AgentResponseUpdateEvent.Update` is `AgentResponseUpdate` — same interface as before (`.Contents`, `.Text`)
- The `case AgentResponseUpdateEvent` MUST appear before `case WorkflowOutputEvent` in the switch

### 1.5 🔴 Workflow Streaming Method Renames

Verified by comparing `MAF/dotnet/src/.../InProcessExecution.cs` vs `MAFvnext/dotnet/src/.../InProcessExecution.cs`.

| Old Method (MAF/) | New Method (MAFvnext/) | AgentEval Impact |
|------------|-----------|------------------|
| `InProcessExecution.StreamAsync(workflow, ...)` | `InProcessExecution.RunStreamingAsync(workflow, ...)` | 🔴 `MAFWorkflowEventBridge` line 91 |
| `InProcessExecution.StreamAsync<T>(workflow, ...)` | `InProcessExecution.RunStreamingAsync<T>(workflow, ...)` | 🔴 `MAFWorkflowEventBridge` line 105 |
| `InProcessExecution.OpenStreamAsync(...)` | `InProcessExecution.OpenStreamingAsync(...)` | ⚪ Not used by AgentEval |
| `InProcessExecution.ResumeStreamAsync(...)` | `InProcessExecution.ResumeStreamingAsync(...)` | ⚪ Not used by AgentEval |

**Note:** The `runId` parameter was renamed to `sessionId` in `MAFvnext`. AgentEval does not use this parameter (it passes `cancellationToken:` only), so no impact.

**MAFWorkflowEventBridge.cs requires two changes:**
```csharp
// Line 91: StreamAsync → RunStreamingAsync
run = await MAFWorkflows.InProcessExecution
    .RunStreamingAsync(workflow, new ChatMessage(ChatRole.User, input), cancellationToken: cancellationToken)

// Line 105: StreamAsync<string> → RunStreamingAsync<string>
run = await MAFWorkflows.InProcessExecution
    .RunStreamingAsync<string>(workflow, input, cancellationToken: cancellationToken)
```

### 1.6 Builder Method Renames

Verified: `MAF/dotnet/src/.../WorkflowBuilder.cs` has `AddFanInEdge`. `MAFvnext/dotnet/src/.../WorkflowBuilder.cs` has `AddFanInBarrierEdge`.

| Old Method | New Method | AgentEval Impact |
|------------|-----------|------------------|
| `WorkflowBuilder.AddFanInEdge(...)` | `WorkflowBuilder.AddFanInBarrierEdge(...)` | 🔴 Test file `MAFGraphExtractorTests.cs` line 147 |

**Note:** `EdgeKind.FanIn` enum value is **unchanged**. The test uses `.AddFanInEdge([b, c], d)` (sources first, target second) — which maps to the current non-obsolete overload `AddFanInBarrierEdge(IEnumerable<ExecutorBinding>, ExecutorBinding)`.

### 1.7 Removed/Redesigned APIs

| Removed API | Replacement | AgentEval Impact |
|-------------|------------|------------------|
| `AIAgent.GetNewThread()` | `AIAgent.CreateSessionAsync(CancellationToken)` | 🔴 Direct — both adapters |
| `AIAgent.DisplayName` | Removed (use `.Name`) | ⚪ Not used |
| `AIAgent.DeserializeThread()` | `AIAgent.DeserializeSessionAsync()` | ⚪ Not used |
| `StreamingRun.RunId` | Renamed to `StreamingRun.SessionId` | ⚪ Not accessed by AgentEval |
| `WorkflowOutputEvent.SourceId` | Renamed to `.ExecutorId` (old kept as `[Obsolete]`) | ⚪ Not accessed directly |

### 1.8 Architectural Pattern Changes

#### Abstract Method Pattern (No AgentEval Impact)

Verified in `MAFvnext/dotnet/src/.../AIAgent.cs`:
```csharp
// RC: Template method pattern
public Task<AgentResponse> RunAsync(...)                    // concrete — sets CurrentRunContext
protected abstract Task<AgentResponse> RunCoreAsync(...)    // override point
```

AgentEval does NOT subclass `AIAgent` — it calls public `RunAsync()`/`RunStreamingAsync()`, which still exist.

#### `IsChatProtocol` Signature (No AgentEval Impact)

`ChatProtocolExtensions.IsChatProtocol(ProtocolDescriptor)` now has an optional `bool allowCatchAll = false` parameter. Default applies. No change needed.

### 1.9 Dependency Version Changes

Verified in `MAFvnext/dotnet/Directory.Packages.props`:

| Package | Current (AgentEval) | Required by RC | Action |
|---------|---------|---------------|--------|
| `Microsoft.Extensions.AI` | `10.0.0` | `10.3.0` | 🟡 Must bump in `Directory.Packages.props` |
| `Microsoft.Extensions.AI.Abstractions` | (transitive) | `10.3.0` | 🟡 Pulled transitively |
| `Microsoft.Extensions.AI.OpenAI` | `10.0.0-preview.1.25559.3` | `10.3.0` | 🟡 Must bump |

---

## 2. Additive Changes (Non-Breaking, New Capabilities)

| Feature | Description | Potential AgentEval Use |
|---------|-------------|----------------------|
| `AgentSession.StateBag` | Typed key-value state per session | Could expose in `TestResult` |
| `AIAgent.CurrentRunContext` | Ambient `AsyncLocal` context during runs | Could capture for diagnostics |
| `RunAsync<T>()` | Structured output with typed deserialization | Could add structured output assertions |
| `AgentRunOptions.ResponseFormat` | Request specific response formats | Could parameterize test cases |
| `Workflow.ReflectExecutors()` | Reflection over bound executors | Could simplify `MAFGraphExtractor` |
| Edge `Label` property | Labels on workflow edges | Could include in `WorkflowGraphSnapshot` |
| `BindAsExecutor(AIAgentHostOptions?)` | New overload alongside old `emitEvents` overload | More control over executor hosting |
| `AgentResponseUpdateEvent.AsResponse()` | Convert single update to full response | Could use in event bridge |
| `LoggingAgent` | Built-in delegating agent for logging | Could wrap agents during evaluation |

---

## 3. No-Impact Changes

| Change | Reason No Impact |
|--------|------------------|
| `AIAgent.Id` no longer `virtual` (uses `IdCore`) | AgentEval reads `.Id`, doesn't override |
| `DelegatingAIAgent` now `abstract` | Not used |
| `AIAgentMetadata` now `sealed` | Not used |
| Checkpointing reflection types (`EdgeInfo`, `DirectEdgeInfo`) | **Unchanged** ✅ |
| `EdgeKind` enum | **Unchanged** ✅ |
| `StreamingRun` / `WatchStreamAsync()` / `TrySendMessageAsync()` | **Unchanged** ✅ |
| `TurnToken` | **Unchanged** ✅ |
| `Workflow.ReflectEdges()` | Return type changed (`List<EdgeInfo>` → `HashSet<EdgeInfo>` in dictionary values) — no code impact (iteration-only usage) ✅ |
| `Workflow.DescribeProtocolAsync()` | **Unchanged** ✅ |
| `ExecutorInvokedEvent`, `ExecutorCompletedEvent`, `ExecutorFailedEvent` | **Unchanged** ✅ |
| `WorkflowBuilder`, `.AddEdge()`, `.Build()` | **Unchanged** ✅ |
| `ChatClientAgent`, `ChatClientAgentOptions` | **Backward compatible** ✅ — new optional `IServiceProvider? services` param on constructors; new `AIContextProviders` + `UseProvidedChatClientAsIs` on options. All optional with defaults — zero AgentEval changes needed |
| `BindAsExecutor(bool emitEvents)` | **Still exists** ✅ (new `AIAgentHostOptions?` overload added alongside) |

---

## 4. File-by-File Update Plan (Source)

### 4.1 `MAFAgentAdapter.cs` — 🔴 HIGH (8 changes)

| Line Area | Change | Detail |
|-----------|--------|--------|
| Field declaration | `AgentThread? _thread` → `AgentSession? _session` | Type + name |
| Constructor param | `AgentThread? thread = null` → `AgentSession? session = null` | Type + name |
| Constructor body | `_thread = thread` → `_session = session` | Name |
| `InvokeAsync` - session | `_thread ?? _agent.GetNewThread()` → `_session ?? await _agent.CreateSessionAsync(ct)` | Sync→async |
| `InvokeStreamingAsync` - session | Same as above | Sync→async |
| `ResetThread()` | `_thread = _agent.GetNewThread()` → `_session = await _agent.CreateSessionAsync()` | Sync→async, method rename |
| `GetNewThread()` | `_agent.GetNewThread()` → `_agent.CreateSessionAsync()` | Sig change, method rename |
| Using alias | Add `using AgentResponse = AgentEval.Core.AgentResponse;` | **Naming conflict** fix (alias takes precedence per C# spec §14.5.3) |

**Before/After:**
```csharp
// BEFORE:
private AgentThread? _thread;
public MAFAgentAdapter(AIAgent agent, AgentThread? thread = null) { ... }
var thread = _thread ?? _agent.GetNewThread();
var response = await _agent.RunAsync(prompt, thread, cancellationToken: cancellationToken);
public void ResetThread() { _thread = _agent.GetNewThread(); }
public AgentThread GetNewThread() => _agent.GetNewThread();

// AFTER:
private AgentSession? _session;
public MAFAgentAdapter(AIAgent agent, AgentSession? session = null) { ... }
var session = _session ?? await _agent.CreateSessionAsync(cancellationToken);
var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
public async Task ResetSessionAsync(CancellationToken ct = default) { _session = await _agent.CreateSessionAsync(ct); }
public async Task<AgentSession> CreateSessionAsync(CancellationToken ct = default) => await _agent.CreateSessionAsync(ct);
```

**Note:** `ResetThread()` and `GetNewThread()` must become async since `CreateSessionAsync()` returns `ValueTask<AgentSession>`. These are NOT on any interface — purely concrete adapter methods. No external callers found.

### 4.2 `MAFIdentifiableAgentAdapter.cs` — 🔴 HIGH (8 changes)

Identical changes to `MAFAgentAdapter.cs`.

### 4.3 `MAFWorkflowEventBridge.cs` — 🔴 CRITICAL (5 changes)

| Line Area | Change | Detail |
|-----------|--------|--------|
| Line 91 | `StreamAsync(workflow, ...)` → `RunStreamingAsync(workflow, ...)` | Method rename |
| Line 105 | `StreamAsync<string>(workflow, ...)` → `RunStreamingAsync<string>(workflow, ...)` | Method rename |
| Line 148 | Restructure `AgentRunUpdateEvent` pattern match | **Event hierarchy change** (see §1.4) |
| Line 194 | Update generic `ExecutorEvent` catch-all | Remove `AgentRunUpdateEvent` exclusion |
| XML comments | Update event type references in `<remarks>` | Documentation accuracy |

### 4.4 `MAFGraphExtractor.cs` — ⚪ NO CHANGES NEEDED

All APIs used are stable in the RC:
- `Workflow.ReflectEdges()` / `Workflow.StartExecutorId` ✅ (Note: Return type changed from `Dictionary<string, List<EdgeInfo>>` to `Dictionary<string, HashSet<EdgeInfo>>`. No impact — all AgentEval code uses `var` and iterates with `foreach`, which works identically on both types.)
- `Checkpointing.EdgeInfo` / `DirectEdgeInfo.HasCondition` ✅
- `EdgeKind.Direct` / `EdgeKind.FanOut` / `EdgeKind.FanIn` ✅

### 4.5 `MAFWorkflowAdapter.cs` — ⚪ NO CHANGES NEEDED

Delegates to `MAFWorkflowEventBridge` and `MAFGraphExtractor`. Uses `using MAFWorkflows = Microsoft.Agents.AI.Workflows;` alias — does NOT import `Microsoft.Agents.AI` directly. The `return new AgentResponse { ... }` at line 345 unambiguously resolves to `AgentEval.Core.AgentResponse` — no naming conflict.

### 4.6 `MAFEvaluationHarness.cs` / `WorkflowEvaluationHarness.cs` — ⚪ NO CHANGES

Zero MAF dependencies.

---

## 5. Test File Update Plan

### 5.1 `MAFWorkflowEventBridgeTests.cs` — ⚪ NO CODE CHANGES (re-verify tests pass)

Tests assert on AgentEval event types, not MAF types. Uses function-based executors, so `AgentRunUpdateEvent` hierarchy change is not exercised. Tests must be re-run after source changes.

### 5.2 `MAFGraphExtractorTests.cs` — 🔴 MUST UPDATE

| Change | Detail |
|--------|--------|
| `.AddFanInEdge([b, c], d)` → `.AddFanInBarrierEdge([b, c], d)` | Builder method renamed (line 147) |

### 5.3 `MAFWorkflowAdapterFromMAFWorkflowTests.cs` — ⚪ NO CHANGES NEEDED

All builder APIs unchanged.

### 5.4-5.6 Other tests — ⚪ NO CHANGES

Zero MAF SDK references.

---

## 6. Samples Impact Analysis

### 6.1 Overview

18 sample files across 2 projects reference MAF types.

### 6.2 Tier 1 — Workflow Samples (⚪ RE-VERIFY, 2 files)

| File | Changes Needed |
|------|---------------|
| `Sample09_WorkflowEvaluationReal.cs` | Verify `BindAsExecutor(emitEvents: true)` still works (yes — overload preserved). Uses `.AddEdge()` only (no `AddFanInEdge`). |
| `Sample10_WorkflowWithTools.cs` | Same — uses `.AddEdge()` only (no `AddFanInEdge`). |

### 6.3 Tier 2 — Agent Creation Samples (⚪ RE-VERIFY, 14 files)

These samples create `ChatClientAgent` instances. `ChatClientAgent` and `ChatClientAgentOptions` are **unchanged** in the RC.

| File |
|------|
| `Sample01_HelloWorld.cs` |
| `Sample02_AgentWithOneTool.cs` |
| `Sample03_AgentWithMultipleTools.cs` |
| `Sample04_PerformanceMetrics.cs` |
| `Sample06_Benchmarks.cs` |
| `Sample07_SnapshotTesting.cs` |
| `Sample12_PolicySafetyEvaluation.cs` |
| `Sample13_TraceRecordReplay.cs` |
| `Sample14_StochasticEvaluation.cs` |
| `Sample15_ModelComparison.cs` |
| `Sample16_CombinedStochasticComparison.cs` |
| `Sample19_StreamingVsAsyncPerformance.cs` |
| `Sample20_RedTeamBasic.cs` |
| `Sample21_RedTeamAdvanced.cs` |

**Changes needed:** None — **0 code changes required.** `new MAFAgentAdapter(agent)` constructor is unchanged. `ChatClientAgent`, `ChatClientAgentOptions`, `BindAsExecutor(emitEvents: true)`, `WorkflowBuilder.AddEdge()` are all backward-compatible. The adapter abstraction layer insulates all sample code from the internal MAF renames (`AgentThread→AgentSession`, `GetNewThread→CreateSessionAsync`, etc.). Verified by exhaustive grep: zero sample files reference `AgentThread`, `GetNewThread`, `ResetThread`, `AgentRunResponse`, or `StreamAsync` directly.

### 6.4 Tier 3 — Unused Import Only (🟢 LOW, 2 files)

| File | Notes |
|------|-------|
| `Sample08_ConversationEvaluation.cs` | Has `using Microsoft.Agents.AI;` but no MAF type references — dead import |
| `Sample11_DatasetsAndExport.cs` | Same — dead import |

### 6.5 Files Without MAF References (7 files)

`Sample05_ComprehensiveRAG.cs`, `Sample17_QualitySafetyMetrics.cs`, `Sample18_JudgeCalibration.cs`, `Sample22_ResponsibleAI.cs`, `Sample23_BenchmarkSystem.cs`, `Program.cs`, `AIConfig.cs`

### 6.6 NuGet Consumer Project

`AgentFactory.cs` uses `ChatClientAgent`, `ChatClientAgentOptions` — unchanged. `.csproj` needs MAF version bumped.

---

## 7. Documentation Impact

### 7.1 Search & Replace

| Find | Replace | Scope |
|------|---------|-------|
| `AgentRunUpdateEvent` | `AgentResponseUpdateEvent` | All docs |
| `AgentRunResponse` (careful) | `AgentResponse` | Docs only (not code comments for AgentEval type) |
| `AgentRunResponseUpdate` | `AgentResponseUpdate` | All docs |
| `AgentThread` | `AgentSession` | All docs |
| `GetNewThread()` | `CreateSessionAsync()` | All docs |
| `StreamAsync(` | `RunStreamingAsync(` | InProcessExecution context only |
| `AddFanInEdge(` | `AddFanInBarrierEdge(` | Workflow context only |
| `1.0.0-preview.251110.2` | `1.0.0-rc1` | Version references |

---

## 8. NuGet Package Updates

### `Directory.Packages.props` changes:

```xml
<!-- BEFORE -->
<PackageVersion Include="Microsoft.Agents.AI" Version="1.0.0-preview.251110.2" />
<PackageVersion Include="Microsoft.Agents.AI.OpenAI" Version="1.0.0-preview.251110.2" />
<PackageVersion Include="Microsoft.Agents.AI.Workflows" Version="1.0.0-preview.251110.2" />
<PackageVersion Include="Microsoft.Extensions.AI" Version="10.0.0" />
<PackageVersion Include="Microsoft.Extensions.AI.OpenAI" Version="10.0.0-preview.1.25559.3" />

<!-- AFTER -->
<PackageVersion Include="Microsoft.Agents.AI" Version="1.0.0-rc1" />
<PackageVersion Include="Microsoft.Agents.AI.OpenAI" Version="1.0.0-rc1" />
<PackageVersion Include="Microsoft.Agents.AI.Workflows" Version="1.0.0-rc1" />
<PackageVersion Include="Microsoft.Extensions.AI" Version="10.3.0" />
<PackageVersion Include="Microsoft.Extensions.AI.OpenAI" Version="10.3.0" />
```

**Note:** `Microsoft.Extensions.AI.OpenAI` goes from a **preview** (`10.0.0-preview.1.25559.3`) to a **stable** release (`10.3.0`). This is a significant quality improvement — stable releases have stronger API guarantees.

**Verify after bump:** `Microsoft.Extensions.AI.Evaluation.Quality` is currently at `9.5.0`. Since M.E.AI bumps from `10.0.0` to `10.3.0`, and both share transitive dependencies on `Microsoft.Extensions.AI.Abstractions`, **recommend bumping to `10.3.0`** (available; used by MAFvnext). Cross-major-version combinations (`9.x` Evaluation + `10.x` Abstractions) risk `MissingMethodException` at runtime.

---

## 9. Recommended Update Sequence

### Phase 1: Preparation
1. Read this plan fully
2. Ensure all tests pass on current codebase: `dotnet test`
3. Create a feature branch: `git checkout -b feat/maf-rc1-upgrade`

### Phase 2: NuGet Version Bump
1. Update `Directory.Packages.props` (see §8)
2. `dotnet restore` — verify package resolution
3. `dotnet build` — expect compile errors (this validates what needs fixing)

### Phase 3: Critical Fixes (do these first)
1. **Event hierarchy fix** in `MAFWorkflowEventBridge.cs` (§1.4) — highest-risk change
2. **Naming conflict fix** — add using alias in both adapter files (§1.2)
3. **Streaming method renames** — `StreamAsync` → `RunStreamingAsync` in `MAFWorkflowEventBridge.cs` (§1.5)

### Phase 4: Type Renames (Mechanical)
1. `AgentThread` → `AgentSession` (both adapters)
2. `GetNewThread()` → `CreateSessionAsync()` (both adapters, sync→async)
3. `AddFanInEdge` → `AddFanInBarrierEdge` (test file)

### Phase 5: Build & Fix
1. `dotnet build` — resolve any remaining compile errors
2. Address any new warnings from M.E.AI 10.3.0

### Phase 6: Test
1. `dotnet test` — run all tests (×3 TFMs)
2. Pay special attention to workflow event bridge tests

### Phase 7: Samples & Docs
1. Update sample files (§6)
2. Update documentation (§7)
3. Update `CHANGELOG.md`

### Phase 8: Cleanup
1. Commit: `feat: upgrade MAF to 1.0.0-rc1`

---

## 10. Risk Assessment

| Risk | Severity | Likelihood | Mitigation |
|------|----------|-----------|------------|
| **Event hierarchy silent regression** — `AgentResponseUpdateEvent` not caught by pattern match | 🔴 Critical | High (certain if not fixed) | §1.4 restructuring is mandatory |
| **Naming conflict** — `AgentResponse` ambiguity | 🔴 High | Certain | Using alias or full qualification |
| `CreateSessionAsync` behavioral difference from `GetNewThread` | 🟡 Medium | Medium | Test both adapters with real MAF agents |
| M.E.AI 10.3.0 introduces breaking changes to `IChatClient` | 🟡 Medium | Low | Check M.E.AI 10.3.0 changelog |
| RC NuGet package not yet published | 🟡 Medium | Medium | Verify `1.0.0-rc1` on NuGet.org before starting |
| `BindAsExecutor` overload resolution | 🟢 Low | Low | Named args (`emitEvents: true`) are explicit; old overload preserved |

---

## 11. Summary

| File | Changes | Severity |
|------|:-------:|----------|
| `MAFAgentAdapter.cs` | 8 | 🔴 High |
| `MAFIdentifiableAgentAdapter.cs` | 8 | 🔴 High |
| `MAFWorkflowEventBridge.cs` | 5 | 🔴 Critical |
| `MAFGraphExtractor.cs` | 0 | ⚪ None |
| `MAFWorkflowAdapter.cs` | 0 | ⚪ None |
| `MAFEvaluationHarness.cs` | 0 | ⚪ None |
| `WorkflowEvaluationHarness.cs` | 0 | ⚪ None |
| `Directory.Packages.props` | 5 | 🟡 Medium |
| `MAFGraphExtractorTests.cs` | 1 | 🟡 Medium |
| Other test files | 0 | ⚪ None |
| 18 sample files | 0 | ⚪ Re-verify only (adapter insulates) |
| Documentation | varies | 🟡 Medium |

---

*Verified by comparing MAF/dotnet/src/ (current pre-RC) against MAFvnext/dotnet/src/ (RC candidate).*  
*All type renames, method renames, and hierarchy changes confirmed by reading actual .cs source files.*
