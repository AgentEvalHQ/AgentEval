# Troubleshooting MAF Workflow Integration

> **Status**: Resolved — February 2026  
> **Applies to**: `Microsoft.Agents.AI.Workflows` v1.0.0-preview.251110.2+  
> **Related files**: `MAFWorkflowEventBridge.cs`, `MAFWorkflowAdapter.cs`, `MAFGraphExtractor.cs`

---

## Symptom

When running a real MAF workflow through `MAFWorkflowAdapter.FromMAFWorkflow()`, the harness reports:

- **0 steps** in the execution timeline
- **~1 second duration** (should be 30–90s for multi-agent LLM workflows)
- **Graph extraction works correctly** (correct nodes, edges, entry/exit)
- **No errors** reported — the workflow "succeeds" silently with empty results
- All workflow assertions fail: *"Expected 4 steps but found 0"*

---

## Root Cause Analysis

Two bugs caused this behavior, both related to how MAF's **ChatProtocol** works internally.

### Bug 1: String Input Silently Dropped

**The problem**: The bridge sent input as a plain `string` via `StreamAsync<string>(workflow, input)`.

**Why it fails**: `ChatClientAgent` bound via `BindAsExecutor()` creates an `AIAgentHostExecutor`, which extends `ChatProtocolExecutor`. This executor registers handlers for:

| Message Type | Handled? | Notes |
|---|---|---|
| `ChatMessage` | ✅ Yes | Primary input type |
| `List<ChatMessage>` | ✅ Yes | Bulk messages |
| `TurnToken` | ✅ Yes | Triggers processing |
| `string` | ❌ **No** | Silently dropped |

The key is that `AIAgentHostExecutor` does **not** pass `StringMessageChatRole` to `ChatProtocolExecutor`, so no `string` handler is registered. When a `string` is sent, the executor has no matching route and silently ignores it.

**Contrast with function-based executors**: `Func<string, ValueTask<string>>` bound via `.BindAsExecutor<string, string>()` creates a simple `IMessageHandler<string, string>` that handles `string` directly — no ChatProtocol involved.

**Fix**: Send input as `ChatMessage(ChatRole.User, input)` instead of plain `string`.

### Bug 2: Missing TurnToken

**The problem**: Even with `ChatMessage` input, the executor accumulated the message but never processed it.

**Why it fails**: `ChatProtocolExecutor` uses a **two-phase protocol**:

1. **Accumulation phase**: Incoming `ChatMessage`s are added to a pending list
2. **Processing phase**: A `TurnToken` triggers `TakeTurnAsync()`, which processes all accumulated messages through the LLM

```
Phase 1:  ChatMessage → executor.AddMessageAsync() → pendingMessages.Add(msg)
Phase 2:  TurnToken   → executor.TakeTurnAsync()   → call LLM → emit response → forward TurnToken
```

`InProcessExecution.StreamAsync<T>()` enqueues the input message but does **NOT** send a `TurnToken`. Only `InProcessExecution.RunAsync<T>()` does this automatically (via internal `BeginRunHandlingChatProtocolAsync`).

**Fix**: After `StreamAsync`, send `TrySendMessageAsync(new TurnToken(emitEvents: true))`.

### Why the Workflow Completed in ~1 Second

Without a `TurnToken`, the first executor (Planner) received the input `ChatMessage` but never processed it. No LLM call was made. With no work to do, the workflow's superstep loop found no pending messages and halted. The stream completed with lifecycle events (`WorkflowStartedEvent`, `SuperStepStartedEvent`, `SuperStepCompletedEvent`) but zero executor events.

### Why Tests All Passed

Unit tests used **function-based executors** (`Func<string, ValueTask<string>>`), which:
- Handle `string` input directly (no `ChatProtocol`)
- Don't require a `TurnToken`
- Process immediately upon receiving a message

This made the test suite completely green while the real workflow (using `ChatClientAgent` → `AIAgentHostExecutor` → `ChatProtocolExecutor`) was silently failing.

---

## The Official MAF Pattern

The [official MAF sample](https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/GettingStarted/Workflows/_Foundational/03_AgentsInWorkflows) shows the correct pattern:

```csharp
// 1. Create agents and build workflow
AIAgent frenchAgent = GetTranslationAgent("French", chatClient);
AIAgent spanishAgent = GetTranslationAgent("Spanish", chatClient);
var workflow = new WorkflowBuilder(frenchAgent)
    .AddEdge(frenchAgent, spanishAgent)
    .Build();

// 2. Start streaming with ChatMessage input (NOT string)
await using StreamingRun run = await InProcessExecution
    .StreamAsync(workflow, new ChatMessage(ChatRole.User, "Hello World!"));

// 3. Send TurnToken to trigger agent processing
await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

// 4. Watch events
await foreach (WorkflowEvent evt in run.WatchStreamAsync())
{
    // Process events...
}
```

Key points:
- Input is `new ChatMessage(ChatRole.User, ...)`, not a plain string
- `TurnToken(emitEvents: true)` must be sent manually after `StreamAsync`
- `emitEvents: true` on the `TurnToken` overrides the executor's binding default

---

## The Fix in AgentEval

The fix in `MAFWorkflowEventBridge.StreamAsAgentEvalEvents()` uses **protocol detection** to handle both AIAgent-based and function-based workflows:

```csharp
// Detect workflow protocol type
var protocol = await workflow.DescribeProtocolAsync(cancellationToken);
bool isChatProtocol = ChatProtocolExtensions.IsChatProtocol(protocol);

if (isChatProtocol)
{
    // AIAgent executors: send ChatMessage + TurnToken
    run = await InProcessExecution
        .StreamAsync(workflow, new ChatMessage(ChatRole.User, input), ...);
    await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
}
else
{
    // Function-based executors: send string directly
    run = await InProcessExecution
        .StreamAsync<string>(workflow, input, ...);
}
```

This mirrors MAF's own `BeginRunHandlingChatProtocolAsync` logic, which checks `IsChatProtocol()` before deciding whether to append a `TurnToken`.

---

## MAF Executor Type Comparison

| Property | `AIAgentHostExecutor` | `AgentRunStreamingExecutor` | Function-based |
|---|---|---|---|
| Created by | `agent.BindAsExecutor()` | `AgentWorkflowBuilder.BuildSequential()` | `func.BindAsExecutor<T,U>()` |
| Base class | `ChatProtocolExecutor` | `ChatProtocolExecutor` | Direct `IMessageHandler` |
| Handles `string`? | ❌ No | ✅ Yes (`StringMessageChatRole = User`) | ✅ Yes (native) |
| Handles `ChatMessage`? | ✅ Yes | ✅ Yes | ❌ No |
| Requires `TurnToken`? | ✅ Yes | ✅ Yes | ❌ No |
| `emitEvents` default | From binding param | Always true | N/A |
| Thread/memory | ✅ `AgentThread` | ❌ None | ❌ None |
| Checkpointing | ✅ Yes | ❌ No | ❌ No |

---

## Diagnostic Checklist

When a MAF workflow produces 0 events through AgentEval, check:

1. **Check execution duration**: If ~1 second for multi-agent workflows → likely no LLM calls happening
2. **Check graph extraction**: If graph is correct (correct nodes/edges) → the workflow structure is fine; the issue is execution
3. **Verify input type**: Is the bridge sending `ChatMessage` for ChatProtocol workflows?
4. **Verify TurnToken**: Is `TrySendMessageAsync(new TurnToken(emitEvents: true))` called after `StreamAsync`?
5. **Run standalone**: Execute the workflow outside AgentEval (see `StandaloneWorkflowTest.cs`) to isolate bridge issues from workflow issues
6. **Check protocol type**: Call `workflow.DescribeProtocolAsync()` and `IsChatProtocol()` to confirm what the workflow expects
7. **Check Azure credentials**: Ensure `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_API_KEY`, and `AZURE_OPENAI_DEPLOYMENT` are set
8. **Add event logging**: Temporarily log every event type from `WatchStreamAsync()` to see what MAF emits

---

## Event Flow Reference

For a successfully executing 2-agent ChatProtocol workflow (Translator → Summarizer), the event stream looks like:

```
[001] SuperStepStartedEvent
[002] ExecutorInvokedEvent: Translator_abc123      ← message receipt
[003] ExecutorCompletedEvent: Translator_abc123     ← message routing complete
[004] ExecutorInvokedEvent: Translator_abc123       ← TurnToken processing
[005] AgentRunUpdateEvent: "Bonjour"                ← streaming LLM token
[006] AgentRunUpdateEvent: ", comment"              ← streaming LLM token
[007] AgentRunUpdateEvent: " vas-tu"                ← streaming LLM token
...
[017] ExecutorCompletedEvent: Translator_abc123     ← LLM response complete
[018] SuperStepCompletedEvent
[019] SuperStepStartedEvent
[020] ExecutorInvokedEvent: Summarizer_def456       ← next executor receives messages + TurnToken
...
[042] ExecutorCompletedEvent: Summarizer_def456
[043] SuperStepCompletedEvent
```

Note: Each executor is invoked **twice** — once for message receipt (quick), once for TurnToken processing (LLM call). Only the TurnToken phase emits `AgentRunUpdateEvent` streaming tokens.

---

## Key Learnings

1. **MAF's ChatProtocol is a two-phase protocol** — messages are accumulated, then processed on `TurnToken`. This is not obvious from the API surface.

2. **`StreamAsync<T>` is NOT equivalent to `RunAsync<T>`** — `RunAsync` auto-handles ChatProtocol (sends `TurnToken`), `StreamAsync` does not.

3. **Silent message dropping** — When an executor has no handler for a message type, it's silently dropped. No error, no warning, no event.

4. **Test coverage gap** — Function-based executors don't exercise the ChatProtocol path. Always include integration tests with real `ChatClientAgent` executors if targeting real workflows.

5. **Protocol detection** (`DescribeProtocolAsync()` + `IsChatProtocol()`) is the correct way to adapt behavior, mirroring MAF's own internal pattern.
