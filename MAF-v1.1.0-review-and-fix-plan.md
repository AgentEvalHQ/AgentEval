# MAF v1.1.0 — Review & Fix Plan

> **Generated:** April 2026  
> **Updated:** Applied all non-deferred tasks ✅  
> **Scope:** All MAF-referencing code in AgentEval — `src/AgentEval.MAF/`, `samples/AgentEval.Samples/`, `samples/AgentEval.NuGetConsumer/`  
> **Reference:** [docs/maf-1.1.0-migration-guide.md](docs/maf-1.1.0-migration-guide.md) — Best Practices section  

---

## Summary

| Severity | Count |
|----------|-------|
| 🔴 Bug | 2 |
| 🟠 Best Practice Violation | 9 |
| 🟡 Missing Feature / Gap | 5 |
| 🔵 Improvement | 3 |
| ⏳ Deferred | 2 |
| **Total** | **21** |

---

## 🔴 BUGS

### ✅ TASK-01: `MAFIdentifiableAgentAdapter` — Session not persisted across calls (BUG)

**File:** [src/AgentEval.MAF/MAF/MAFIdentifiableAgentAdapter.cs](src/AgentEval.MAF/MAF/MAFIdentifiableAgentAdapter.cs)

**Problem:** Both `InvokeAsync()` (line ~56) and `InvokeStreamingAsync()` (line ~83) use:
```csharp
var session = _session ?? await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
```
This creates a **local variable** — the newly-created session is never stored back in `_session`. On the next call, `_session` is still null, so a **new fresh session** is created every time. This means multi-turn conversations lose all context.

**Compare:** `MAFAgentAdapter` correctly uses `_session ??= await _agent.CreateSessionAsync(...)` which persists the session.

**Fix:**  
Change both occurrences from:
```csharp
var session = _session ?? await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
```
to:
```csharp
_session ??= await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
```
Then use `_session` instead of `session` in `RunAsync`/`RunStreamingAsync` calls.

**Impact:** Model comparison tests using multi-turn conversations will silently lose context between turns.

---

### ✅ TASK-02: `MAFIdentifiableAgentAdapter` — Does not implement `ISessionResettableAgent` or `IHistoryInjectableAgent`

**File:** [src/AgentEval.MAF/MAF/MAFIdentifiableAgentAdapter.cs](src/AgentEval.MAF/MAF/MAFIdentifiableAgentAdapter.cs)

**Problem:** `MAFAgentAdapter` implements `IStreamableAgent`, `ISessionResettableAgent`, and `IHistoryInjectableAgent`. But `MAFIdentifiableAgentAdapter` only implements `IStreamableAgent` and `IModelIdentifiable`. It has `ResetSessionAsync()` and `CreateSessionAsync()` methods but does **not** declare the `ISessionResettableAgent` interface, so consumers cannot discover these capabilities through interface checks. It also lacks `InjectConversationHistory()` entirely.

**Fix:**
1. Add `ISessionResettableAgent` to the interface list
2. Add `IHistoryInjectableAgent` with same `InjectConversationHistory()` + `BuildMessages()` pattern as `MAFAgentAdapter`
3. Add internal `_injectedHistory` list matching `MAFAgentAdapter`

**Impact:** Consumers cannot use model-identifiable adapters in conversation evaluation or history injection scenarios.

---

## 🟠 BEST PRACTICE VIOLATIONS

### ✅ TASK-03: Samples use `new ChatClientAgent()` instead of `.AsAIAgent()` (Best Practice #1)

**Files affected (20+ occurrences):**
- `samples/AgentEval.Samples/WorkflowsAndConversations/01_ConversationEvaluation.cs` (line 158)
- `samples/AgentEval.Samples/WorkflowsAndConversations/02_WorkflowEvaluationReal.cs` (lines 259, 276, 293, 311)
- `samples/AgentEval.Samples/WorkflowsAndConversations/03_WorkflowWithTools.cs` (lines 322, 347, 372, 396)
- `samples/AgentEval.Samples/SafetyAndSecurity/01_PolicySafetyEvaluation.cs` (line 268)
- `samples/AgentEval.Samples/SafetyAndSecurity/03_RedTeamAdvanced.cs` (line 295)
- `samples/AgentEval.Samples/PerformanceAndStatistics/02_StochasticEvaluation.cs` (line 128)
- `samples/AgentEval.Samples/PerformanceAndStatistics/03_ModelComparison.cs` (line 239)
- `samples/AgentEval.Samples/PerformanceAndStatistics/04_CombinedStochasticComparison.cs` (line 198)
- `samples/AgentEval.Samples/PerformanceAndStatistics/05_StreamingVsAsyncPerformance.cs` (line 95)
- `samples/AgentEval.Samples/DataAndInfrastructure/01_SnapshotTesting.cs` (line 275)
- `samples/AgentEval.Samples/DataAndInfrastructure/03_TraceRecordReplay.cs` (lines 162, 169, 306)
- `samples/AgentEval.Samples/DataAndInfrastructure/04_BenchmarkSystem.cs` (line 153)
- `samples/AgentEval.Samples/MemoryEvaluation/09_MemoryAIContextProvider.cs` (line 85)

> **Note:** `samples/AgentEval.NuGetConsumer/AgentFactory.cs` (lines 31, 69, 105) also uses `new ChatClientAgent()` — see deferred TASK-D1.

**Best Practice violated:** #1 — "Use `AsAIAgent()` Extensions Over Manual Construction"

**Current pattern:**
```csharp
return new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "MyAgent",
    ChatOptions = new() { Instructions = "..." }
});
```

**Recommended pattern:**
```csharp
return chatClient.AsAIAgent(
    name: "MyAgent",
    instructions: "...");
```

**Note:** `new ChatClientAgent(chatClient, options)` is valid when `ChatClientAgentOptions`-specific features are needed (e.g., `AIContextProviders`, `ChatHistoryProvider`). For those cases, add a code comment explaining why. For simple cases (name + instructions + tools only), prefer `.AsAIAgent()`.

**Fix:** Convert simple `new ChatClientAgent()` calls to `.AsAIAgent()` where no options-specific features are used. Keep `new ChatClientAgent()` for `09_MemoryAIContextProvider.cs` (uses `AIContextProviders`) and workflow agents (may need `Description`).

---

### ✅ TASK-04: No samples demonstrate `InMemoryChatHistoryProvider` (Best Practice #4)

**Files affected:** All samples

**Problem:** Best Practice #4 recommends implementing `ChatHistoryProvider` for conversation storage. No sample demonstrates explicit `InMemoryChatHistoryProvider` usage or custom `ChatHistoryProvider`. The sole conversation sample (`01_ConversationEvaluation.cs`) relies on `MAFAgentAdapter`'s manual session management rather than the MAF pipeline.

**Fix:** Add `InMemoryChatHistoryProvider` to the conversation evaluation sample or create a new sample demonstrating:
```csharp
var agent = chatClient.AsAIAgent(new ChatClientAgentOptions
{
    Name = "ConversationAgent",
    ChatOptions = new() { Instructions = "..." },
    ChatHistoryProvider = new InMemoryChatHistoryProvider(
        new InMemoryChatHistoryProviderOptions
        {
            ChatReducer = new MessageCountingChatReducer(20)
        })
});
```

---

### ✅ TASK-05: No samples demonstrate middleware pattern (Best Practice #5 & #10)

**Files affected:** All samples

**Problem:** Best Practices #5 (pipeline architecture) and #10 (provide both streaming and non-streaming middleware) are not demonstrated in any sample. No sample uses `.AsBuilder().Use(...)`.

**Fix:** Add a middleware demonstration — either a new sample or augment an existing one (e.g., the policy/safety sample `E1`). Show:
```csharp
var agentWithMiddleware = agent.AsBuilder()
    .Use(
        runFunc: GuardrailMiddleware,
        runStreamingFunc: GuardrailStreamingMiddleware)
    .Build();
```

---

### ✅ TASK-06: No samples demonstrate OpenTelemetry observability (Best Practice #6)

**Files affected:** All samples

**Problem:** Best Practice #6 recommends enabling OpenTelemetry. Zero samples call `UseOpenTelemetry()` or `WithOpenTelemetry()`. The performance profiling sample (`D1`) would be the natural place for this.

**Fix:** Add OpenTelemetry setup to the performance profiling sample or create a dedicated observability sample:
```csharp
var tracedAgent = agent.WithOpenTelemetry(sourceName: "AgentEval.Samples");
```

---

### ✅ TASK-07: No samples demonstrate structured output `RunAsync<T>()` (Best Practice #7)

**Files affected:** All samples

**Problem:** Best Practice #7 recommends `RunAsync<T>()` for type-safe responses. No sample uses this pattern.

**Fix:** Create a sample or augment an existing one showing:
```csharp
var response = await agent.RunAsync<WeatherReport>("What's the weather in Paris?");
Console.WriteLine(response.Result.Temperature);
```

---

### ✅ TASK-08: No samples demonstrate `ApprovalRequiredAIFunction` (Best Practice #8)

**Files affected:** `samples/AgentEval.Samples/SafetyAndSecurity/01_PolicySafetyEvaluation.cs`

**Problem:** Best Practice #8 recommends wrapping sensitive tools with `ApprovalRequiredAIFunction`. The `01_PolicySafetyEvaluation.cs` sample registers dangerous tools (`TransferFunds`, `DeleteAllData`) directly without wrapping them. The `NeverCallTool` assertion catches violations after the fact, but MAF's native approval mechanism isn't used.

**Fix:** Wrap sensitive tools:
```csharp
Tools =
[
    AIFunctionFactory.Create(ValidateIdentity),
    AIFunctionFactory.Create(CheckBalance),
    new ApprovalRequiredAIFunction(AIFunctionFactory.Create(TransferFunds)),
    // DeleteAllData should never be available — remove from tools
]
```

---

### ✅ TASK-09: No samples demonstrate compaction (Best Practice #9)

**Files affected:** All samples

**Problem:** Best Practice #9 recommends compaction for long conversations. No sample demonstrates `CompactionProvider` or `PipelineCompactionStrategy`.

**Fix:** Augment the session lifecycle sample (`A6`) or conversation evaluation sample (`C1`) with compaction:
```csharp
// Use pipeline compaction for long conversations
var compaction = new PipelineCompactionStrategy(
    new ToolResultCompactionStrategy(),
    new SummarizationCompactionStrategy(chatClient),
    new SlidingWindowCompactionStrategy(maxMessages: 50));
```

---

### ✅ TASK-10: No samples demonstrate session serialization/persistence (Best Practice #3)

**Files affected:** All samples — specifically `06_AgentSessionLifecycle.cs`

**Problem:** The session lifecycle sample (`A6`) demonstrates session creation and reset, but does **not** demonstrate `SerializeSessionAsync()` / `DeserializeSessionAsync()` for persistence. Best Practice #3 specifically mentions serialization.

**Fix:** Add a step to `06_AgentSessionLifecycle.cs`:
```csharp
// Serialize session for persistence
JsonElement serialized = await agent.SerializeSessionAsync(session);
Console.WriteLine($"Session serialized: {serialized.GetRawText().Length} bytes");

// Restore session
AgentSession restored = await agent.DeserializeSessionAsync(serialized);
var response = await agent.RunAsync("What was my name?", restored);
```

---

### ✅ TASK-11: `MAFAgentAdapter` — Does not expose session serialization

**File:** [src/AgentEval.MAF/MAF/MAFAgentAdapter.cs](src/AgentEval.MAF/MAF/MAFAgentAdapter.cs)

**Problem:** `MAFAgentAdapter` wraps `AIAgent` but does not expose `SerializeSessionAsync()` or `DeserializeSessionAsync()`. Users who need to persist sessions for replay/CI testing cannot do so through the adapter.

**Fix:** Add serialization methods:
```csharp
public async Task<JsonElement> SerializeSessionAsync(CancellationToken ct = default)
{
    if (_session is null) throw new InvalidOperationException("No active session.");
    return await _agent.SerializeSessionAsync(_session, ct).ConfigureAwait(false);
}

public async Task RestoreSessionAsync(JsonElement state, CancellationToken ct = default)
{
    _session = await _agent.DeserializeSessionAsync(state, ct).ConfigureAwait(false);
}
```

---

## 🟡 MISSING FEATURES / GAPS

### ✅ TASK-12: `AgentEval.MAF.csproj` — Missing `Microsoft.Agents.AI.OpenAI` package reference

**File:** [src/AgentEval.MAF/AgentEval.MAF.csproj](src/AgentEval.MAF/AgentEval.MAF.csproj)

**Problem:** The MAF project references `Microsoft.Agents.AI` and `Microsoft.Agents.AI.Workflows` but not `Microsoft.Agents.AI.OpenAI`. The `.AsAIAgent()` extension method on `AzureOpenAIClient` requires this package. While consumers add it themselves, the MAF layer could provide convenience for OpenAI scenarios.

**Assessment:** This may be intentional (keep the core MAF layer provider-agnostic). If intentional, add a comment explaining why. If not, add the reference.

**Fix:** Either:
- Add `<PackageReference Include="Microsoft.Agents.AI.OpenAI" />` to the MAF project, or
- Add a code comment in the .csproj explaining the deliberate omission

---

### ✅ TASK-13: No adapter support for `AgentResponse.ContinuationToken`, `FinishReason`, `CreatedAt`

**File:** [src/AgentEval.MAF/MAF/MAFAgentAdapter.cs](src/AgentEval.MAF/MAF/MAFAgentAdapter.cs)

**Problem:** `AgentResponse` from MAF includes `ContinuationToken`, `FinishReason`, `CreatedAt`, and `AdditionalProperties` (confirmed in MAFVnext source). The adapter only extracts `Text`, `Messages`, and `Usage`. The `ContinuationToken` is important for background agent responses, and `FinishReason` is useful for evaluation (e.g., detecting `MaxTokens` truncation vs `Stop`).

**Fix:** Add these to AgentEval's `AgentResponse` model and extract them in the adapter:
```csharp
return new AgentResponse
{
    Text = response.Text,
    RawMessages = response.Messages.ToList(),
    TokenUsage = tokenUsage,
    FinishReason = response.FinishReason?.ToString(),
    // ContinuationToken for background responses
    AdditionalProperties = new Dictionary<string, object?>
    {
        ["ContinuationToken"] = response.ContinuationToken,
        ["CreatedAt"] = response.CreatedAt,
    }
};
```

---

### ✅ TASK-14: Large code duplication between `MAFAgentAdapter` and `MAFIdentifiableAgentAdapter`

**Files:**
- [src/AgentEval.MAF/MAF/MAFAgentAdapter.cs](src/AgentEval.MAF/MAF/MAFAgentAdapter.cs)
- [src/AgentEval.MAF/MAF/MAFIdentifiableAgentAdapter.cs](src/AgentEval.MAF/MAF/MAFIdentifiableAgentAdapter.cs)

**Problem:** Both adapters contain nearly identical code for:
- Session creation/management
- Token usage extraction from `AgentResponse.Usage`
- Streaming chunk processing (`TextContent`, `FunctionCallContent`, `FunctionResultContent`, `UsageContent`)
- `ResetSessionAsync()` / `CreateSessionAsync()`

This violates DRY. When TASK-01 is fixed and TASK-13 features are added, both files need identical changes.

**Fix:** Extract a shared base class or composition helper:
```csharp
internal class MAFAgentCore(AIAgent agent)
{
    // Shared: session management, token extraction, streaming processing
}
```
Then both adapters delegate to this core. Alternatively, make `MAFIdentifiableAgentAdapter` extend `MAFAgentAdapter` (adding `IModelIdentifiable`).

---

### ✅ TASK-15: `MAFWorkflowEventBridge` — Potential tool call correlation failure for concurrent calls

**File:** [src/AgentEval.MAF/MAF/MAFWorkflowEventBridge.cs](src/AgentEval.MAF/MAF/MAFWorkflowEventBridge.cs)

**Problem:** `pendingToolCalls` tracks tools by `CallId`. If `FunctionCallContent.CallId` is null (which the code handles with `Guid.NewGuid()`), the fabricated ID won't match the corresponding `FunctionResultContent.CallId` (also null → empty string). This means:
```csharp
// Tool call: CallId = null → stored as Guid.NewGuid()
pendingToolCalls["<random-guid>"] = (...)

// Tool result: CallId = null → lookupKey = ""
pendingToolCalls.TryGetValue("", out var pending)  // MISS!
```

**Fix:** Synchronize the null-handling between call and result:
```csharp
// For calls with no CallId, use a deterministic fallback (e.g., tool name + order)
var callId = call.CallId ?? $"__auto_{call.Name}_{pendingToolCalls.Count}";
```
And on the result side, attempt to match unmatched results to any single pending call by name.

---

### ✅ TASK-16: No sample demonstrates `agent.AsAIFunction()` — agent-as-tool composition

**Files affected:** All samples

**Problem:** The migration guide section 6 documents `agent.AsAIFunction()` for agent-as-tool composition. No sample demonstrates this pattern, which is a key MAF 1.1.0 feature for multi-agent systems.

**Fix:** Add a sample showing an orchestrator agent delegating to specialists:
```csharp
var weatherAgent = chatClient.AsAIAgent(name: "WeatherAgent", ...);
var orchestrator = chatClient.AsAIAgent(
    name: "Orchestrator",
    tools: [weatherAgent.AsAIFunction()]);
```

---

## 🔵 IMPROVEMENTS

### ✅ TASK-17: Inconsistent agent naming pattern across samples

**Problem:** Some samples use `Name` property on `ChatClientAgentOptions`, others pass `name:` to `.AsAIAgent()`. Some use descriptive names ("TravelBookingAgent"), others use generic names ("Calculator Agent (gpt-4o)"). Agents in workflows have both `Name` and `Description`, but standalone agents rarely set `Description`.

**Fix:** Standardize naming:
- Always set both `Name` and `Description` on agents
- Use PascalCase identifiers for agent names (e.g., "WeatherAgent" not "Weather Agent")
- In model comparison, include deployment in description not name

---

### ✅ TASK-18: `ConversationExtractor.ExtractToolUsage()` — Light path tool correlation is fragile

**File:** [src/AgentEval.MAF/Evaluators/ConversationExtractor.cs](src/AgentEval.MAF/Evaluators/ConversationExtractor.cs)

**Problem:** The light path extracts tool usage from conversation transcripts without real-time streaming data. Tool call-result correlation falls back to "most-recent pending call" heuristic when `CallId` is missing. This may mis-correlate in concurrent scenarios. The code documents this limitation but doesn't provide a warning or diagnostic.

**Fix:** Add a diagnostic warning when heuristic correlation is used:
```csharp
// Log diagnostic: "Tool result correlated by heuristic — CallId was null"
```
Also consider adding a `CorrelationConfidence` field to the tool usage report (High = CallId match, Low = heuristic).

---

### ✅ TASK-19: Add production credential guidance comment to `AIConfig.cs`

**File:** [samples/AgentEval.Samples/AIConfig.cs](samples/AgentEval.Samples/AIConfig.cs)

**Problem:** Per Best Practice #2, `DefaultAzureCredential` should be avoided in production. The samples correctly use `AzureKeyCredential` (explicit API keys), which is fine for samples/dev. However, there's no guidance comment directing users toward `ManagedIdentityCredential` for production.

**Fix:** Add a comment in `AIConfig.cs`:
```csharp
/// <remarks>
/// This sample uses AzureKeyCredential (API key) for simplicity.
/// For production, use ManagedIdentityCredential or another specific TokenCredential.
/// See: https://learn.microsoft.com/en-us/agent-framework/agents/?pivots=programming-language-csharp#azure-and-openai-sdk-options-reference
/// </remarks>
```

---

## ⏳ DEFERRED (Blocked on NuGet package publish)

> These tasks apply to `samples/AgentEval.NuGetConsumer/` which consumes the **published NuGet package** (not project references). They cannot be completed until a new AgentEval NuGet package version is published with the MAF 1.1.0-compatible API surface.

### TASK-D1: `NuGetConsumer` references `AgentEval 0.6.0-beta` — must be updated after package publish

**File:** [samples/AgentEval.NuGetConsumer/AgentEval.NuGetConsumer.csproj](samples/AgentEval.NuGetConsumer/AgentEval.NuGetConsumer.csproj)

**Problem:** The NuGet consumer sample references `AgentEval 0.6.0-beta`. The comment says "0.6.0-beta published with MAF RC3" — this does not have the MAF 1.1.0-compatible API surface. Any code changes to this project would be against an outdated package and may not compile or behave correctly.

**Blocked on:** Publishing a new AgentEval NuGet package with MAF 1.1.0 support.

**Fix:** Once a new package is published:
1. Update `<PackageReference Include="AgentEval" Version="..." />` to the new version
2. Run `dotnet restore` and `dotnet build` to verify API compatibility
3. Fix any breaking changes from the API surface evolution

---

### TASK-D2: Convert NuGetConsumer `new ChatClientAgent()` to `.AsAIAgent()` (Best Practice #1)

**File:** [samples/AgentEval.NuGetConsumer/AgentFactory.cs](samples/AgentEval.NuGetConsumer/AgentFactory.cs) (lines 31, 69, 105)

**Problem:** All three factory methods (`CreateTravelAgent`, `CreateTravelAIAgent`, `CreateWeatherAgent`) use `new ChatClientAgent(chatClient, new ChatClientAgentOptions { ... })`. Per Best Practice #1, simple agent creation should use `.AsAIAgent()`.

**Blocked on:** TASK-D1 — package must be updated first.

**Fix:** After TASK-D1 is complete:
```csharp
// Before
return new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "TravelAgent",
    ChatOptions = new() { Instructions = "...", Tools = [...] }
});

// After
return chatClient.AsAIAgent(
    name: "TravelAgent",
    instructions: "...",
    tools: [...]);
```

Also address: `CreateTravelAgent()` and `CreateTravelAIAgent()` have nearly identical configuration — consolidate into a single method.

---

## Priority Order

### Phase 1 — Fix Bugs (Critical)
1. **TASK-01** — Fix `MAFIdentifiableAgentAdapter` session persistence
2. **TASK-02** — Add missing interfaces to `MAFIdentifiableAgentAdapter`

### Phase 2 — Best Practice Alignment (High)  
3. **TASK-03** — Convert simple `new ChatClientAgent()` to `.AsAIAgent()` in samples
4. **TASK-10** — Add session serialization demo to `06_AgentSessionLifecycle.cs`
5. **TASK-11** — Expose session serialization in `MAFAgentAdapter`
6. **TASK-14** — Extract shared base/helper to reduce duplication
7. **TASK-15** — Fix null `CallId` correlation in `MAFWorkflowEventBridge`

### Phase 3 — New Sample Coverage (Medium)
8. **TASK-04** — Add `InMemoryChatHistoryProvider` demo
9. **TASK-05** — Add middleware demo
10. **TASK-06** — Add OpenTelemetry demo
11. **TASK-07** — Add structured output `RunAsync<T>()` demo
12. **TASK-08** — Add `ApprovalRequiredAIFunction` demo
13. **TASK-09** — Add compaction demo
14. **TASK-16** — Add agent-as-tool demo

### Phase 4 — Polish (Low)
15. **TASK-12** — Clarify `Microsoft.Agents.AI.OpenAI` omission
16. **TASK-13** — Extract `FinishReason`/`ContinuationToken` from responses
17. **TASK-17** — Standardize agent naming
18. **TASK-18** — Add heuristic correlation diagnostics
19. **TASK-19** — Add production credential guidance comment

### Phase 5 — Deferred (Blocked)
20. **TASK-D1** — Update NuGetConsumer package reference (blocked on NuGet publish)
21. **TASK-D2** — Convert NuGetConsumer `new ChatClientAgent()` to `.AsAIAgent()` (blocked on TASK-D1)
