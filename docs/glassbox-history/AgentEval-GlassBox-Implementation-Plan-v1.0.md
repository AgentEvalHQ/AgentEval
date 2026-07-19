# AgentEval — Glass Box Implementation Plan

### A mechanical, codebase-verified build plan for chat-boundary tracing, Trace Fidelity, the runtime policy gate, and CLI consolidation

| | |
|---|---|
| **Status** | Implementation plan v1.0 — ready to execute. Derived from `AgentEval-GlassBox-Proposal-v0.11.md` (the *what/why*) plus an 11-agent reconnaissance of the live codebase (the *exactly-where/how*). |
| **Audience** | An implementing engineer or model (GPT-5, Sonnet 4.6, Opus, etc.) who has **not** read the codebase. Every type name, file path, namespace, signature, and acceptance check below is verified against the real code as of 2026-05-31. |
| **Source of truth** | This plan overrides the proposal wherever they disagree. §2 lists every correction and *why*. When in doubt, trust this document; it was checked against the binaries. |
| **Date** | 2026-05-31 |
| **Releases** | v0.11: Phases 0–4, 5a, 8, 9, 10. v0.12: Phases 5b, 6. v0.13: Phase 7. |
| **Verification** | Grounded in an 11-agent codebase reconnaissance; all load-bearing MEAI 10.5.0 APIs (`DelegatingChatClient`, `ChatClientBuilder.Use`, `AIFunction.InvokeAsync`, `ChatMessage`/`ChatResponse`/`ChatResponseUpdate`/`FunctionCallContent`/`TextContent`/`UsageDetails` ctors & setters) were confirmed by **reflecting the restored binaries**. Passed two adversarial review rounds (correctness-vs-code, mechanical-followability, completeness, soundness). |
| **Revision** | **v1.1 (2026-05-31)** — second deep audit fixed three interrelated heart-of-the-feature defects the first round missed: **(B)** composition order — the recorder must be **inner** of `UseFunctionInvocation` (proposal §6.1 had it outer → would record 1 entry, not N); **(A/C)** `ToolCorrelationScope` moved to **Core** (Core can't reference MAF) and is **caller-established** (not recorder-set), read by both wrappers; plus `Index`-vs-`CorrelationId` semantics documented. Also: fully specified `EvalGatingChatClient` (was a skeleton), concrete fidelity scoring rubric with a pinned worked example, concrete CLI option wiring, and all-named record constructors. |

---

## ▶ Progress Tracker (live — update after every task & Revision Gate)

> **Legend:** `%` = task completion · `✓` = ☐ not started / ◐ in progress / ✅ done & gated · Each phase closes with a **`N-R` Revision Gate** (§4.5: re-verify all tasks, full build+test, AAA + Moq, DRY/KISS/SOLID/CLEAN, Opus gap review). Branch: `feat/glass-box-v0.11-chat-boundary-tracing`. Baseline: **4372 pass / 1 skip / 0 fail** (net10, `744da5c`).

| Phase | Task | Description | % | ✓ | Notes |
|---|---|---|---|---|---|
| **0** v0.11 | T0.1 | AgentTrace v1.1: `TraceEntryScope`, additive `TraceEntry` fields, `TraceToolDefinition`, factory methods, `Version`→1.1 | 100 | ✅ | Done. `AgentTrace.cs`; Core builds 0/0. |
| 0 | T0.2 | v1.0 back-compat regression test (inline v1.0 JSON — more robust than content-copied fixture) | 100 | ✅ | `AgentTraceV11SchemaTests` — load + round-trip, no v1.1 keys leak. |
| 0 | T0.3 | `ToolDefinitionDeduplicator` (preset-toggled) | 100 | ✅ | `ToolDefinitionDeduplicator.cs` + 5 tests. |
| 0 | T0.4 | `ScriptedChatClient` (tool-calls/finish-reason/usage/streaming/throw) | 100 | ✅ | `ScriptedChatClient.cs` + 6 tests. |
| 0 | **0-R** | Revision Gate (§4.5) | 100 | ✅ | 20 new tests green; **full suite 4392/1/0 net10 — zero regression**; additive-only confirmed; committed+pushed `feat/glass-box-v0.11…`. |
| **1** v0.11 | T1.0 | Re-verify MEAI base types (gate; green) | 0 | ☐ | |
| 1 | T1.1 | `SamplePreset` enum | 0 | ☐ | |
| 1 | T1.2 | `TraceRecordingChatClient : DelegatingChatClient` | 0 | ☐ | |
| 1 | T1.3 | `UseTraceRecording` builder extension | 0 | ☐ | |
| 1 | T1.4 | Tests (incl. 🔴 per-round-trip composition test) | 0 | ☐ | |
| 1 | T1.6 | `ToolCorrelationScope` (Core, caller-established) | 0 | ☐ | |
| 1 | T1.5 | Docs (`tracing.md` two-layer section) | 0 | ☐ | |
| 1 | **1-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **2** v0.11 | T2.2 | `EvaluatingAIFunction : AIFunction` + correlation | 0 | ☐ | |
| 2 | T2.3 | `WithEvaluation` extension | 0 | ☐ | |
| 2 | T2.4 | Tests (AIFunctionFactory-built tools) | 0 | ☐ | |
| 2 | **2-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **3** v0.11 | T3.1 | Six discrepancy classes + pinned scoring rubric | 0 | ☐ | |
| 3 | T3.2 | `TraceFidelityReport` model | 0 | ☐ | |
| 3 | T3.3 | `TraceFidelityRunner` + `EvalResult` emission | 0 | ☐ | |
| 3 | T3.4 | Shape-B family + registration + hosting context | 0 | ☐ | |
| 3 | T3.5 | CLI `bench trace-fidelity` handler + wiring | 0 | ☐ | |
| 3 | T3.6 | Contract-test inclusion | 0 | ☐ | |
| 3 | T3.7 | Reconciliation tests (one per class) | 0 | ☐ | |
| 3 | T3.8 | Docs (`benchmarks/trace-fidelity.md`) | 0 | ☐ | |
| 3 | **3-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **4** v0.11 | T4.1 | `IChatGate`/`GateVerdict`/`EvalGatePolicy`/`EvalGateRefusalException` | 0 | ☐ | |
| 4 | T4.2 | Built-in gates (PII/injection/safety-metric) | 0 | ☐ | |
| 4 | T4.3 | `EvalGatingChatClient` | 0 | ☐ | |
| 4 | T4.4 | `UseEvalGate` builder extension | 0 | ☐ | |
| 4 | T4.5 | Tests | 0 | ☐ | |
| 4 | T4.6 | Docs (`guardrails.md`) | 0 | ☐ | |
| 4 | **4-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **5a** v0.11 | T5a.1 | Workflow pre-wiring docs | 0 | ☐ | |
| 5a | **5a-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **8** v0.11 | T8.1 | `doctor` double-wrapping check | 0 | ☐ | |
| 8 | T8.2 | `migrate` v1.0→v1.1 | 0 | ☐ | |
| 8 | T8.3 | `Observability/01_GlassBoxFullStack` sample | 0 | ☐ | |
| 8 | **8-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **9** v0.11 | T9.1 | Reuse TripPlanner workflow | 0 | ☐ | |
| 9 | T9.2 | Multi-endpoint config + openai-compatible judge wiring | 0 | ☐ | |
| 9 | T9.3 | `bench autoaudit` handler | 0 | ☐ | |
| 9 | T9.4 | Docs (`showcase/auto-audit.md`) | 0 | ☐ | |
| 9 | **9-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **10** v0.11 | T10.1 | Release workflow pack/publish CLI tool | 0 | ☐ | |
| 10 | T10.2 | Docs lead with `dotnet tool install` | 0 | ☐ | |
| 10 | T10.3 | ADR-019 + ADR-020 | 0 | ☐ | |
| 10 | **10-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **6** v0.12 | T6.1 | Compliance per-turn evidence field (Compliance.Core) | 0 | ☐ | |
| 6 | **6-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **5b** v0.12 | T5b.1 | Workflow transparent adapter hook | 0 | ☐ | |
| 5b | **5b-R** | Revision Gate (§4.5) | 0 | ☐ | |
| **7** v0.13 | T7.1 | Mission Control `ChatTurn` GraphQL type + resolver | 0 | ☐ | |
| 7 | T7.2 | Depth bump + SPA per-turn timeline | 0 | ☐ | |
| 7 | **7-R** | Revision Gate (§4.5) | 0 | ☐ | |

*Build order (per §6): 0 → 1 → 2 → 4 → 3 → 5a → 8 → 9 → 10, then 6 → 5b → 7.*

---

## 0. How to use this plan

**This plan is designed to be followed literally. Do not improvise.**

1. **Work phase by phase, in the order given in §6.** Each phase is independently shippable and has a hard dependency list. Do not start a phase until its dependencies show ✅.
2. **Every task has the same shape:** `Goal → Files → Steps (exact) → Tests → Acceptance (DoD)`. A task is **done** only when every Acceptance checkbox is objectively true (the test passes, the file exists, the command prints the expected output). No subjective judgement is permitted.
3. **Read §1 (verified facts) and §2 (corrections) before writing any code.** They prevent the most likely mistakes — wrong namespace, wrong score scale, a type that doesn't exist, a "free" CLI change that isn't.
4. **Use §3 (the canonical v1.1 schema) as the single definition** of every new type and field. All phases reference it; never redefine these inline.
5. **Honour §4 (conventions) in every file.** SPDX header, file-scoped namespace, nullable annotations, XML docs on public members. CI does not treat warnings as errors, but reviewers do — keep it warning-clean.
6. **After each phase, run the full build + test suite** (`dotnet build AgentEval.sln -c Release` then `dotnet test AgentEval.sln -c Release`) and confirm zero new failures before moving on.
7. **Per the project's standing process, an Opus-grade gap review of the actual changed files must follow each implementation batch** before it is considered complete.

> **Notation.** `T<phase>.<n>` = a task. `[ ]` = an objective acceptance check. `📁` = a file you create or edit. `⚠️` = a verified gotcha that will bite you if ignored. Paths are repo-relative to `C:\git\joslat\AgentEval`.

---

## 1. Verified codebase facts (ground truth)

These were confirmed by reading the source and, for the MEAI/MAF base types, by reflecting the restored NuGet binaries (`Microsoft.Extensions.AI 10.5.0`, `Microsoft.Agents.AI 1.3.0`). Treat them as settled.

### 1.1 Platform & build

| Fact | Value |
|---|---|
| Central package mgmt | `Directory.Packages.props`. MEAI **10.5.0**, MEAI.OpenAI 10.5.0, MAF (`Microsoft.Agents.AI*`) **1.3.0**, Hot Chocolate **16.0.0**, xunit 2.9.3, Verify.Xunit 28.8.1. |
| `Directory.Build.props` | `LangVersion=preview`, `ImplicitUsings=enable`, `Nullable=enable`, `TreatWarningsAsErrors=false`, `GenerateDocumentationFile=true`, `NoWarn=CS1591`. |
| `global.json` SDK | `8.0.100`, `rollForward=latestMajor`, `allowPrerelease=true`. |
| Umbrella package | `src/AgentEval/AgentEval.csproj` (`TargetFrameworks=net8.0;net9.0;net10.0`) bundles 11 internal projects via `ProjectReference … PrivateAssets="all"` + an `IncludeSubProjectDlls` target. It is the **only** library NuGet package. All internal projects are `IsPackable=false`, `RootNamespace=AgentEval`. |
| CLI package | `src/AgentEval.Cli/AgentEval.Cli.csproj` — `IsPackable=true`, **already** has `PackAsTool=true`, `ToolCommandName=agenteval`, `PackageId=AgentEval.Cli`; `TargetFrameworks=net8.0;net10.0` (net9 dropped); `PackageVersion` intentionally not hardcoded. |

### 1.2 The MEAI/MAF base types Glass Box depends on (REFLECTED — settled)

| Type | Verified |
|---|---|
| `Microsoft.Extensions.AI.DelegatingChatClient` | **Exists** in `Microsoft.Extensions.AI.Abstractions` 10.5.0. `public`, **not sealed**. `GetResponseAsync(IEnumerable<ChatMessage>, ChatOptions?, CancellationToken)` and `GetStreamingResponseAsync(…)` are **`virtual`** → overridable. Ctor takes `(IChatClient innerClient)`. |
| `Microsoft.Extensions.AI.ChatClientBuilder` | **Exists** in `Microsoft.Extensions.AI` 10.5.0. Has `Use(Func<IChatClient,IChatClient>)`, `Use(Func<IChatClient,IServiceProvider,IChatClient>)`, plus delegate-based middleware overloads. `IChatClient.AsBuilder()` extension exists. |
| `AIFunction` | Abstract, in MEAI Abstractions; base = `AIFunctionDeclaration`. **Only abstract member:** `protected abstract ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)`. `Name`/`Description`/`JsonSchema` are virtual on `AIFunctionDeclaration`; `JsonSerializerOptions`/`UnderlyingMethod` virtual on `AIFunction`. |
| `ChatResponse` | `.Messages : IList<ChatMessage>`, `.Text`, `.ModelId`, `.FinishReason : ChatFinishReason?`, `.Usage : UsageDetails?`. Ctors: `new ChatResponse(ChatMessage)` and `new ChatResponse(IList<ChatMessage>)`. ⚠️ **`.FinishReason`, `.ModelId`, `.Usage` have public setters (settable)** — reflected & confirmed — so the `new ChatResponse(msg){ FinishReason=…, ModelId=…, Usage=… }` object-initializer used in T0.4/T4.3 compiles. |
| `ChatResponseUpdate` (streaming) | `.Role`, `.Text`, `.Contents : IList<AIContent>`, `.FinishReason`, `.ModelId`. |
| `FunctionCallContent` | `: ToolCallContent`. `.Name : string`, `.Arguments : IDictionary<string,object?>?`, `.CallId` (from base), `.Exception`. |
| `FunctionResultContent` | `: ToolResultContent`. `.Result : object?`, `.CallId` (from base), `.Exception`. |
| `UsageDetails` | `.InputTokenCount : long?`, `.OutputTokenCount : long?`, `.TotalTokenCount : long?`. ⚠️ **`long?`**, not `int`. |
| `ChatFinishReason` | `struct` (sealed value type). `.Value : string`. Well-knowns: `ChatFinishReason.Stop`, `.Length`, `.ToolCalls`, `.ContentFilter`. |
| `AIFunctionArguments` | Dictionary-like (`IDictionary<string,object?>`), plus `.Services : IServiceProvider`, `.Context : IDictionary<object,object?>`. |
| `ChatOptions` | `.Temperature : float?`, `.MaxOutputTokens : int?`, `.ResponseFormat`, `.Tools : IList<AITool>?`, `.ModelId`. |

> ⚠️ The earlier `AgentEval-TraceRecordingChatClient-Proposal.md` draft claimed these base types were absent in 10.5.0 and budgeted an ~80 LOC shim. **That is outdated.** The shim is **not** needed. T1.0 re-verifies once more before coding, but the answer is already green.

### 1.3 Trace model (the spine — `AgentEval.Core`, namespace `AgentEval.Tracing`)

📁 `src/AgentEval.Core/Tracing/AgentTrace.cs` — all of:

- `public sealed class AgentTrace` — `Version (="1.0")`, `TraceName`, `CapturedAt`, `AgentName?`, `ModelId?`, `Entries : List<TraceEntry>`, `Performance : TracePerformance?`, `Metadata : Dictionary<string,object>?`. All `[JsonPropertyName]` camelCase.
- `public class TraceEntry` (**not sealed**) — `Type : TraceEntryType`, `Index : int`, `Timestamp : DateTimeOffset`, `Prompt?`, `Text?`, `DurationMs : long?`, `ToolCalls : List<TraceToolCall>?`, `TokenUsage : TraceTokenUsage?`, `StreamingChunks : List<TraceStreamChunk>?`, `IsStreaming : bool?`, `Error : TraceError?`.
- `enum TraceEntryType { Request, Response, ToolCall, StreamChunk }` with `[JsonConverter(typeof(JsonStringEnumConverter))]`.
- `TraceToolCall` (`Name`, `Arguments?`, `Result?`, `StartedAt?`, `DurationMs?`, `Succeeded=true`, `Error?`), `TraceTokenUsage` (`PromptTokens:int`, `CompletionTokens:int`, computed `TotalTokens`), `TraceStreamChunk`, `TraceError` (`Type?`,`Message?`,`StackTrace?`), `TracePerformance`.

📁 `src/AgentEval.Core/Tracing/TraceSerializer.cs` — `static` class. `JsonSerializerOptions{ WriteIndented=true, PropertyNamingPolicy=CamelCase, DefaultIgnoreCondition=WhenWritingNull }`. Reflection-based STJ (**no source-gen context**). Methods: `SerializeAsync`, `SerializeToStringAsync`, `DeserializeAsync`, `DeserializeFromStringAsync`, `LoadFromFileAsync`, `SaveToFileAsync`.

📁 Existing recorders (all concrete, expose `.Trace`; **no `ITraceRecorder` interface exists**): `TraceRecordingAgent` (wraps `IEvaluableAgent`; builds `TraceEntry` instances inline via `new TraceEntry{…}`), `ChatTraceRecorder` (wraps an agent; `AddUserTurnAsync`/`GetResult`→`ChatExecutionResult`/`ToAgentTrace`→hardcodes `Version="1.0"` at line ~244), `WorkflowTraceRecorder` (+ inline `WorkflowTraceSerializer`), `TraceReplayingAgent`, `WorkflowTraceReplayingAgent`.

### 1.4 Eval / benchmark layer (`AgentEval.Abstractions.Evals`, `AgentEval.Core`)

| Type | Shape |
|---|---|
| `EvalResult` (sealed record) | `(EvalMetadata Metric, EvalScore Score, EvalDetails Details, EvalProvenance Provenance, DateTimeOffset EvaluatedAt)` |
| `EvalScore` (record) | `(double Value, int? Ordinal, string Label, bool Passed, double? Threshold, string Severity, double? Confidence)` — ⚠️ **`Value` is 0–1**, not 0–100. |
| `EvalDetails` (record) | `(IReadOnlyDictionary<string,double>? Dimensions, IReadOnlyList<EvalEvidence>? Evidence, IReadOnlyList<string>? Recommendations, IReadOnlyList<EvalResult>? SubResults, string? AggregationStrategy)` — **`SubResults` is the tree**. |
| `EvalMetadata` (record) | `(string Key, string Name, string Category, string Version)` |
| `EvalProvenance` (record) | `(string Type, string? JudgeModel, string? PromptId, string? PromptHash, int? TokensUsed, double EstimatedCost, bool CacheHit)` |
| `EvalEvidence` | `Source`, `Reference`, `Message` |
| `EvalInput` (record) | `(string Query, string? Response, string? Context, string? GroundTruth, IReadOnlyList<ToolCall>? ToolCalls, IReadOnlyList<ToolDefinition>? ToolDefinitions, IReadOnlyList<ExpectedAction>? ExpectedActions, string? SystemMessage, IReadOnlyDictionary<string,object>? Metadata)` |
| `IEval` | `{ string Key, Name, Category, Version; Task<EvalResult> EvaluateAsync(EvalInput, CancellationToken) }` |
| `MetricResult` (class) | `{ required string MetricName; required double Score (0–100); bool Passed; string? Explanation; IDictionary<string,object>? Details; static Pass(...); static Fail(...) }` |
| `IMetric` / `ISafetyMetric` | `IMetric { Name; Description; MetricCategory Categories; decimal? EstimatedCostPerEvaluation; Task<MetricResult> EvaluateAsync(EvaluationContext, CancellationToken) }`. `ISafetyMetric : IMetric` is a marker. |
| `CompositeEval` | `: IEval`, builds the `EvalResult` tree via `EvalDetails.SubResults`. ⚠️ Uses `AsyncLocal<int>` recursion guard. Depth cap is now the shared **`EvalTreeLimits.MaxTreeWalkDepth`** (Abstractions, added by ADR-018/ARC-03) — reference it, not the literal `32`. Core also exposes tree-walk extensions `Leaves()` / `FindByKey(key)` / `CountLeaves()` to reuse. |

⚠️ **`IRunOutputStore` does not exist.** The real types are `IOutputStore` (write) and `IOutputStoreReader` (read), in `src/AgentEval.Abstractions/Output/`. `RunManifest.ContentHash` (SHA-256) is the audit-chain anchor.

### 1.5 Benchmark family registry (`AgentEval.Core.Benchmarks`, ADR-017)

📁 `src/AgentEval.Core/Benchmarks/BenchmarkFamilyRegistry.cs`:

- `public sealed class BenchmarkFamily(string name, string description, CostTier defaultCostTier, IReadOnlyList<BenchmarkPreset> presets, Func<string,IEvaluator?,CompositeEval>? compositeFactory=null, Type? runnerType=null, Func<string,object>? runnerFactory=null, Func<EvalInput,IEvaluator?,CancellationToken,Task<EvalResult>>? evaluateAsync=null, string? docLinkUrl=null, string? owningAssemblyName=null)`.
  - **Shape A** = supply `compositeFactory`. **Shape B** = supply `runnerType` + `runnerFactory` (ctor throws if `runnerFactory` set without `runnerType`).
- `public sealed record BenchmarkPreset(string Name, string Description, CostTier? CostTier = null)`.
- `enum CostTier { Free, Low, Medium, High }`.
- `static class BenchmarkFamilyRegistry { void Register(BenchmarkFamily); BenchmarkFamily? TryGet(string); IReadOnlyList<BenchmarkFamily> All; internal void Reset(); }` — idempotent on same-content re-register, throws on name conflict with different content.

**Shape-B template to copy:** 📁 `src/AgentEval.Memory/External/LongMemEval/LongMemEvalBenchmarkRegistration.cs` — `internal static class …Registration` with `[ModuleInitializer] public static void Register()` (wrapped in `#pragma warning disable CA2255`), plus an `AsyncLocal`-backed `…RunnerHostingContext` to pass inputs into the runner factory. **Shape-A template:** 📁 `src/AgentEval.Evals.Agentic/AgenticBenchmarkRegistration.cs`.

**Contract test:** 📁 `tests/AgentEval.Tests/Benchmarks/BenchmarkNamespaceContractTests.cs` — asserts every public `*Benchmark` factory lives in namespace `AgentEval.Benchmarks`; supporting runner/result types are listed in a `DomainTypeExceptions` set.

### 1.6 CLI (`src/AgentEval.Cli`)

- `Program.cs` registers commands via **System.CommandLine** (`new Command(...)`, `.Add(option)`, `.SetAction(...)`). Commands: `init`/`doctor`/`migrate`/`bench`/`compliance`/`render`/`mc`.
- `Commands/BenchListCommand.cs` — `bench --list` calls `AnchorAssemblies()` (a **hardcoded** list of `_ = typeof(SomeFamily).Assembly;` touches to force `[ModuleInitializer]`), then enumerates `BenchmarkFamilyRegistry.All`.
- `Infrastructure/EndpointFactory.cs` — `CreateOpenAICompatible(string endpoint, string model, string? apiKey)` and `CreateAzure(string? endpoint, string deploymentName, string? apiKey)` **already exist**; used by `eval`/`redteam` commands via `--endpoint/--model/--api-key`.
- `Commands/JudgeFactory.cs` — `Resolve(IEvaluator? override, string judgeKind, string? systemPrompt)` resolves the judge from `AZURE_OPENAI_*` env vars (Azure-only today); stub via `AGENTEVAL_ALLOW_STUB_JUDGE=1`.
- `Commands/DoctorCommand.cs` — `RunAsync()` runs five checks (solution.json schema, subject name, run hash, compliance audit chain, legacy paths). **No double-wrapping check yet.**

### 1.7 Mission Control (`src/AgentEval.MissionControl` + `…Spa`)

- `McHost.cs` — Hot Chocolate 16, `AddGraphQLServer().AddQueryType<Query>()`; **convention discovery** (no explicit `ObjectType<…>`); `MaxExecutionDepth = 10` (~line 100). REST under `/api/v1/*`; `BinaryEndpoints.cs` maps `/api/v1/runs/{runId}/trace`.
- `GraphQL/Query.cs` — `ScenarioTree(runId, scenarioId)` resolver (~line 581) returns `EvalResult?` via `EvalResultPersistence`.
- SPA: `src/lib/eval-tree.ts` (hand-written TS interfaces — **no codegen yet**), `components/EvalResultNode.tsx` (recursive), `pages/ScenarioTreePage.tsx` (hand-written `SCENARIO_TREE_QUERY`), `pages/TraceWaterfallPage.tsx` (renders `AgentTrace` from the REST `/trace` endpoint).

> 💡 Because `TraceWaterfallPage` already renders `AgentTrace.Entries` from REST, **chat-turn entries appear in the trace waterfall for free after Phase 1** — no Mission Control change required. Phase 7 (the eval-tree-integrated per-turn view) is genuinely additive on top of that.

### 1.8 Guardrails, policies, safety evaluators

- `src/AgentEval.Core/Assertions/`: `ToolUsageAssertions` (`NeverCallTool`, `NeverPassArgumentMatching`, `MustConfirmBefore` — **post-hoc** fluent assertions on `ToolUsageReport`), `BehavioralPolicyViolationException : AgentEvalAssertionException` (`PolicyName`, `ViolationType` (string: `ForbiddenTool|SensitiveData|MissingConfirmation|RegexTimeout`), `ViolatingAction`, `ForbiddenToolName?`, `MatchedPattern?`, `RedactedValue?`, `ArgumentName?`, `ToolName?`, `Because?`, `Suggestions?`; static `Create(...)`).
- `src/AgentEval.Abstractions/Models/ToolUsageReport.cs`: `Calls`, `Count`, `ToolNames`, `WasToolCalled`, `GetCallsByName`, …
- Safety: `ToxicityMetric`, `BiasMetric` (`: ISafetyMetric`, `EvaluateAsync(EvaluationContext)→MetricResult`). Red-team: `PIIDetectionEvaluator : IProbeEvaluator` (regexes for Email/Phone_US/SSN/CreditCard/IP_Address) and `ContainsTokenEvaluator` (injection), `EvaluateAsync(AttackProbe, string response, CT)→EvaluationResult`.
- ⚠️ **`AgentEval.Guardrails` namespace does not exist.** **No unified pre/post gate-evaluator interface exists.** `IMetric` (post-run scoring, `EvaluationContext`) and `IProbeEvaluator` (red-team, `AttackProbe`) are different shapes — neither is a drop-in gate evaluator.

### 1.9 Samples & tests

- Grouped samples: `samples/AgentEval.Samples/<Theme>/NN_Name.cs`, each a `static class` with `RunAsync()` wired into `Program.cs`'s menu. Themes today: GettingStarted, SafetyAndSecurity, Benchmarks, MetricsAndQuality, PerformanceAndStatistics, MemoryEvaluation, DataAndInfrastructure, WorkflowsAndConversations.
- TripPlanner workflow: 📁 `samples/AgentEval.TravelDemo/Workflows/TripPlannerWorkflow.cs` → `public static (Workflow Workflow, string[] ExecutorIds) Create()`, executor IDs `["TripPlanner","FlightReservation","HotelReservation","Presenter"]`.
- `FakeChatClient` (📁 `src/AgentEval.Core/Testing/FakeChatClient.cs`): `IChatClient` returning queued **plain-text** responses; `WithResponse(string)`, `ReceivedMessages`, `CallCount`, `ThrowOnNextCall`. ⚠️ `GetStreamingResponseAsync` **throws `NotImplementedException`**; **cannot script tool calls, finish reasons, or usage.** Insufficient for Phase 1/3 acceptance tests → Phase 0 adds `ScriptedChatClient`.
- Tests: xunit + FluentAssertions (`.Should()`), Verify.Xunit for snapshots. Test projects `tests/AgentEval.Tests` and `tests/AgentEval.Memory.Tests`.

---

## 2. Corrections to the proposal (read before coding)

The proposal is directionally right but contains inaccuracies that would cause a literal implementer to fail. Each correction below is **binding**.

| # | Proposal says | Reality / what to do instead |
|---|---|---|
| C1 | MEAI 10.5.0 base types "settled, verify locally" | **Confirmed present** (reflected). Keep the one-command re-verify (T1.0) but expect green. No shim. |
| C2 | Phase 0 touches "`Abstractions` / `Core`" | Trace model is **`AgentEval.Core` only** (`namespace AgentEval.Tracing`). Abstractions is untouched in Phase 0. |
| C3 | (implicit) one `AgentTrace` | **Two** `AgentTrace` types. Edit `AgentEval.Tracing.AgentTrace` (class, Core). Never touch `AgentEval.Output.AgentTrace` (record, Abstractions). Always qualify in ambiguous files. |
| C4 | Sketch uses `ChatTurnEntry.ForRequest(...)`, `ToolExecutionEntry.For(...)` | **These types do not exist** and subclasses would break the flat `List<TraceEntry>` STJ serialization. Instead add the v1.1 fields to `TraceEntry` and add **static factory methods on `TraceEntry`** that return a populated base `TraceEntry` (§3.2). |
| C5 | `Scope` is an "additive nullable field; default AgentInvocation" | Declare `Scope` as **`TraceEntryScope?` (nullable)** so `WhenWritingNull` keeps existing agent-boundary trace JSON byte-identical (no snapshot churn). Consumers treat `null == AgentInvocation` via the `EffectiveScope` helper (§3.2). |
| C6 | Trace Fidelity sub-results "scored 0–100 like MetricResult" | `EvalResult`/`EvalScore.Value` is **0–1**. Store normalized 0–1 in `EvalScore.Value`; surface the 0–100 figure in `EvalDetails.Dimensions["score100"]`. (§ Phase 3.) |
| C7 | `IRunOutputStore` / "audit-chain picks it up" | Type is **`IOutputStore`/`IOutputStoreReader`**. Trace Fidelity flows to the store/Mission Control by emitting an `EvalResult` tree through the existing scenario-result path; no new store type. |
| C8 | "`bench trace-fidelity` works with **no** CLI changes (ADR-017)" | **False.** ADR-017 auto-discovery only fills `bench --list`. A runnable subcommand needs a **handler class (~50 LOC) + `Program.cs` wiring + `BenchListCommand.AnchorAssemblies()` touch**. Phase 3 and Phase 9 include explicit CLI tasks. |
| C9 | Phase 10 "add `PackAsTool`/`ToolCommandName`/`PackageId`" | **Already present.** Phase 10 = add the release-workflow pack/publish step, docs, deprecate standalone repo. |
| C10 | §9.2 "add generic `openai-compatible` option (~10 LOC)" | `EndpointFactory.CreateOpenAICompatible` **already exists** (used by eval/redteam). The gap is only the **bench judge path** (`JudgeFactory` is Azure-only). Wiring it through is ~50 LOC, optional, folded into Phase 9/10. |
| C11 | "register Trace Fidelity as a benchmark family" (implies trivial) | Correct shape (**Shape B**), but it consumes **two traces**. Use an `AsyncLocal` hosting context (copy `LongMemEvalRunnerHostingContext`) to pass the trace pair into the runner factory. |
| C12 | Gate "same policy concepts run inline" | There is **no gate-evaluator abstraction** to reuse. Phase 4 **defines** `IChatGate` + `GateVerdict` (Allow/Block/Redact) and provides built-in gates that reuse the existing PII regexes and injection tokens, plus an `ISafetyMetric→IChatGate` adapter. |
| C13 | Acceptance tests use `FakeChatClient` to script tool calls / retries | `FakeChatClient` can't. Phase 0 adds **`ScriptedChatClient`** (tool calls, finish reasons, usage, streaming, throw-after-N). |
| C14 | `EvaluatingAIFunction` overrides "`Name`,`Description`,`JsonSchema`,`InvokeCoreAsync`" | Correct, but note **only `InvokeCoreAsync` is abstract**; the rest are virtual on `AIFunctionDeclaration`/`AIFunction` and must be **forwarded** to the inner function. `InvokeCoreAsync` returns `ValueTask<object?>`. |
| C15 | Per-turn token usage | `UsageDetails` exposes **`long?`** counts; `TraceTokenUsage` uses `int`. Cast with null-coalesce: `(int)(usage?.InputTokenCount ?? 0)`. |
| C16 | "doctor gains double-wrapping check; migrate handles v1.0→v1.1" | Neither exists yet; both are net-new in Phase 8 against `DoctorCommand`/`MigrateCommand`. |
| C17 | Phase 7 needs a `ChatTurn` GraphQL type + recursive resolver | True, but **chat-turn entries already render** via REST `/trace` + `TraceWaterfallPage` after Phase 1. Phase 7 is the eval-tree-integrated view; also bump `MaxExecutionDepth` 10→12 and update hand-written SPA queries (no codegen). |
| C18 | `ToAgentTrace()` emits a trace | `ChatTraceRecorder.ToAgentTrace()` **hardcodes `Version="1.0"`**. Leave it at "1.0" (it produces only agent/Request/Response entries — genuinely v1.0-shaped). Do not blindly bump it. |
| C19 | "tool-definition de-dupe helper" in Phase 0 | Keep it, but it is a **pure helper** invoked by the recorder/preset, not a serializer change. Off in AuditGrade. |
| C20 | Effort "~0.5–1.5 d/phase" | Treat as relative ordering only; the CLI-handler work (C8) and `ScriptedChatClient` (C13) add real time not in the proposal's totals. |
| C21 | ADRs "land as ADR" | File **ADR-019** (two-layer chat-boundary recording) and **ADR-020** (AgentTrace v1.1 schema). ⚠️ **ADR-018 is already taken** by `018-compliance-core-and-shared-extractions.md` (merged in `744da5c`), so the next free numbers are **019/020**; template in §4.4. |
| C22 | (post-recon main merge) plan assumed pre-merge tree | Commit `744da5c` (128-finding ThoroughReview wave + Compliance.Core, ADR-018) merged to main **after** the recon. Net effects on this plan: (a) ADR numbers shift to 019/020 (C21); (b) **reference `EvalTreeLimits` (Abstractions) / its `MaxTreeWalkDepth` const + the Core tree-walk extensions `Leaves()`/`FindByKey()`/`CountLeaves()`** instead of the literal `32` (ARC-03); (c) Phase 6 must account for the new `AgentEval.Compliance.Core` shared project (ADR-018) — put shared evidence plumbing there, regulation-specific bits in the packs; (d) **`UmbrellaDependencyClosureTests` guard**: if any sub-project gains a new `PackageReference`, mirror it in `src/AgentEval/AgentEval.csproj` or that test fails (Glass Box adds none expected — MEAI/MAF already referenced); (e) `EvalScore` now validates `Threshold`/`Confidence` finiteness too — TraceFidelity's values (0–1, 0.8, null) are finite, so no impact. The core spine (AgentTrace/TraceEntry/BenchmarkFamily/FakeChatClient/EvalResult/CLI helpers) was re-verified **unchanged** on current main. |

---

## 3. The canonical AgentTrace v1.1 schema (single source of truth)

Every phase references this section. Implement it **exactly once** in Phase 0; never redefine inline elsewhere.

### 3.1 New enum + additive fields on `TraceEntry`

Add to 📁 `src/AgentEval.Core/Tracing/AgentTrace.cs`.

```csharp
/// <summary>
/// The recording layer an entry was captured at (Glass Box, v1.1).
/// Null on v1.0 traces and on agent-boundary entries — interpret null as <see cref="AgentInvocation"/>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TraceEntryScope
{
    /// <summary>Agent-boundary entry (TraceRecordingAgent). v1.0 default semantics.</summary>
    AgentInvocation = 0,
    /// <summary>One LLM round-trip at the IChatClient boundary (TraceRecordingChatClient).</summary>
    ChatTurn = 1,
    /// <summary>One tool/function execution (EvaluatingAIFunction).</summary>
    ToolExecution = 2,
}
```

Add these **nullable** properties to `TraceEntry` (do not remove or reorder existing ones):

```csharp
    /// <summary>Recording layer (v1.1). Null ⇒ AgentInvocation (v1.0 semantics).</summary>
    [JsonPropertyName("scope")]
    public TraceEntryScope? Scope { get; set; }

    /// <summary>Correlation id linking ChatTurn entries to their ToolExecution children (v1.1).</summary>
    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; set; }

    /// <summary>Verbatim system prompt sent to the model on this turn (v1.1, ChatTurn requests).</summary>
    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    /// <summary>Tool definitions advertised to the model on this turn (v1.1, ChatTurn requests).</summary>
    [JsonPropertyName("toolDefinitions")]
    public List<TraceToolDefinition>? ToolDefinitions { get; set; }

    /// <summary>Provider finish reason for this turn, e.g. "stop"/"tool_calls"/"length"/"content_filter" (v1.1).</summary>
    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; set; }

    /// <summary>Sampling / request options captured per turn (v1.1): temperature, maxOutputTokens, responseFormat, modelId.</summary>
    [JsonPropertyName("requestOptions")]
    public Dictionary<string, object?>? RequestOptions { get; set; }

    /// <summary>Provider-specific metadata (e.g. Azure content-filter verdicts) (v1.1).</summary>
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, object?>? ProviderMetadata { get; set; }

    /// <summary>Convenience: the effective scope, treating null as AgentInvocation. Not serialized.</summary>
    [JsonIgnore]
    public TraceEntryScope EffectiveScope => Scope ?? TraceEntryScope.AgentInvocation;
```

New supporting type (same file):

```csharp
/// <summary>A tool/function definition as advertised to the model on a chat turn (v1.1).</summary>
public class TraceToolDefinition
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    /// <summary>JSON schema of the parameters, as a JSON string (verbatim).</summary>
    [JsonPropertyName("parametersSchema")] public string? ParametersSchema { get; set; }
}
```

Bump the default: `public string Version { get; set; } = "1.1";`

> **Version semantics (settled — consumers must follow this).** `AgentTrace.Version` is the **highest schema version of any content the file may contain**, *not* a per-entry marker. `"1.1"` means "this trace may carry v1.1 fields (Scope, CorrelationId, ToolDefinitions, FinishReason, …)"; individual entries may still be v1.0-shaped (all-null v1.1 fields). **Consumers branch on per-entry data — `entry.EffectiveScope` and field presence — never on the header string.** It is therefore correct (not lossy) for `TraceRecordingChatClient` to set `Version="1.1"` even when wrapping a trace loaded from a v1.0 file: once a ChatTurn entry is appended, the file genuinely contains v1.1 content. `ChatTraceRecorder.ToAgentTrace()` keeps `"1.0"` (C18) precisely because it only ever emits v1.0-shaped entries.

> **Gate-verdict metadata schema (Phase 4 writes this; documented here as the canonical shape).** The runtime gate (§Phase 4) records each verdict into the trace-level `AgentTrace.Metadata` dictionary (NOT as a `TraceEntry`, so it never enters the Index pairing/replay path). Key: `gate.{stage}.{seq}.{policyName}` where `stage ∈ {pre, post}`, `seq` is a per-client monotonic counter, `policyName` is the gate's `PolicyName`. Value: a `Dictionary<string, object?>` with `["action"] : string` (`"Allow"|"Block"|"Redact"`), `["reason"] : string?`, `["matches"] : IReadOnlyList<string>?`, `["correlationId"] : string?` (the ambient `ToolCorrelationScope.Current`). Compliance (Phase 6) and Mission Control read gate verdicts from here.

### 3.2 Static factory methods on `TraceEntry` (replaces the proposal's non-existent `ChatTurnEntry`/`ToolExecutionEntry`)

Add to the `TraceEntry` class. They return a populated **base `TraceEntry`** (serialization-safe — no subclassing).

```csharp
    /// <summary>Builds a ChatTurn request entry from the messages and options sent to the model.</summary>
    public static TraceEntry ForChatRequest(
        int index, string? correlationId,
        string? systemPrompt, string? promptText,
        List<TraceToolDefinition>? toolDefinitions,
        Dictionary<string, object?>? requestOptions) => new()
    {
        Type = TraceEntryType.Request,
        Scope = TraceEntryScope.ChatTurn,
        Index = index,
        CorrelationId = correlationId,
        Timestamp = DateTimeOffset.UtcNow,
        SystemPrompt = systemPrompt,
        Prompt = promptText,
        ToolDefinitions = toolDefinitions,
        RequestOptions = requestOptions,
    };

    /// <summary>Builds a ChatTurn response entry from the model's reply.</summary>
    public static TraceEntry ForChatResponse(
        int index, string? correlationId, string? text, long durationMs,
        TraceTokenUsage? usage, List<TraceToolCall>? toolCalls,
        string? finishReason, Dictionary<string, object?>? providerMetadata) => new()
    {
        Type = TraceEntryType.Response,
        Scope = TraceEntryScope.ChatTurn,
        Index = index,
        CorrelationId = correlationId,
        Timestamp = DateTimeOffset.UtcNow,
        Text = text,
        DurationMs = durationMs,
        TokenUsage = usage,
        ToolCalls = toolCalls,
        FinishReason = finishReason,
        ProviderMetadata = providerMetadata,
    };

    /// <summary>Builds an error entry for a failed ChatTurn round-trip.</summary>
    public static TraceEntry ForChatError(int index, string? correlationId, Exception ex, long durationMs) => new()
    {
        Type = TraceEntryType.Response,
        Scope = TraceEntryScope.ChatTurn,
        Index = index,
        CorrelationId = correlationId,
        Timestamp = DateTimeOffset.UtcNow,
        DurationMs = durationMs,
        Error = new TraceError { Type = ex.GetType().Name, Message = ex.Message, StackTrace = ex.StackTrace },
    };

    /// <summary>Builds a ToolExecution entry (Phase 2).</summary>
    public static TraceEntry ForToolExecution(
        int index, string? correlationId, string toolName, string? arguments,
        string? result, long durationMs, bool succeeded, string? error) => new()
    {
        Type = TraceEntryType.ToolCall,
        Scope = TraceEntryScope.ToolExecution,
        Index = index,
        CorrelationId = correlationId,
        Timestamp = DateTimeOffset.UtcNow,
        DurationMs = durationMs,
        ToolCalls = new List<TraceToolCall>
        {
            new() { Name = toolName, Arguments = arguments, Result = result, DurationMs = durationMs, Succeeded = succeeded, Error = error }
        },
    };
```

> ⚠️ **`Index` vs `CorrelationId` — two different keys, do not conflate (verified against `TraceReplayingAgent.BuildRequestResponsePairs()`):**
> - **`Index`** = the *pairing* key. `BuildRequestResponsePairs()` does `Entries.GroupBy(e => e.Index)` then takes `FirstOrDefault(Type==Request)` + `FirstOrDefault(Type==Response)`, pairing only when both exist. So a `ChatTurn` round-trip's Request and Response **share one `Index`** (the per-round-trip counter from Phase 1), and **each round-trip gets a distinct `Index`** (never reuse an index across two LLM calls). `ToolExecution` entries are `Type==ToolCall` → they are **silently ignored** by `BuildRequestResponsePairs()` (confirmed: it filters by `Type`), so they never break replay even if their `Index` coincides with a chat turn's.
> - **`CorrelationId`** = the *grouping* key, **not** a pairing key. It is the per-**invocation** id from the ambient `ToolCorrelationScope` (§Phase 1, T1.6), stamped identically on every `ChatTurn` and `ToolExecution` entry produced during one agent invocation. It exists so Trace Fidelity and Mission Control can group "all evidence from this invocation" and link tool executions to the invocation that triggered them.
> - **Consumers must never pair Request↔Response *across scopes* by raw `Index`.** Replay pairs within `ChatTurn`/`AgentInvocation` Request+Response only. Fine-grained "which turn requested which tool" is recovered from the tool **`CallId`** recorded on the `ChatTurn` response entry's `ToolCalls`, not from `Index`.

---

## 4. Conventions (apply to every file)

### 4.1 File header (mandatory — every `.cs` file)
```csharp
// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
```

### 4.2 Code style
- File-scoped namespaces (`namespace AgentEval.Tracing;`). `Nullable=enable` — annotate every reference type. `LangVersion=preview`.
- XML `<summary>` docs on every **public** type and member (CS1591 is suppressed but reviewers require docs on public surface).
- `RootNamespace=AgentEval` everywhere — new code uses namespaces under `AgentEval.*` regardless of project folder.
- New public benchmark factory classes **must** be `namespace AgentEval.Benchmarks` (enforced by `BenchmarkNamespaceContractTests`).

### 4.3 Test conventions
- xunit `[Fact]`/`[Theory]`; FluentAssertions `.Should()`; Verify.Xunit for JSON snapshots. Place tests mirroring source folders under `tests/AgentEval.Tests/…`.
- Naming: `MethodOrScenario_Condition_ExpectedResult`.
- For registry tests, call `BenchmarkFamilyRegistry.Reset()` (internal, via `InternalsVisibleTo`) between cases that mutate it.

### 4.4 ADR template (for Phase 10's ADR-019/020)
Sections, in order: **Title**, **Status** (Proposed→Accepted), **Date**, **Context**, **Decision**, **Consequences**, **Alternatives Considered**. Add a row to 📁 `docs/adr/README.md`.

### 4.5 The Revision Gate — run after **every** phase (`Phase N-R`)

After each phase's tasks are done, a **Revision Gate** (`Phase N-R`) re-verifies the whole phase before the next one starts. **No phase counts as complete until its Revision Gate is fully green.** The gate is identical for every phase — this section is the single definition; each `Phase N-R` just references it plus a phase-specific focus.

**R1 — Re-verify every task (again).** Re-read the *actual files* produced/edited in the phase against each task's acceptance checkboxes. Every box must be objectively true. Confirm no existing public API changed and no existing test/snapshot was altered (Glass Box is additive).

**R2 — Build & full test run.** `dotnet build AgentEval.sln -c Release` clean (no new warnings on new files), then `dotnet test AgentEval.sln -c Release` → **zero new failures vs. the pinned baseline (4372 pass / 1 skip / 0 fail on net10 as of `744da5c`)**. Run on all TFMs the changed projects target.

**R3 — Test quality (thorough, meaningful).**
- **Coverage:** every new public behaviour and every branch (happy path, error path, edge case) is covered. New code that isn't exercised by a test does not ship.
- **AAA:** every test is structured **Arrange / Act / Assert** (comment the three sections; one logical assert-target per test).
- **Mocks over stubs (Moq):** verify *interactions/behaviour* with **Moq** for collaborator interfaces (`IChatGate`, `ISafetyMetric`, `IEvaluator`, `IOutputStore`, `IEvaluableAgent`) — assert calls happened with expected args via `Verify(...)`. Add `Moq` to `Directory.Packages.props` + the test csproj (test-only). **Exception:** for `IChatClient` (async streaming + `AIContent` scripting) use the hand-rolled `ScriptedChatClient`/`FakeChatClient` — Moq cannot cleanly script `IAsyncEnumerable` + tool-call content; this matches the repo's existing convention. State which is used and why in the test file.
- **Meaningful assertions:** assert observable behaviour and data, not implementation details; no `Assert.True(true)`/tautologies.
- **Negative control:** for each non-trivial feature, include (or confirm) a test that **fails if the feature is reverted** (the ThoroughReview wave's standard — see its per-finding "negative control" notes).
- **Determinism:** no wall-clock/random/order-dependent flakiness; use `ScriptedChatClient`, fixed seeds, injected clocks.

**R4 — Code quality.** Review the phase's code for: **DRY** (no duplicated logic — reuse `EvalTreeLimits`, `WorkflowToolCallChecks`, `WorkspaceRootDiscovery`, existing recorders/serializers/PII regexes rather than re-implementing); **KISS** (smallest design that works; no speculative generality); **SOLID** (SRP — one reason to change per type; DIP — depend on `IChatClient`/`IChatGate` abstractions; OCP — extend via the registry/builder, don't modify existing types); **CLEAN** (intention-revealing names, small methods, no dead code/commented-out blocks, no magic numbers); **pragmatic architecture** (additive, no public-API breaks, no new project/dependency unless justified). Honour conventions §4 (SPDX header, file-scoped namespace, nullable annotations, XML docs on public surface).

**R5 — Dependency-closure guard.** If the phase added a `PackageReference` to any embedded sub-project, mirror it in `src/AgentEval/AgentEval.csproj` (ADR-018 `UmbrellaDependencyClosureTests` fails otherwise). Re-run that test.

**R6 — Opus-grade gap review.** Per the project's standing process, an Opus-grade gap review of the **actual changed files** runs against the phase before sign-off (independent eyes; catches what the implementer's own checks miss).

**R7 — Tracking + commit.** Update the §Progress Tracker table (set the phase's tasks to their true `% done` / `✓` / notes). Tick the phase DoD. Commit the phase as one focused commit (`feat(glassbox): Phase N — <title>`), then proceed to `Phase N+1`. Only a fully-green gate unlocks the next phase.

---

## 5. Phases

Each phase: **Goal · Depends on · Files · Tasks · Acceptance (DoD)**. Do not mark a phase complete until *every* box is checked and `dotnet test AgentEval.sln -c Release` shows no new failures.

---

### Phase 0 — AgentTrace v1.1 schema + test harness (`AgentEval.Core`)

**Goal.** Land the additive v1.1 schema (§3) with zero behavioural change to existing recorders, plus the test scaffolding (`ScriptedChatClient`, v1.0 fixture) that later phases depend on.
**Depends on.** Nothing. **This is the first phase.**
**Release.** v0.11.

📁 `src/AgentEval.Core/Tracing/AgentTrace.cs` · 📁 `src/AgentEval.Core/Tracing/TraceSerializer.cs` · 📁 `src/AgentEval.Core/Tracing/ToolDefinitionDeduplicator.cs` (new) · 📁 `src/AgentEval.Core/Testing/ScriptedChatClient.cs` (new) · 📁 `tests/AgentEval.Tests/Tracing/AgentTraceV11SchemaTests.cs` (new) · 📁 `tests/AgentEval.Tests/Fixtures/trace-v1_0-sample.json` (new) · 📁 `tests/AgentEval.Tests/Testing/ScriptedChatClientTests.cs` (new).

#### T0.1 — Add the v1.1 enum, fields, supporting type, and factory methods
Apply §3.1 and §3.2 verbatim to `AgentTrace.cs`. Bump `AgentTrace.Version` default `"1.0"`→`"1.1"`.
- [ ] `AgentTrace.cs` compiles with `TraceEntryScope`, the 7 new `TraceEntry` properties, `EffectiveScope`, `TraceToolDefinition`, and the 4 static factory methods.
- [ ] ⚠️ `Scope` is `TraceEntryScope?` (nullable) — confirm with a test that a `new TraceEntry()` serializes **without** a `"scope"` key (so existing agent traces are byte-identical).
- [ ] ⚠️ **Version-bump semantics:** changing the `Version` default to `"1.1"` affects only **newly constructed** `AgentTrace` instances. Deserialized traces keep whatever `"version"` the file holds (so the T0.2 round-trip leaves a v1.0 file at `"1.0"`). Do **not** add code that auto-bumps `Version` on load. (Per C18, `ChatTraceRecorder.ToAgentTrace()` intentionally keeps emitting `"1.0"`.)

#### T0.2 — Confirm v1.0 back-compat is a no-op + add the regression test
The reflection-based serializer already ignores unknown keys on read and (via `WhenWritingNull`) omits null v1.1 fields on write. **No `TraceSerializer` code change is required**; the deliverable is proof.
1. Create 📁 `tests/AgentEval.Tests/Fixtures/trace-v1_0-sample.json` — a real v1.0 trace (`"version":"1.0"`, 2 entries: one Request, one Response with `tokenUsage`, **no** `scope`/`correlationId`/etc.). Generate it by recording a `FakeChatClient`-backed agent with `TraceRecordingAgent` and copying the output, or hand-write it matching the §1.3 shape.
2. Test `LoadV10Trace_DeserializesWithNullScope_EffectiveScopeIsAgentInvocation`.
- [ ] `TraceSerializer.LoadFromFileAsync(fixture)` returns a trace whose every entry has `Scope == null` and `EffectiveScope == AgentInvocation`.
- [ ] Round-trip test: deserialize the v1.0 fixture → re-serialize → the JSON is unchanged (no spurious `scope` keys, version still readable). Use Verify.Xunit or an explicit string compare.
- [ ] A freshly built v1.1 trace with a `ChatTurn` entry serializes `"scope":"ChatTurn"` (string, not `1`) — proves the `JsonStringEnumConverter` is honoured.

#### T0.3 — Tool-definition de-dup helper (per C19; used by Phase 1 presets)
Create 📁 `src/AgentEval.Core/Tracing/ToolDefinitionDeduplicator.cs`:
```csharp
namespace AgentEval.Tracing;

/// <summary>
/// Collapses repeated tool-definition payloads across turns to control trace size.
/// First occurrence of a (name, schema) pair is kept verbatim; later identical occurrences
/// are replaced by a reference marker. Disabled for AuditGrade (auditors want verbatim payloads).
/// </summary>
public sealed class ToolDefinitionDeduplicator
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly bool _enabled;
    public ToolDefinitionDeduplicator(bool enabled) => _enabled = enabled;

    /// <summary>Returns the list to record: verbatim on first sight, or a name-only stub when already seen.</summary>
    public List<TraceToolDefinition>? Process(List<TraceToolDefinition>? defs)
    {
        if (!_enabled || defs is null) return defs;
        var outp = new List<TraceToolDefinition>(defs.Count);
        foreach (var d in defs)
        {
            var key = d.Name + "" + (d.ParametersSchema ?? "");
            outp.Add(_seen.Add(key)
                ? d
                : new TraceToolDefinition { Name = d.Name, Description = d.Description, ParametersSchema = null });
        }
        return outp;
    }
}
```
- [ ] Unit test: first call returns verbatim schemas; a second call with the same defs returns stubs with `ParametersSchema == null`; with `enabled:false`, both calls return verbatim.

#### T0.4 — `ScriptedChatClient` (per C13 — prerequisite for Phase 1/3 tests)
Create 📁 `src/AgentEval.Core/Testing/ScriptedChatClient.cs`, namespace `AgentEval.Testing`. A deterministic `IChatClient` that scripts full `ChatResponse`s (tool calls, finish reason, usage) **and** streaming, plus throw-after-N.
```csharp
using Microsoft.Extensions.AI;

namespace AgentEval.Testing;

/// <summary>
/// Deterministic IChatClient for Glass Box tests. Unlike FakeChatClient it can script
/// tool calls, finish reasons, token usage, streaming, and a throw after N calls — the
/// inputs Trace Fidelity / chat-boundary tests need.
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ScriptedTurn> _turns = new();
    public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = new();
    public List<ChatOptions?> ReceivedOptions { get; } = new();
    public int CallCount => ReceivedMessages.Count;

    public ScriptedChatClient Add(ScriptedTurn turn) { _turns.Enqueue(turn); return this; }

    /// <summary>Scripts a plain assistant text reply with finish reason "stop".</summary>
    public ScriptedChatClient AddText(string text, long? inTok = null, long? outTok = null)
        => Add(new ScriptedTurn { Text = text, FinishReason = ChatFinishReason.Stop, InputTokens = inTok, OutputTokens = outTok });

    /// <summary>Scripts an assistant turn that emits a tool call (finish reason "tool_calls").</summary>
    public ScriptedChatClient AddToolCall(string callId, string name, IDictionary<string, object?> args)
        => Add(new ScriptedTurn { ToolCallId = callId, ToolName = name, ToolArgs = args, FinishReason = ChatFinishReason.ToolCalls });

    /// <summary>Scripts a turn that throws (simulates a transient/provider error).</summary>
    public ScriptedChatClient AddThrow(string message = "Simulated provider error")
        => Add(new ScriptedTurn { Throw = message });

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ReceivedMessages.Add(messages.ToList());
        ReceivedOptions.Add(options);
        var t = _turns.Count > 0 ? _turns.Dequeue() : new ScriptedTurn { Text = "", FinishReason = ChatFinishReason.Stop };
        if (t.Throw is not null) throw new InvalidOperationException(t.Throw);

        var contents = new List<AIContent>();
        if (t.ToolName is not null)
            // FunctionCallContent's `arguments` param is non-nullable; default to an empty dict to avoid a CS8604 warning.
            contents.Add(new FunctionCallContent(t.ToolCallId ?? "call_0", t.ToolName, t.ToolArgs ?? new Dictionary<string, object?>()));
        if (!string.IsNullOrEmpty(t.Text))
            contents.Add(new TextContent(t.Text));

        var msg = new ChatMessage(ChatRole.Assistant, contents);
        var resp = new ChatResponse(msg) { FinishReason = t.FinishReason, ModelId = "scripted" };
        if (t.InputTokens is not null || t.OutputTokens is not null)
            resp.Usage = new UsageDetails { InputTokenCount = t.InputTokens, OutputTokenCount = t.OutputTokens };
        return Task.FromResult(resp);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resp = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var m in resp.Messages)
            yield return new ChatResponseUpdate(m.Role, m.Contents) { FinishReason = resp.FinishReason, ModelId = resp.ModelId };
    }

    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}

/// <summary>One scripted model turn for <see cref="ScriptedChatClient"/>.</summary>
public sealed class ScriptedTurn
{
    public string? Text { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public IDictionary<string, object?>? ToolArgs { get; init; }
    public ChatFinishReason? FinishReason { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public string? Throw { get; init; }
}
```
- [ ] `ScriptedChatClientTests`: `AddText` yields a `ChatResponse` with `Text`, `FinishReason==Stop`, `Usage` populated; `AddToolCall` yields a response whose `Messages[0].Contents` contains a `FunctionCallContent` with the given `Name`/`CallId`/`Arguments`; `AddThrow` makes `GetResponseAsync` throw; streaming yields ≥1 `ChatResponseUpdate`.
- [ ] ⚠️ Verify the exact `FunctionCallContent` and `ChatResponseUpdate` constructors against MEAI 10.5.0 IntelliSense (signatures confirmed present; argument order is the one MEAI exposes). Adjust only constructor *call sites* if needed — the shapes in §1.2 are authoritative.

**Phase 0 DoD**
- [ ] `dotnet build AgentEval.sln -c Release` clean.
- [ ] All T0.* tests green; no existing trace snapshot test changed (proves additive-only).
- [ ] `git grep -n "= \"1.0\""` in `src/AgentEval.Core/Tracing` shows only `ChatTraceRecorder` (intentional per C18) still emitting "1.0".
- [ ] **Phase 0-R — Revision Gate (§4.5)** green (focus: v1.0 round-trip byte-identical; `ScriptedChatClient` scripts tool-calls/finish-reason/usage/streaming; de-dup helper toggles by preset).

---

### Phase 1 — `TraceRecordingChatClient` + `UseTraceRecording` (`AgentEval.Core` / `AgentEval.Tracing`)

**Goal.** Record every LLM round-trip verbatim at the `IChatClient` boundary into a v1.1 `AgentTrace`.
**Depends on.** Phase 0 ✅.
**Release.** v0.11.

📁 `src/AgentEval.Core/Tracing/TraceRecordingChatClient.cs` (new) · 📁 `src/AgentEval.Core/Tracing/TraceRecordingChatClientExtensions.cs` (new) · 📁 `src/AgentEval.Core/Tracing/ToolCorrelationScope.cs` (new) · 📁 `tests/AgentEval.Tests/Tracing/TraceRecordingChatClientTests.cs` (new) · 📁 `docs/tracing.md` (edit).

> ⚠️⚠️ **COMPOSITION ORDER IS LOAD-BEARING — read before T1.2 (this is the #1 way to silently break Glass Box).**
> MEAI's `ChatClientBuilder` makes the **first** `.Use(...)` the **outermost** layer (it wraps inner→outer; confirmed). `FunctionInvokingChatClient` (FICC) is the component that *loops* — it calls **its inner client once per model round-trip** until no tool calls remain.
> Therefore, to capture **every** round-trip (the entire point — proposal §3.3, Phase 1 acceptance), `TraceRecordingChatClient` **must be composed INNER of** `UseFunctionInvocation` — i.e. it must appear **after** `.UseFunctionInvocation()` in the `.Use()` chain so FICC calls it N times:
> ```csharp
> raw.AsBuilder()
>    .UseEvalGate(pre: [...])      // outermost — sees the original user input
>    .UseFunctionInvocation()      // the tool loop — calls its inner client once PER round-trip
>    .UseTraceRecording("agent", trace)   // INNER of FICC → records ONE entry per real model round-trip ✅
>    .UseEvalGate(post: [...])
>    .Build();                     // raw model client is innermost
> ```
> ❌ **Do NOT** put `.UseTraceRecording()` *before* `.UseFunctionInvocation()` (as the proposal's §6.1 sketch does). That makes the recorder outer of FICC, so it records **one** entry for the whole loop — defeating chat-boundary capture. (If FICC is *not* in the chain — e.g. MAF's agent runtime drives the tool loop itself and calls the traced `IChatClient` once per turn — then there is no FICC layer and the recorder correctly sees each turn. The rule is specifically: *whenever FICC is composed, the recorder goes inner of it*.)
> This corrects proposal §6.1 (which lists the recorder before FICC). All sample/gate composition snippets in this plan use the corrected order.
> **Gate placement nuance:** `UseEvalGate(pre:…)` belongs **outermost** (it must see the original user input). `UseEvalGate(post:…)` placed inner of FICC (as above) inspects **each per-turn model output** — fine for PII/toxicity redaction since the user-facing answer is one of those turns. If you specifically need to gate only the **final aggregated answer**, place the post-gate **outer of `UseFunctionInvocation`** instead. Either placement is valid; pick per intent and document it in the wiring.

#### T1.0 — Re-verify MEAI base types (gate; expected green)
Run: `dotnet list src/AgentEval.Core/AgentEval.Core.csproj package --include-transitive | findstr /I "Microsoft.Extensions.AI"`.
- [ ] MEAI 10.5.0 resolved. (Type availability already confirmed by reflection — see §1.2. If, contrary to that, `DelegatingChatClient`/`ChatClientBuilder.Use` are missing at compile time, STOP and escalate; do not write a shim without sign-off.)

#### T1.1 — `SamplePreset` enum (shared by recorder + gate + benchmark)
Add to a new 📁 `src/AgentEval.Core/Tracing/SamplePreset.cs`:
```csharp
namespace AgentEval.Tracing;

/// <summary>Capture fidelity preset. Controls per-turn detail and tool-definition de-dup.</summary>
public enum SamplePreset
{
    /// <summary>Minimal capture; tool-definition de-dup ON; for CI smoke runs.</summary>
    Smoke,
    /// <summary>Standard capture; tool-definition de-dup ON.</summary>
    Standard,
    /// <summary>Full verbatim capture; tool-definition de-dup OFF (auditors want raw payloads).</summary>
    AuditGrade,
}
```
- [ ] Compiles. `AuditGrade` documented as de-dup-off.

#### T1.2 — `TraceRecordingChatClient`
Create 📁 `src/AgentEval.Core/Tracing/TraceRecordingChatClient.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json;
using System.Threading;          // Interlocked (also an implicit using when ImplicitUsings=enable; explicit here for clarity)
using Microsoft.Extensions.AI;

namespace AgentEval.Tracing;

/// <summary>
/// Records every LLM round-trip at the <see cref="IChatClient"/> boundary into an
/// <see cref="AgentTrace"/> (Scope = ChatTurn). Complements <see cref="TraceRecordingAgent"/>
/// (agent boundary) and is distinct from <see cref="ChatTraceRecorder"/> (conversation replay).
/// See docs/tracing.md §"Two recording layers".
/// </summary>
public sealed class TraceRecordingChatClient : DelegatingChatClient
{
    private readonly AgentTrace _trace;
    private readonly ToolDefinitionDeduplicator _dedup;
    private int _index;

    public TraceRecordingChatClient(IChatClient inner, string agentName, AgentTrace trace, SamplePreset preset = SamplePreset.Standard)
        : base(inner)
    {
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _trace.AgentName ??= agentName;
        _trace.Version = "1.1";
        _dedup = new ToolDefinitionDeduplicator(enabled: preset != SamplePreset.AuditGrade);
    }

    /// <summary>The accumulating trace. Call <see cref="Finalize"/> before reading aggregate performance.</summary>
    public AgentTrace Trace => _trace;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var i = Interlocked.Increment(ref _index) - 1;             // distinct per round-trip (pairing key)
        var corr = ToolCorrelationScope.Current;                    // per-invocation grouping key (may be null; best-effort)
        _trace.Entries.Add(TraceEntry.ForChatRequest(
            i, corr,
            systemPrompt: ExtractSystemPrompt(msgs),
            promptText: ExtractLastUserText(msgs),
            toolDefinitions: _dedup.Process(ExtractToolDefinitions(options)),
            requestOptions: ExtractRequestOptions(options)));

        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await base.GetResponseAsync(msgs, options, cancellationToken);
            sw.Stop();
            _trace.ModelId ??= resp.ModelId;
            _trace.Entries.Add(TraceEntry.ForChatResponse(
                i, corr, resp.Text, sw.ElapsedMilliseconds,
                MapUsage(resp.Usage), MapToolCalls(resp),
                resp.FinishReason?.Value, ExtractProviderMetadata(resp)));
            return resp;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            _trace.Entries.Add(TraceEntry.ForChatError(i, corr, ex, sw.ElapsedMilliseconds));
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var msgs = messages as IList<ChatMessage> ?? messages.ToList();
        var i = Interlocked.Increment(ref _index) - 1;
        var corr = ToolCorrelationScope.Current;                    // per-invocation grouping key (see T1.6)
        _trace.Entries.Add(TraceEntry.ForChatRequest(
            i, corr, ExtractSystemPrompt(msgs), ExtractLastUserText(msgs),
            _dedup.Process(ExtractToolDefinitions(options)), ExtractRequestOptions(options)));

        var sw = Stopwatch.StartNew();
        var chunks = new List<TraceStreamChunk>();
        var text = new System.Text.StringBuilder();
        string? finish = null;
        var idx = 0;
        await using var e = base.GetStreamingResponseAsync(msgs, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatResponseUpdate cur;
            try { if (!await e.MoveNextAsync()) break; cur = e.Current; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                _trace.Entries.Add(TraceEntry.ForChatError(i, corr, ex, sw.ElapsedMilliseconds));
                throw;
            }
            if (!string.IsNullOrEmpty(cur.Text)) { text.Append(cur.Text); chunks.Add(new TraceStreamChunk { Index = idx++, Text = cur.Text }); }
            finish ??= cur.FinishReason?.Value;
            yield return cur;
        }
        sw.Stop();
        var resp = TraceEntry.ForChatResponse(i, corr, text.ToString(), sw.ElapsedMilliseconds, usage: null, toolCalls: null, finish, providerMetadata: null);
        resp.IsStreaming = true;
        resp.StreamingChunks = chunks;
        _trace.Entries.Add(resp);
    }

    /// <summary>Computes aggregate <see cref="TracePerformance"/> from the recorded ChatTurn entries.</summary>
    public void Finalize()
    {
        var responses = _trace.Entries.Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response).ToList();
        _trace.Performance = new TracePerformance
        {
            TotalDurationMs = responses.Sum(r => r.DurationMs ?? 0),
            TotalPromptTokens = responses.Sum(r => r.TokenUsage?.PromptTokens ?? 0),
            TotalCompletionTokens = responses.Sum(r => r.TokenUsage?.CompletionTokens ?? 0),
            CallCount = responses.Count,
            ToolCallCount = responses.Sum(r => r.ToolCalls?.Count ?? 0),
        };
    }

    // --- helpers (private static) ---
    private static string? ExtractSystemPrompt(IList<ChatMessage> m) =>
        string.Join("\n", m.Where(x => x.Role == ChatRole.System).Select(x => x.Text).Where(t => !string.IsNullOrEmpty(t)));
    private static string? ExtractLastUserText(IList<ChatMessage> m) =>
        m.LastOrDefault(x => x.Role == ChatRole.User)?.Text;
    private static List<TraceToolDefinition>? ExtractToolDefinitions(ChatOptions? o)
    {
        if (o?.Tools is null || o.Tools.Count == 0) return null;
        return o.Tools.OfType<AIFunction>().Select(f => new TraceToolDefinition
        {
            Name = f.Name, Description = f.Description,
            // JsonSchema is a non-nullable JsonElement; a default/Undefined element throws from GetRawText().
            ParametersSchema = f.JsonSchema.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : f.JsonSchema.GetRawText()
        }).ToList();
    }
    private static Dictionary<string, object?>? ExtractRequestOptions(ChatOptions? o)
    {
        if (o is null) return null;
        return new Dictionary<string, object?>
        {
            ["temperature"] = o.Temperature, ["maxOutputTokens"] = o.MaxOutputTokens,
            ["responseFormat"] = o.ResponseFormat?.GetType().Name, ["modelId"] = o.ModelId,
        };
    }
    private static TraceTokenUsage? MapUsage(UsageDetails? u) => u is null ? null : new TraceTokenUsage
    {
        PromptTokens = (int)(u.InputTokenCount ?? 0), CompletionTokens = (int)(u.OutputTokenCount ?? 0)
    };
    private static List<TraceToolCall>? MapToolCalls(ChatResponse r)
    {
        var calls = r.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        if (calls.Count == 0) return null;
        return calls.Select(c => new TraceToolCall
        {
            Name = c.Name,
            Arguments = c.Arguments is null ? null : JsonSerializer.Serialize(c.Arguments),
        }).ToList();
    }
    private static Dictionary<string, object?>? ExtractProviderMetadata(ChatResponse r) =>
        r.AdditionalProperties is null || r.AdditionalProperties.Count == 0
            ? null
            : r.AdditionalProperties.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
}
```
> ⚠️ `f.JsonSchema` is a `JsonElement` → use `.GetRawText()`. `AdditionalProperties` is `AdditionalPropertiesDictionary` (string→object?) — adjust the `ToDictionary` lambda to the exact KV value type if the compiler complains.

#### T1.3 — Builder extension
Create 📁 `src/AgentEval.Core/Tracing/TraceRecordingChatClientExtensions.cs`:
```csharp
using Microsoft.Extensions.AI;

namespace AgentEval.Tracing;

/// <summary>Builder extension to insert <see cref="TraceRecordingChatClient"/> into a chat pipeline.</summary>
public static class TraceRecordingChatClientExtensions
{
    /// <summary>Records every LLM round-trip into <paramref name="trace"/>. Usage:
    /// <c>client.AsBuilder().UseTraceRecording("planner", trace).Build()</c>.</summary>
    public static ChatClientBuilder UseTraceRecording(
        this ChatClientBuilder builder, string agentName, AgentTrace trace, SamplePreset preset = SamplePreset.Standard) =>
        builder.Use(inner => new TraceRecordingChatClient(inner, agentName, trace, preset));
}
```
- [ ] Compiles; `client.AsBuilder().UseTraceRecording("x", trace).Build()` resolves.

#### T1.4 — Tests (`TraceRecordingChatClientTests`) using `ScriptedChatClient`
Script a 4-turn loop: `AddToolCall("c1","SearchFlights",{from,to})` → `AddToolCall("c2","SearchHotels",{city})` → `AddText("…summary…")` → `AddText("Final answer", inTok:100, outTok:50)`.
- [ ] Wrapping the scripted client with `UseTraceRecording` and issuing 4 `GetResponseAsync` calls produces a trace with **4 `ChatTurn` Request entries + 4 `ChatTurn` Response entries**, each Response with `Scope==ChatTurn` and a distinct `Index`.
- [ ] The following appear on the relevant entries → **≥8 of the 11 evidence classes in proposal §3.3** present: system prompt, tool definitions (when `ChatOptions.Tools` set), per-turn text, finish reason ("tool_calls"/"stop"), tool-call args, per-turn usage, per-turn latency, **request options** (`RequestOptions["temperature"]`/`["maxOutputTokens"]`), and **provider metadata** (`ProviderMetadata` populated when the response carries `AdditionalProperties`, else null).
- [ ] Per-turn `TokenUsage` maps `long?`→`int` correctly (turn 4: prompt 100, completion 50).
- [ ] A scripted `AddThrow()` produces a `ChatTurn` Response entry carrying `Error` and re-throws.
- [ ] Streaming: scripting a text turn and consuming `GetStreamingResponseAsync` yields chunks and a finalized Response entry with `IsStreaming==true` and populated `StreamingChunks`.
- [ ] **Replay compatibility (per §3.2 Index/CorrelationId rule):** load a v1.1 trace with 4 `ChatTurn` round-trips (distinct `Index` 0–3) into a `TraceReplayingAgent` and confirm `BuildRequestResponsePairs()` does **not** throw and replays all 4. Then confirm a v1.0 fixture (2 agent-boundary entries, `Index` 0,1) still replays correctly. Then confirm a mixed-scope trace (ChatTurn + ToolExecution `Type==ToolCall` entries) replays without the ToolCall entries corrupting the pairs.
- [ ] **CorrelationId grouping:** when the calls run inside `using new ToolCorrelationScope("inv-1")`, every recorded entry has `CorrelationId=="inv-1"`; with no scope, `CorrelationId==null` (best-effort) and recording still succeeds.
- [ ] **🔴 Composition / per-round-trip capture (the headline acceptance — do NOT skip):** build `scripted.AsBuilder().UseFunctionInvocation().UseTraceRecording("a", trace).Build()` where `scripted` is a `ScriptedChatClient` that emits a tool call on turn 1 then a final text answer on turn 2, and register a matching tool so FICC actually loops. Invoke **once**. Assert the trace contains **2** `ChatTurn` Response entries (one per real round-trip), **not 1**. Then build the *wrong* order (`.UseTraceRecording().UseFunctionInvocation()`) and assert it yields **1** entry — documenting in the test why the inner-of-FICC order is required.
- [ ] Overhead micro-check: 10k no-op `GetResponseAsync` calls over a trivial inner client add <2 ms p99 per call (informational; record the number in the test output, do not gate CI on it).

#### T1.6 — `ToolCorrelationScope` (in **Core**, established by the caller)
> ⚠️ This type lives in **`AgentEval.Core/Tracing`** (namespace `AgentEval.Tracing`), **not** in `AgentEval.MAF`. Reason (verified): `AgentEval.MAF` references `AgentEval.Core` but **Core does not reference MAF**, and `TraceRecordingChatClient` (Core, T1.2) must *read* this scope. Placing it in MAF would make Phase 1 un-compilable. Both `TraceRecordingChatClient` (Core) and `EvaluatingAIFunction` (MAF, Phase 2) **read** `ToolCorrelationScope.Current`; **neither sets it** — the caller (sample / harness / test) establishes it once per agent invocation so the id flows (via `AsyncLocal`) to both the inner recorder and the tool executions, regardless of pipeline order.
```csharp
namespace AgentEval.Tracing;

/// <summary>
/// Ambient, per-invocation correlation id. Establish it with a using-block around an agent
/// invocation: <c>using var _ = new ToolCorrelationScope(invocationId);</c>. TraceRecordingChatClient
/// and EvaluatingAIFunction READ <see cref="Current"/> to stamp every entry of the invocation with the
/// same CorrelationId. Flows through async/await; see proposal §14 Q2 for the parallel-tools caveat.
/// </summary>
public sealed class ToolCorrelationScope : IDisposable
{
    private static readonly AsyncLocal<string?> _current = new();
    public static string? Current => _current.Value;
    private readonly string? _previous;
    public ToolCorrelationScope(string correlationId) { _previous = _current.Value; _current.Value = correlationId; }
    public void Dispose() => _current.Value = _previous;
}
```
- [ ] Test `ToolCorrelationScope_NestedScopesRestorePrevious`: outer `"outer"`→`Current=="outer"`; inner `"inner"`→`Current=="inner"`; dispose inner→`"outer"`; dispose outer→`null`.

#### T1.5 — Docs
Edit 📁 `docs/tracing.md`: add a "Two recording layers" subsection with the proposal §4.4 contrast table (ChatTraceRecorder vs TraceRecordingChatClient) and a `UseTraceRecording` usage snippet.
- [ ] Section renders; table present; snippet compiles conceptually (matches T1.3 API).

**Phase 1 DoD**
- [ ] Build clean; all T1.* tests green.
- [ ] No change to `TraceRecordingAgent`/`ChatTraceRecorder`/existing snapshots.
- [ ] **Phase 1-R — Revision Gate (§4.5)** green (focus: 🔴 the per-round-trip composition test — N entries for an N-turn FICC loop, 1 for the wrong order; <2 ms p99 overhead recorded).

---

### Phase 2 — `EvaluatingAIFunction` + `WithEvaluation` (`AgentEval.MAF`)

**Goal.** Capture tool/function **execution** (timing, args, result, exception) — the one thing `IChatClient` middleware structurally cannot see — and correlate it to the parent chat turn.
**Depends on.** Phase 0 ✅ (uses `TraceEntry.ForToolExecution` + `CorrelationId`).
**Release.** v0.11.

📁 `src/AgentEval.MAF/Tracing/EvaluatingAIFunction.cs` (new) · 📁 `src/AgentEval.MAF/Tracing/EvaluatingAIFunctionExtensions.cs` (new) · 📁 `tests/AgentEval.Tests/Tracing/EvaluatingAIFunctionTests.cs` (new).

> Placement: `EvaluatingAIFunction` lives in `AgentEval.MAF` per proposal §4.2/§7 (tool wiring is MAF's surface). It **reads** `AgentEval.Tracing.ToolCorrelationScope.Current` (defined in **Core**, T1.6) — MAF references Core, so this is legal.

#### T2.1 — `ToolCorrelationScope` lives in Core (not here)
⚠️ **Correction (architectural):** `ToolCorrelationScope` is created in **Phase 1 / Core** (T1.6), **not** in MAF. Core cannot reference MAF, and Phase 1's Core-resident `TraceRecordingChatClient` must read the same scope — so the type must live in Core. Phase 2 only **consumes** it. There is no MAF-local correlation type and no `src/AgentEval.MAF/Tracing/ToolCorrelationScope.cs`.
- [ ] `EvaluatingAIFunction.cs` declares `using AgentEval.Tracing;` and reads `ToolCorrelationScope.Current` for the `CorrelationId`; no MAF-local scope type exists.

#### T2.2 — `EvaluatingAIFunction`
Create 📁 `EvaluatingAIFunction.cs` (override **only** `InvokeCoreAsync`; forward the virtual declaration members — per C14):
```csharp
using System.Diagnostics;
using System.Text.Json;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Tracing;

/// <summary>
/// Wraps an <see cref="AIFunction"/> to record its execution (start, duration, args, result,
/// exception) into an <see cref="AgentTrace"/> as a ToolExecution entry correlated to the parent
/// ChatTurn via <see cref="ToolCorrelationScope"/>. Closes the gap IChatClient middleware can't reach.
/// </summary>
public sealed class EvaluatingAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly AgentTrace _trace;

    public EvaluatingAIFunction(AIFunction inner, AgentTrace trace)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;
    public override JsonSerializerOptions? JsonSerializerOptions => _inner.JsonSerializerOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var i = _trace.Entries.Count;
        var corr = ToolCorrelationScope.Current;
        var sw = Stopwatch.StartNew();
        var argsJson = SafeSerialize(arguments);
        try
        {
            var result = await _inner.InvokeAsync(arguments, cancellationToken);   // public entry point — see note below
            sw.Stop();
            _trace.Entries.Add(TraceEntry.ForToolExecution(
                i, corr, _inner.Name, argsJson, SafeSerialize(result), sw.ElapsedMilliseconds, succeeded: true, error: null));
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _trace.Entries.Add(TraceEntry.ForToolExecution(
                i, corr, _inner.Name, argsJson, result: null, sw.ElapsedMilliseconds, succeeded: false, error: ex.Message));
            throw;
        }
    }

    private static string? SafeSerialize(object? o)
    {
        if (o is null) return null;
        try { return JsonSerializer.Serialize(o); } catch { return o.ToString(); }
    }
}
```
> ⚠️ **Settled (do not deviate):** call `_inner.InvokeAsync(arguments, cancellationToken)` — the **public** entry point on `AIFunction` (confirmed present in MEAI 10.5.0: `public ValueTask<object?> InvokeAsync(AIFunctionArguments, CancellationToken)`). Do **not** call `_inner.InvokeCoreAsync(...)`: `InvokeCoreAsync` is `protected`, and C# (CS1540) forbids accessing a protected member through a **base-typed** reference (`_inner` is typed `AIFunction`) from a sibling derived class. `InvokeAsync` internally routes to the inner's `InvokeCoreAsync`, so behaviour is identical. State this in the type's XML doc.

#### T2.3 — Extension
Create 📁 `EvaluatingAIFunctionExtensions.cs`:
```csharp
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Tracing;

/// <summary>Wraps an <see cref="AIFunction"/> with execution recording. Usage: <c>fn.WithEvaluation(trace)</c>.</summary>
public static class EvaluatingAIFunctionExtensions
{
    public static AIFunction WithEvaluation(this AIFunction fn, AgentTrace trace) => new EvaluatingAIFunction(fn, trace);
}
```

#### T2.4 — Tests
Build a real `AIFunction` via `AIFunctionFactory.Create(...)` over a local delegate (e.g. `int Add(int a, int b) => a+b;` and one that throws).
- [ ] A wrapped function invoked inside a `using new ToolCorrelationScope("chat-0001")` records a `ToolExecution` entry (`Scope==ToolExecution`, `Type==ToolCall`) with `CorrelationId=="chat-0001"`, the serialized args, the serialized result, `Succeeded==true`, and `DurationMs>=0`.
- [ ] A wrapped throwing function records `Succeeded==false` with `Error` set, and re-throws.
- [ ] `Name`/`Description`/`JsonSchema` of the wrapper equal the inner's (forwarding works).

**Phase 2 DoD**
- [ ] Build clean; T2.* green. Correlation verified for sequential tools (parallel-tools caveat documented per proposal §14 Q2).
- [ ] **Phase 2-R — Revision Gate (§4.5)** green (focus: `_inner.InvokeAsync` (not InvokeCoreAsync); forwarded Name/Description/JsonSchema; CorrelationId read from scope).

---

### Phase 3 — Trace Fidelity (runner + Shape-B family + CLI handler) (`AgentEval.Core`)

**Goal.** Reconcile an agent-boundary trace against a chat-boundary trace and emit an `EvalResult` tree flagging the six discrepancy classes — the structurally novel capability.
**Depends on.** Phase 0 ✅, Phase 1 ✅ (produces the chat-boundary trace). Phase 2 optional (improves tool-execution evidence).
**Release.** v0.11.

📁 `src/AgentEval.Core/Benchmarks/TraceFidelity/TraceFidelityRunner.cs` (new) · 📁 `…/TraceFidelityReport.cs` (new) · 📁 `…/TraceFidelityBenchmark.cs` (new, **namespace `AgentEval.Benchmarks`**) · 📁 `…/TraceFidelityBenchmarkRegistration.cs` (new) · 📁 `…/TraceFidelityRunnerHostingContext.cs` (new) · 📁 `src/AgentEval.Cli/Commands/BenchTraceFidelityCommand.cs` (new) · edits to 📁 `src/AgentEval.Cli/Program.cs` + 📁 `src/AgentEval.Cli/Commands/BenchListCommand.cs` · tests.

> ⚠️ Per C8, the CLI subcommand is **not** free. T3.5–T3.6 add it explicitly.

#### T3.1 — The six discrepancy classes + scoring rubric (define once, test-pinned)
Discrepancy classes (proposal §5.1), each producing one child `EvalResult`:

| Key | Detect by comparing | Default severity | Weight |
|---|---|---|---|
| `missing_tool_calls` | tool calls in chat trace (`FunctionCallContent`/ToolExecution) absent from agent trace `ToolCalls` | High | 0.20 |
| `phantom_tool_calls` | tool calls in agent trace absent from chat trace | High | 0.20 |
| `argument_drift` | same tool/turn, different serialized args | Medium | 0.15 |
| `hidden_retries` | chat-trace ChatTurn round-trips `N` > agent-trace invocations `M` for the same logical call | High | 0.20 |
| `token_under_reporting` | `Σ` chat-turn tokens ≠ agent-trace `TracePerformance` totals (beyond a 2% tolerance) | Low | 0.10 |
| `suppressed_finish_reason` | chat trace has `content_filter`/`length` but agent boundary reports `stop`/none | Critical | 0.15 |

> ✅ **De-dup does not affect fidelity.** All six classes compare tool **calls** (`FunctionCallContent`/`ToolCalls`), per-turn **finish reasons**, and **token usage** — **never** the tool *definition schemas* that `ToolDefinitionDeduplicator` stubs. So a Smoke/Standard trace (de-dup ON) and an AuditGrade trace (de-dup OFF) reconcile identically. The reconciliation reads `TraceEntry.ToolCalls`, `FinishReason`, and `TokenUsage`, not `ToolDefinitions`.

**Rubric (per C6 — scores are 0–1). Fully specified — implement these exact numbers:**

`severityPenalty` per discrepancy occurrence (how much one occurrence subtracts before clamping):

| Severity | severityPenalty (per occurrence) |
|---|---|
| Critical | 1.00 |
| High | 0.50 |
| Medium | 0.25 |
| Low | 0.10 |

Per-class child score: `childValue = 1.0 - min(severityPenalty(class) × count, 1.0)` (clamp to [0,1]; `count==0 ⇒ childValue==1.0`).
Family root score: `rootValue = 1.0 - Σ_classes( weight(class) × (1.0 - childValue(class)) )`, clamped to [0,1]. The six weights sum to 1.00 (0.20+0.20+0.15+0.20+0.10+0.15). Surface `EvalDetails.Dimensions["score100"] = rootValue*100`.

**Worked example (pin this in a test):** suppose `hidden_retries` count=1 (High, penalty 0.50) and all other classes count=0.
- `childValue(hidden_retries) = 1.0 - min(0.50×1, 1.0) = 0.50`; every other `childValue = 1.0`.
- `rootValue = 1.0 - (0.20 × (1.0 - 0.50)) = 1.0 - 0.10 = 0.90`. → root `score100 = 90`, `hidden_retries` child `score100 = 50`.

- [ ] A `TraceFidelityRubric` static class holds `Weight(classKey)`, `Severity(classKey)`, and `SeverityPenalty(severity)` as named constants with XML docs.
- [ ] **Divergence test (pin the math, like OWASP preset tests):** assert the worked example above produces root `Value==0.90` and `hidden_retries` child `Value==0.50` exactly; assert the six weights sum to 1.00.

#### T3.2 — `TraceFidelityReport` (plain result model)
Create 📁 `TraceFidelityReport.cs` (namespace `AgentEval.Benchmarks`): a record with `IReadOnlyList<TraceFidelityDiscrepancy> Discrepancies`, `double OverallScore` (0–1), and `TraceFidelityDiscrepancy(string ClassKey, string Severity, int Count, double Score, IReadOnlyList<string> Examples)`.
- [ ] Compiles; immutable records.

#### T3.3 — `TraceFidelityRunner` (the reconciliation engine + EvalResult emission)
Create 📁 `TraceFidelityRunner.cs`. Public API:
```csharp
namespace AgentEval.Benchmarks;

public sealed class TraceFidelityRunner
{
    private readonly SamplePreset _preset;
    public TraceFidelityRunner(SamplePreset preset = SamplePreset.Standard) => _preset = preset;

    /// <summary>Reconciles an agent-boundary trace against a chat-boundary trace.</summary>
    public TraceFidelityReport Reconcile(AgentTrace agentTrace, AgentTrace chatTrace) { /* §3.1 rubric */ }

    /// <summary>Reconciles and projects the report onto the unified EvalResult tree
    /// (one SubResult per discrepancy class; scores normalized 0–1; 0–100 in Dimensions).</summary>
    public EvalResult ReconcileToEvalResult(AgentTrace agentTrace, AgentTrace chatTrace)
    {
        var report = Reconcile(agentTrace, chatTrace);
        var subs = report.Discrepancies.Select(d => new EvalResult(
            Metric: new EvalMetadata(Key: $"trace_fidelity.{d.ClassKey}", Name: d.ClassKey, Category: "TraceFidelity", Version: "1.0"),
            Score: new EvalScore(Value: d.Score, Ordinal: null,
                Label: d.Score >= 0.99 ? "pass" : d.Score >= 0.8 ? "warn" : "fail",
                Passed: d.Score >= 0.8, Threshold: 0.8, Severity: d.Severity, Confidence: null),
            Details: new EvalDetails(
                Dimensions: new Dictionary<string, double> { ["count"] = d.Count, ["score100"] = d.Score * 100 },
                Evidence: d.Examples.Select(x => new EvalEvidence(Source: "chat-vs-agent", Reference: d.ClassKey, Message: x)).ToList(),
                Recommendations: null, SubResults: null, AggregationStrategy: null),
            Provenance: new EvalProvenance(Type: "code", JudgeModel: null, PromptId: null, PromptHash: null, TokensUsed: null, EstimatedCost: 0.0, CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow)).ToList();

        return new EvalResult(
            Metric: new EvalMetadata(Key: "trace_fidelity", Name: "Trace Fidelity", Category: "TraceFidelity", Version: "1.0"),
            Score: new EvalScore(Value: report.OverallScore, Ordinal: null,
                Label: report.OverallScore >= 0.99 ? "pass" : report.OverallScore >= 0.8 ? "warn" : "fail",
                Passed: report.OverallScore >= 0.8, Threshold: 0.8,
                Severity: report.OverallScore >= 0.8 ? "Low" : report.OverallScore >= 0.5 ? "Medium" : "High", Confidence: null),
            Details: new EvalDetails(
                Dimensions: new Dictionary<string, double> { ["score100"] = report.OverallScore * 100 },
                Evidence: null, Recommendations: null, SubResults: subs, AggregationStrategy: "severity-weighted"),
            Provenance: new EvalProvenance(Type: "code", JudgeModel: null, PromptId: null, PromptHash: null, TokensUsed: null, EstimatedCost: 0.0, CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
```
> ✅ All record constructions use **all-named arguments** (the positional+named mix is valid C#, but all-named is unambiguous for a junior copying it). Arg names/orders match §1.4 (`EvalResult(Metric, Score, Details, Provenance, EvaluatedAt)`, `EvalScore(Value, Ordinal, Label, Passed, Threshold, Severity, Confidence)`, `EvalEvidence(Source, Reference, Message)`, `EvalProvenance(Type, JudgeModel, PromptId, PromptHash, TokensUsed, EstimatedCost, CacheHit)`). Root `Severity` uses a conventional `Low/Medium/High` value (not `"info"`).
- [ ] `Reconcile` correctly identifies each of the six classes given crafted trace pairs (see T3.7).
- [ ] `ReconcileToEvalResult` acceptance: root `Metric.Key == "trace_fidelity"`; root `Score.Value ∈ [0,1]`; root `Details.Dimensions["score100"] == Score.Value*100`; `Details.SubResults.Count == 6`; each sub-result's `Metric.Key` is exactly one of `trace_fidelity.{missing_tool_calls,phantom_tool_calls,argument_drift,hidden_retries,token_under_reporting,suppressed_finish_reason}`; each sub-result `Score.Value ∈ [0,1]`; for a firing class, `Details.Evidence` is non-empty with a concrete example (tool name + turn index). Tree depth = 2 (well under `EvalTreeLimits.MaxTreeWalkDepth`).

#### T3.4 — Hosting context + Shape-B registration (copy the LongMemEval template)
Create 📁 `TraceFidelityRunnerHostingContext.cs` — an `AsyncLocal`-backed context carrying the **trace pair**, modelled exactly on `LongMemEvalRunnerHostingContext` (§1.5). Use this **exact** shape:
```csharp
using AgentEval.Tracing;

namespace AgentEval.Benchmarks;

/// <summary>
/// Ambient context that hands the agent-boundary + chat-boundary trace pair to the Shape-B
/// RunnerFactory. Populate it with a using-block before calling the registry factory:
/// <c>using var ctx = new TraceFidelityRunnerHostingContext(agentTrace, chatTrace);</c>.
/// </summary>
public sealed class TraceFidelityRunnerHostingContext : IDisposable
{
    private static readonly AsyncLocal<TraceFidelityRunnerHostingContext?> _current = new();
    public static TraceFidelityRunnerHostingContext? Current => _current.Value;

    public AgentTrace AgentBoundaryTrace { get; }
    public AgentTrace ChatBoundaryTrace { get; }
    private readonly TraceFidelityRunnerHostingContext? _previous;

    public TraceFidelityRunnerHostingContext(AgentTrace agentBoundaryTrace, AgentTrace chatBoundaryTrace)
    {
        AgentBoundaryTrace = agentBoundaryTrace ?? throw new ArgumentNullException(nameof(agentBoundaryTrace));
        ChatBoundaryTrace = chatBoundaryTrace ?? throw new ArgumentNullException(nameof(chatBoundaryTrace));
        _previous = _current.Value;
        _current.Value = this;
    }

    public void Dispose() => _current.Value = _previous;
}
```
> Note: the CLI handler (T3.5) constructs the runner **directly** (it already holds both traces), so the hosting context is the *registry-driven* path (used by `bench --list`-triggered generic invocation and tests) — exactly the LongMemEval split. The `runnerFactory` in T3.4 returns the runner; the caller reads the trace pair from `Current` if it needs them.

Create 📁 `TraceFidelityBenchmark.cs` (namespace `AgentEval.Benchmarks`, `public static partial class`) with `Smoke()/Standard()/AuditGrade()` returning a configured `TraceFidelityRunner`. Create 📁 `TraceFidelityBenchmarkRegistration.cs`:
```csharp
using System.Runtime.CompilerServices;
using AgentEval.Benchmarks;
using AgentEval.Core.Benchmarks;

namespace AgentEval.Benchmarks;

internal static class TraceFidelityBenchmarkRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Register() => BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
        name: "trace-fidelity",
        description: "Reconciles agent-boundary vs chat-boundary traces; flags missing/phantom calls, hidden retries, argument drift, token under-reporting, suppressed finish reasons.",
        defaultCostTier: CostTier.Free,   // pure code, no LLM tokens
        presets:
        [
            new("smoke", "Core discrepancy classes; de-dup on", CostTier.Free),
            new("standard", "All six discrepancy classes; de-dup on", CostTier.Free),
            new("audit-grade", "All six classes; verbatim payloads (de-dup off)", CostTier.Free),
        ],
        runnerType: typeof(TraceFidelityRunner),
        runnerFactory: preset => preset.Trim().ToLowerInvariant() switch
        {
            "smoke" => TraceFidelityBenchmark.Smoke(),
            "standard" => TraceFidelityBenchmark.Standard(),
            "audit-grade" => TraceFidelityBenchmark.AuditGrade(),
            _ => throw new ArgumentException($"Unknown trace-fidelity preset '{preset}'. Known: smoke, standard, audit-grade."),
        },
        evaluateAsync: null,   // Shape B — two-trace input doesn't map onto a single EvalInput
        docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/benchmarks/trace-fidelity.md",
        owningAssemblyName: typeof(TraceFidelityBenchmark).Assembly.GetName().Name));
}
```
- [ ] `[ModuleInitializer]` fires on `AgentEval.Core` load (Core is always loaded). After touching the assembly, `BenchmarkFamilyRegistry.TryGet("trace-fidelity")` is non-null with three presets.
- [ ] Registry integration test (mirror `tests/AgentEval.Memory.Tests` pattern): family present, `RunnerType == typeof(TraceFidelityRunner)`, `RunnerFactory("standard")` returns a `TraceFidelityRunner`, `CompositeFactory == null`, `EvaluateAsync == null`.

#### T3.5 — CLI handler `bench trace-fidelity` (per C8 — NOT free)
**Copy the real Shape-B handler template:** 📁 `src/AgentEval.Cli/Commands/BenchMemoryCommand.cs` (verified to exist; it is the closest analog — a Shape-B family handler). Create 📁 `src/AgentEval.Cli/Commands/BenchTraceFidelityCommand.cs` mirroring it **exactly** in structure, with this signature:
```csharp
public static class BenchTraceFidelityCommand
{
    public static async Task<int> RunAsync(
        string agentTraceFile, string chatTraceFile, string preset, string subject, string? rootOverride,
        CancellationToken ct = default)
    {
        // 1. Workspace setup — copy BenchMemoryCommand lines 42–60 verbatim:
        //    WorkspaceRootValidator.CanonicaliseOrNull(rootOverride) → WorkspaceRootDiscovery.Find(...) → check .agenteval/.
        // 2. Anchor: _ = typeof(AgentEval.Benchmarks.TraceFidelityBenchmark).Assembly;
        //    var family = BenchmarkFamilyRegistry.TryGet("trace-fidelity"); (null-check like BenchMemoryCommand L65–70)
        // 3. Load the two traces:
        //    var agentTrace = await TraceSerializer.LoadFromFileAsync(agentTraceFile, ct);
        //    var chatTrace  = await TraceSerializer.LoadFromFileAsync(chatTraceFile, ct);
        // 4. var runner = new AgentEval.Benchmarks.TraceFidelityRunner(ParsePreset(preset));   // direct construction (handler holds both traces)
        //    var evalResult = runner.ReconcileToEvalResult(agentTrace, chatTrace);
        // 5. Persist — copy BenchMemoryCommand lines 106–168 verbatim, substituting:
        //    EvalProject "AgentEval.Core", Harness "BenchTraceFidelityCommand", Kind "benchmark";
        //    Verdict from evalResult.Score.Passed (PASS/FAIL); RunSummary.Metrics["trace_fidelity_score100"] = evalResult.Score.Value*100;
        //    write report-native.json = JsonSerializer.Serialize(evalResult, indented) into store.ResolveRunDirectory(...).
        // 6. return evalResult.Score.Passed ? 0 : 2;
    }
}
```
The persistence path is **the same `FileSystemOutputStore` flow** `BenchMemoryCommand` uses (`StartRunAsync(subjectIdentity, new RunContext(...))` → `CompleteRunAsync(manifest, summary, ct)` → write `report-native.json` under `store.ResolveRunDirectory(subjectIdentity, runId)`) — that flow IS the audit chain + Mission Control feed.

**Wire it in `Program.cs`** by copying the `bench memory` block (around `Program.cs:320–342`). Concrete System.CommandLine wiring (match the version/idioms already in `Program.cs` — `Option<T>` ctor + `SetAction((parseResult, ct) => …)` returning `Task<int>`):
```csharp
var tfAgentOpt   = new Option<string>("--agent-trace")  { Description = "Path to the agent-boundary .trace.json", Required = true };
var tfChatOpt    = new Option<string>("--chat-trace")   { Description = "Path to the chat-boundary .trace.json", Required = true };
var tfPresetOpt  = new Option<string>("--preset")       { Description = "smoke | standard | audit-grade", DefaultValueFactory = _ => "standard" };
var tfSubjectOpt = new Option<string>("--subject")      { Description = "Subject name", DefaultValueFactory = _ => "trace-fidelity" };
var tfRootOpt    = new Option<string?>("--root")        { Description = "Workspace root override" };
var benchTraceFidelityCmd = new Command("trace-fidelity", "Reconcile an agent-boundary vs chat-boundary trace.");
benchTraceFidelityCmd.Add(tfAgentOpt); benchTraceFidelityCmd.Add(tfChatOpt); benchTraceFidelityCmd.Add(tfPresetOpt);
benchTraceFidelityCmd.Add(tfSubjectOpt); benchTraceFidelityCmd.Add(tfRootOpt);
benchTraceFidelityCmd.SetAction(async (parseResult, ct) =>
    await BenchTraceFidelityCommand.RunAsync(
        parseResult.GetValue(tfAgentOpt)!, parseResult.GetValue(tfChatOpt)!,
        parseResult.GetValue(tfPresetOpt)!, parseResult.GetValue(tfSubjectOpt)!,
        parseResult.GetValue(tfRootOpt), ct));
benchCmd.Add(benchTraceFidelityCmd);
```
> ⚠️ The exact `Option<T>`/`SetAction` API shape (e.g. `Required`, `DefaultValueFactory`, `GetValue`) must match the System.CommandLine version already used in `Program.cs` — **copy the idioms from the adjacent `bench memory` block verbatim** rather than the above if they differ (the repo pins `System.CommandLine 2.0.3`).
**Update `BenchListCommand.AnchorAssemblies()`** (called at `Program.cs:69`): add `_ = typeof(AgentEval.Benchmarks.TraceFidelityBenchmark).Assembly;` alongside the existing anchors.
- [ ] `agenteval bench --list` shows `trace-fidelity` with three presets and `Free` cost tier.
- [ ] `agenteval bench trace-fidelity --agent-trace a.json --chat-trace c.json --preset standard` runs end-to-end and writes a manifest + `report-native.json` under `.agenteval/`, returning exit 0 (clean) or 2 (discrepancies).

#### T3.6 — Contract-test inclusion (per ADR-017 walkthrough)
Edit 📁 `tests/AgentEval.Tests/Benchmarks/BenchmarkNamespaceContractTests.cs`: ensure `TraceFidelityBenchmark` (the public factory) is in `AgentEval.Benchmarks` (it is). Locate the `DomainTypeExceptions` collection in that file (a set of allowed-outside-`AgentEval.Benchmarks` / non-factory type names) and add the fully-qualified names `AgentEval.Benchmarks.TraceFidelityRunner`, `AgentEval.Benchmarks.TraceFidelityReport`, `AgentEval.Benchmarks.TraceFidelityRunnerHostingContext` (match the existing entries' string format — short name vs FQN — exactly) with the justification comment `// trace-fidelity: runner/result/host support types, not factories`. If the test enumerates loaded assemblies, add an anchor touch `_ = typeof(AgentEval.Benchmarks.TraceFidelityBenchmark).Assembly;`.
- [ ] Contract test passes with the new family registered.

#### T3.7 — Reconciliation unit tests (one probe per discrepancy class)
Build both traces **by hand** — most deterministic, no live client needed. The **chat-boundary** trace holds `ChatTurn` entries (via the §3.2 `TraceEntry.ForChatRequest/ForChatResponse` factories); the **agent-boundary** trace holds `AgentInvocation`-scope entries (plain `new TraceEntry{…}`, `Scope` left null) whose `ToolCalls` list is what the framework *reported*. Use these fixture helpers (place in the test class):
```csharp
static AgentTrace Chat(params TraceEntry[] e) => new() { Version = "1.1", Entries = e.ToList() };
static AgentTrace Agent(TracePerformance? perf = null, params TraceEntry[] e)
    => new() { Version = "1.0", Entries = e.ToList(), Performance = perf };
static TraceEntry ChatResp(int i, string? finish = "stop",
        IEnumerable<(string name, string args)>? calls = null, int prompt = 0, int completion = 0,
        string correlationId = "inv-1")   // parameterized so multi-scope traces can be built when needed
    => TraceEntry.ForChatResponse(i, correlationId, "…", durationMs: 10,
         usage: new TraceTokenUsage { PromptTokens = prompt, CompletionTokens = completion },
         toolCalls: calls?.Select(c => new TraceToolCall { Name = c.name, Arguments = c.args }).ToList(),
         finishReason: finish, providerMetadata: null);
static TraceEntry AgentResp(int i, IEnumerable<(string name, string args)>? calls = null)
    => new() { Type = TraceEntryType.Response, Index = i,
               ToolCalls = calls?.Select(c => new TraceToolCall { Name = c.name, Arguments = c.args }).ToList() };
```
Each test asserts the named discrepancy fires with the stated `count` at its rubric severity, the child `Score.Value` matches the T3.1 formula, and the root `Value < 1.0`; the final test asserts a clean pair yields every child `Value == 1.0` and root `Value == 1.0`.

- [ ] **hidden_retries** *(headline acceptance)*: `chat = Chat(ChatResp(0, "tool_calls", [("SearchFlights","{}")]), ChatResp(1, "tool_calls", [("SearchFlights","{}")]), ChatResp(2,"stop"))` (a silent retry — same tool twice); `agent = Agent(null, AgentResp(0, [("SearchFlights","{}")]))` (reported once). → `hidden_retries` count=1 (High); per T3.1's worked example root `Value == 0.90` and the child `Value == 0.50`.
- [ ] **missing_tool_calls**: chat emits `SearchHotels`; `agent`'s `ToolCalls` omit it. → `missing_tool_calls` count=1 (High).
- [ ] **phantom_tool_calls**: `agent` reports `DeleteAll`; chat has no matching `FunctionCallContent`/`ToolCall`. → `phantom_tool_calls` count=1 (High).
- [ ] **argument_drift**: both report `Book`, but chat args `{"city":"NRT"}` vs agent args `{"city":"Tokyo"}`. → `argument_drift` count=1 (Medium).
- [ ] **token_under_reporting**: chat turns sum to 300 tokens (`ChatResp(0,prompt:200,completion:100)`); `agent = Agent(new TracePerformance{ TotalPromptTokens=100, TotalCompletionTokens=50 }, …)` (150, >2% gap). → `token_under_reporting` count=1 (Low).
- [ ] **suppressed_finish_reason**: a chat turn has `ChatResp(0, finish:"content_filter")`; agent boundary reports `stop`/none. → `suppressed_finish_reason` count=1 (Critical).
- [ ] **clean pair**: identical tool calls/args, matching token totals, no filtered finish → all six children `Value == 1.0`, root `Value == 1.0`.

#### T3.8 — Docs
Create 📁 `docs/benchmarks/trace-fidelity.md`: what it detects, the rubric table (T3.1), the CLI usage, and the "upstream loop to microsoft/agent-framework" note (proposal §5.4). Link from 📁 `docs/benchmarks.md`.
- [ ] Page exists and links resolve.

**Phase 3 DoD**
- [ ] Build clean; T3.* green; `bench --list` and `bench trace-fidelity` work; contract test green.
- [ ] **Phase 3-R — Revision Gate (§4.5)** green (focus: rubric math pinned by divergence test; all six discrepancy classes covered; EvalResult tree via `EvalTreeLimits`).

---

### Phase 4 — Runtime policy gate: `EvalGatingChatClient` + `IChatGate` (`AgentEval.Core` / `AgentEval.Guardrails`)

**Goal.** Run policy checks **inline** — pre-flight (refuse bad input) and post-flight (block/redact bad output) — on live chat traffic, writing every decision into the trace.
**Depends on.** Phase 0 ✅ (trace v1.1), **Phase 1 (T1.6 `ToolCorrelationScope` — the gate reads `ToolCorrelationScope.Current` for the verdict's `correlationId`; it is defined in Core/T1.6, and the gate's `using AgentEval.Tracing;` imports it).** **Per C12, this phase first defines the missing `IChatGate` abstraction.**
**Release.** v0.11.

📁 `src/AgentEval.Core/Guardrails/IChatGate.cs` (new) · 📁 `…/GateVerdict.cs` (new) · 📁 `…/EvalGatePolicy.cs` (new) · 📁 `…/EvalGateRefusalException.cs` (new) · 📁 `…/EvalGatingChatClient.cs` (new) · 📁 `…/EvalGatingChatClientExtensions.cs` (new) · 📁 `…/Gates/RegexPiiGate.cs` (new) · 📁 `…/Gates/TokenInjectionGate.cs` (new) · 📁 `…/Gates/SafetyMetricGate.cs` (new) · tests.

#### T4.1 — The gate abstraction (`IChatGate`, `GateVerdict`, `EvalGatePolicy`)
Create 📁 `GateVerdict.cs`, `EvalGatePolicy.cs`, `IChatGate.cs`:
```csharp
namespace AgentEval.Guardrails;

/// <summary>What a gate decided about a chat turn.</summary>
public enum GateAction { Allow, Block, Redact }

/// <summary>How a gate's adverse verdict is enforced.</summary>
public enum EvalGatePolicy
{
    /// <summary>Record the verdict into the trace, let the call through (default; CI→prod safe).</summary>
    WarnOnly,
    /// <summary>Throw <see cref="EvalGateRefusalException"/> before/after the call.</summary>
    ThrowOnFail,
    /// <summary>Mutate messages (pre) or response (post) and proceed. With post-gates, rejected for streaming at RUNTIME (eager throw in GetStreamingResponseAsync) — see T4.3.</summary>
    Redact,
}

/// <summary>A gate's decision for one turn.</summary>
public sealed record GateVerdict(GateAction Action, string PolicyName, string? Reason = null,
    string? RedactedText = null, IReadOnlyList<string>? Matches = null)
{
    public static GateVerdict Allow(string policy) => new(GateAction.Allow, policy);
    public static GateVerdict Block(string policy, string reason, IReadOnlyList<string>? matches = null)
        => new(GateAction.Block, policy, reason, null, matches);
    public static GateVerdict Redacted(string policy, string redactedText, IReadOnlyList<string>? matches = null)
        => new(GateAction.Redact, policy, "redacted", redactedText, matches);
}

/// <summary>A pre- or post-flight check on chat content. Pure, fast, deterministic where possible.</summary>
public interface IChatGate
{
    /// <summary>Stable policy name recorded into the trace and exceptions.</summary>
    string PolicyName { get; }
    /// <summary>Inspect text (a concatenated user prompt pre-flight, or the model reply post-flight).</summary>
    ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default);
}
```
Create 📁 `EvalGateRefusalException.cs` — wrap/derive consistent with `BehavioralPolicyViolationException` semantics (reuse `BehavioralPolicyViolationException.Create(...)` fields where natural; keep the gate exception in `AgentEval.Guardrails`):
```csharp
namespace AgentEval.Guardrails;

/// <summary>Thrown by <see cref="EvalGatingChatClient"/> under ThrowOnFail when a gate blocks a turn.</summary>
public sealed class EvalGateRefusalException : Exception
{
    public string PolicyName { get; }
    public GateAction Action { get; }
    public string Stage { get; }   // "pre" | "post"
    public EvalGateRefusalException(GateVerdict verdict, string stage)
        : base($"Gate '{verdict.PolicyName}' {verdict.Action} ({stage}): {verdict.Reason}")
        { PolicyName = verdict.PolicyName; Action = verdict.Action; Stage = stage; }
}
```
- [ ] Compiles; `GateVerdict` factory helpers covered by a small unit test.

#### T4.2 — Built-in gates (reuse existing detectors — per C12)
- 📁 `Gates/RegexPiiGate.cs`: reuse the **five PII regexes** from `PIIDetectionEvaluator` at 📁 `src/AgentEval.RedTeam/RedTeam/Attacks/PIILeakageAttack.cs`. Inline them here (verbatim, with an explicit `MatchTimeout` to prevent ReDoS — the repo enforces bounded regex evaluation per commit `c575dc1`):
  ```csharp
  private static readonly TimeSpan PiiTimeout = TimeSpan.FromMilliseconds(100);
  private static readonly (string Name, Regex Pattern)[] Patterns =
  {
      ("Email",      new Regex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled, PiiTimeout)),
      ("Phone_US",   new Regex(@"\b\d{3}[-.]?\d{3}[-.]?\d{4}\b",                  RegexOptions.Compiled, PiiTimeout)),
      ("SSN",        new Regex(@"\b\d{3}-\d{2}-\d{4}\b",                          RegexOptions.Compiled, PiiTimeout)),
      ("CreditCard", new Regex(@"\b\d{4}[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{4}\b",     RegexOptions.Compiled, PiiTimeout)),
      ("IP_Address", new Regex(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",        RegexOptions.Compiled, PiiTimeout)),
  };
  ```
  `PolicyName => "pii-detection"`. `InspectAsync` runs each pattern over `text`; on any match return `GateVerdict.Block("pii-detection", $"PII detected: {matchedNames}", matches)` — or, when used post-flight in Redact mode, `GateVerdict.Redacted("pii-detection", redacted, matches)` where each match is replaced by `█` repeated to its length. Wrap matching in `try { … } catch (RegexMatchTimeoutException) { /* treat as no-match, record a warning */ }`.
- 📁 `Gates/TokenInjectionGate.cs`: an `IChatGate` over a configurable token/phrase set (mirrors `ContainsTokenEvaluator`); `Block` when an injection marker appears.
- 📁 `Gates/SafetyMetricGate.cs`: an **adapter** wrapping any `ISafetyMetric` (e.g. `ToxicityMetric`). ⚠️ `EvaluationContext.Input` and `.Output` are **`required`** (§1.4), so set both in the initializer: `new EvaluationContext { Input = text, Output = text }`. Call `metric.EvaluateAsync(ctx, ct)`; map `MetricResult.Passed == false` → `GateVerdict.Block(metric.Name, result.Explanation ?? "safety metric failed")`, else `Allow`. Ctor: `SafetyMetricGate(ISafetyMetric metric, string? policyName = null)` with `PolicyName => policyName ?? metric.Name`.
- [ ] Each gate has a unit test: a known SSN triggers `RegexPiiGate`; a known injection token triggers `TokenInjectionGate`; a failing `ISafetyMetric` (use a fake metric) triggers `SafetyMetricGate`.

#### T4.3 — `EvalGatingChatClient`
Create 📁 `EvalGatingChatClient.cs`:
```csharp
using System.Diagnostics;
using System.Threading;          // Interlocked (also an implicit using when ImplicitUsings=enable; explicit here for clarity)
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.Guardrails;

/// <summary>
/// Applies <see cref="IChatGate"/> checks pre- and post-model-call on live traffic, recording every
/// decision into the <see cref="AgentTrace"/>. Same policy intent as the post-hoc behavioural
/// assertions, applied inline. ⚠️ <b>Redact OR ThrowOnFail</b> combined with post-gates is NOT supported
/// for streaming (the builder cannot know in advance whether the caller streams, so this client throws at
/// the START of GetStreamingResponseAsync in those configurations — the full output cannot be inspected,
/// blocked, or redacted once bytes are in flight). Streaming with WarnOnly, or with pre-gates only, is fine.
/// </summary>
public sealed class EvalGatingChatClient : DelegatingChatClient
{
    private readonly IReadOnlyList<IChatGate> _pre;
    private readonly IReadOnlyList<IChatGate> _post;
    private readonly EvalGatePolicy _policy;
    private readonly AgentTrace? _trace;

    public EvalGatingChatClient(IChatClient inner, IReadOnlyList<IChatGate>? pre, IReadOnlyList<IChatGate>? post,
        EvalGatePolicy policy, AgentTrace? trace = null) : base(inner)
    { _pre = pre ?? []; _post = post ?? []; _policy = policy; _trace = trace; }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var msgs = new List<ChatMessage>(messages);                   // FIX(PLAN-001): always a fresh mutable list — pre-gate Redact may rewrite a message in place
        await ApplyPreAsync(msgs, ct);                                // mutates msgs in place; may throw (ThrowOnFail) or rewrite (Redact)
        var resp = await base.GetResponseAsync(msgs, options, ct);
        return await ApplyPostAsync(resp, ct);                        // may throw (ThrowOnFail) or rewrite (Redact)
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        // FIX(DEFECT-6): runtime guard (the builder can't know streaming will be used). BOTH Redact AND
        // ThrowOnFail with post-gates are unsupported for streaming — the full output cannot be inspected/
        // blocked/redacted before bytes are in flight. Reject LOUDLY rather than silently downgrading to WarnOnly.
        if (_post.Count > 0 && (_policy == EvalGatePolicy.Redact || _policy == EvalGatePolicy.ThrowOnFail))
            throw new NotSupportedException(
                $"EvalGatePolicy.{_policy} with post-gates is not supported for streaming responses: the full " +
                "output cannot be inspected before transmission. Use non-streaming, or WarnOnly for streaming.");
        return StreamCore(messages, options, ct);

        async IAsyncEnumerable<ChatResponseUpdate> StreamCore(
            IEnumerable<ChatMessage> m, ChatOptions? o,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken c)
        {
            var msgs = new List<ChatMessage>(m);
            await ApplyPreAsync(msgs, c);                             // pre-gates apply to streaming too
            await foreach (var u in base.GetStreamingResponseAsync(msgs, o, c).WithCancellation(c))
                yield return u;
            // Post-gates do not run on streams: WarnOnly post-gates are a no-op here (no full text to inspect);
            // Redact/ThrowOnFail post-gate configs were rejected above. Pre-gates fully apply.
        }
    }

    // --- gate plumbing (private) ---
    // FIX(DEFECT-C/F): gate verdicts are recorded at the TRACE level (AgentTrace.Metadata), NOT as
    // TraceEntry instances. Synthetic Request/Response entries would collide with the per-round-trip
    // Index pairing space (§3.2) and corrupt TraceReplayingAgent.BuildRequestResponsePairs(). Trace-level
    // Metadata keeps gate evidence fully out of the pairing/replay path while still being serialized,
    // hash-anchored, and surfaced to Mission Control / compliance (Phase 6). `msgs` is always a fresh
    // List<> (see callers), so the in-place Redact index-set is safe.
    private async Task ApplyPreAsync(List<ChatMessage> msgs, CancellationToken ct)
    {
        if (_pre.Count == 0) return;
        // Inspect AND redact the SAME message so a Redact verdict is never silently dropped: target the last
        // User message; if there is none, fall back to the last message of any role. (Aligning inspect+redact
        // to one target avoids the old "join-all-but-redact-one" inconsistency / no-op when no User msg exists.)
        var userIdx = LastIndexOfRole(msgs, ChatRole.User);
        var targetIdx = userIdx >= 0 ? userIdx : msgs.Count - 1;
        var text = targetIdx >= 0 ? msgs[targetIdx].Text ?? "" : "";
        foreach (var gate in _pre)
        {
            var v = await gate.InspectAsync(text, ct);
            Record(v, "pre");
            if (v.Action == GateAction.Allow) continue;
            if (_policy == EvalGatePolicy.ThrowOnFail) throw new EvalGateRefusalException(v, "pre");
            if (_policy == EvalGatePolicy.Redact && v.Action == GateAction.Redact && v.RedactedText is not null && targetIdx >= 0)
            {
                msgs[targetIdx] = new ChatMessage(msgs[targetIdx].Role, v.RedactedText);   // preserve role; safe: msgs is a mutable List<>
                text = v.RedactedText;                                                     // chain subsequent gates over redacted text
            }
            // WarnOnly (or Block under WarnOnly): recorded, proceed.
        }
    }

    private async Task<ChatResponse> ApplyPostAsync(ChatResponse resp, CancellationToken ct)
    {
        if (_post.Count == 0) return resp;
        var text = resp.Text ?? "";
        foreach (var gate in _post)
        {
            var v = await gate.InspectAsync(text, ct);
            Record(v, "post");
            if (v.Action == GateAction.Allow) continue;
            if (_policy == EvalGatePolicy.ThrowOnFail) throw new EvalGateRefusalException(v, "post");
            if (_policy == EvalGatePolicy.Redact && v.Action == GateAction.Redact && v.RedactedText is not null)
            {
                resp = new ChatResponse(new ChatMessage(ChatRole.Assistant, v.RedactedText))
                       { FinishReason = resp.FinishReason, ModelId = resp.ModelId, Usage = resp.Usage };
                text = v.RedactedText;
            }
        }
        return resp;
    }

    /// <summary>
    /// Records a gate verdict into the trace's top-level <see cref="AgentTrace.Metadata"/> under a unique
    /// key "gate.{stage}.{seq}.{PolicyName}" (stage = pre|post). Recorded at the TRACE level — never as a
    /// TraceEntry — so gate evidence never participates in Request/Response Index pairing or replay
    /// (see FIX(DEFECT-C/F) above). The schema of the recorded value is documented in §3.1. No-op when no trace is attached.
    /// </summary>
    private void Record(GateVerdict v, string stage)
    {
        if (_trace is null) return;
        _trace.Metadata ??= new Dictionary<string, object>();
        var seq = Interlocked.Increment(ref _gateSeq);
        _trace.Metadata[$"gate.{stage}.{seq}.{v.PolicyName}"] = new Dictionary<string, object?>
        {
            ["action"] = v.Action.ToString(), ["reason"] = v.Reason,
            ["matches"] = v.Matches, ["correlationId"] = ToolCorrelationScope.Current,
        };
    }
    private int _gateSeq;

    private static int LastIndexOfRole(IList<ChatMessage> m, ChatRole role)
    { for (var i = m.Count - 1; i >= 0; i--) if (m[i].Role == role) return i; return -1; }
}
```
> ⚠️ Determinism notes for the implementer: gates run **in list order**; under `ThrowOnFail` the **first** non-Allow verdict throws (later gates don't run); under `Redact` each gate sees the text as redacted by prior gates (chained); under `WarnOnly` **all** gates run and **all** verdicts are recorded. **Every verdict is recorded into `AgentTrace.Metadata` (trace-level), keyed `gate.{pre|post}.{seq}.{PolicyName}` — never as a `TraceEntry`** (so gate evidence stays out of the Index pairing / replay path; see §3.1 for the value schema). Streaming runs pre-gates only; `Redact`/`ThrowOnFail`+post-gates are rejected up front (not silently downgraded).

#### T4.4 — Builder extension
Create 📁 `EvalGatingChatClientExtensions.cs`:
```csharp
public static ChatClientBuilder UseEvalGate(this ChatClientBuilder builder,
    IReadOnlyList<IChatGate>? pre = null, IReadOnlyList<IChatGate>? post = null,
    EvalGatePolicy policy = EvalGatePolicy.WarnOnly, AgentTrace? trace = null) =>
    builder.Use(inner => new EvalGatingChatClient(inner, pre, post, policy, trace));
```
> ⚠️ The Redact-streaming rejection is enforced at **runtime** (in `GetStreamingResponseAsync`), **not** at build time — the builder cannot know whether the caller will stream. This corrects the earlier "reject at build time" wording.
- [ ] `client.AsBuilder().UseEvalGate(pre:[...]).UseFunctionInvocation().UseTraceRecording("a",trace).Build()` composes (corrected order — gate pre is outermost, recorder inner of FICC per the Phase 1 composition rule).

#### T4.5 — Tests
- [ ] **Pre-gate ThrowOnFail blocks**: a `TokenInjectionGate` at `ThrowOnFail` throws `EvalGateRefusalException` **before** the inner client is called (assert inner `CallCount==0`).
- [ ] **Post-gate Redact scrubs**: a `RegexPiiGate` at `Redact` replaces a scripted SSN (`123-45-6789`) in the response with `█`s; the returned `ChatResponse.Text` contains no SSN.
- [ ] **WarnOnly never short-circuits**: with a blocking gate at `WarnOnly`, the call proceeds and a verdict is recorded in the trace.
- [ ] **Streaming + Redact rejected**: calling `GetStreamingResponseAsync` on a `Redact`+post-gate client throws `NotSupportedException` eagerly (before any update is yielded).
- [ ] **Streaming + ThrowOnFail rejected (no silent downgrade — DEFECT-6)**: calling `GetStreamingResponseAsync` on a `ThrowOnFail`+post-gate client also throws `NotSupportedException` eagerly; it is **not** silently downgraded to WarnOnly. (Streaming with WarnOnly post-gates, or pre-gates only, does not throw.)
- [ ] **Verdicts recorded at trace level, not as entries (PLAN-002 / DEFECT-C/F)**: with a trace attached, pre- and post-gate verdicts appear in `trace.Metadata` under keys matching `gate.pre.*` / `gate.post.*` (value carries `action`/`reason`/`matches`/`correlationId`). Assert **no new `TraceEntry` was added by the gate** (`trace.Entries.Count` is unchanged by gating alone), so gate evidence never collides with the Index pairing space.
- [ ] **Read-only input is safe (PLAN-001)**: passing a read-only/immutable `IEnumerable<ChatMessage>` (e.g. `ImmutableArray`) to a `Redact` pre-gate that rewrites the user message does **not** throw (the client copies to a mutable `List<>` first).
- [ ] Every decision (allow/block/redact) is recorded in `AgentTrace.Metadata` (when a trace is supplied).

#### T4.6 — Docs
Edit 📁 `docs/tracing.md` or a new 📁 `docs/guardrails.md`: the runtime-gate vs post-hoc-assertion distinction (proposal §6 callout), the three policies, the composition-order recommendation, and the "pair with provider moderation; not a sole safety layer" caveat.
- [ ] Page exists; composition snippet matches T4.4.

**Phase 4 DoD**
- [ ] Build clean; T4.* green; `AgentEval.Guardrails` namespace created under `src/AgentEval.Core/Guardrails/`.
- [ ] **Phase 4-R — Revision Gate (§4.5)** green (focus: streaming Redact/ThrowOnFail+post rejected eagerly; verdicts in trace Metadata not entries; read-only input safe; gates reuse PII regexes DRY).

---

### Phase 5a — Workflow chat-boundary capture: pre-wiring docs (`AgentEval.MAF` — docs only)

**Goal.** Document the supported v0.11 pattern for capturing chat-boundary detail inside MAF workflows: wrap each executor's `IChatClient` with `UseTraceRecording` **before** handing it to `WorkflowBuilder`. **No AgentEval code change.**
**Depends on.** Phase 1 ✅.
**Release.** v0.11.

📁 `docs/workflows.md` (edit) · 📁 a runnable snippet in the auto-audit sample (Phase 9) is the worked example.

#### T5a.1 — Document the pattern
Edit 📁 `docs/workflows.md`: add "Chat-boundary capture inside workflows (v0.11 pattern)" showing, for each agent factory, building its `IChatClient` with the **inner-of-FICC composition rule** (per Phase 1): if the executor's client composes `UseFunctionInvocation`, put `UseTraceRecording` **after** it — `raw.AsBuilder().UseFunctionInvocation().UseTraceRecording(executorId, perExecutorTrace).Build()`; if MAF's runtime drives the tool loop (no FICC in the chain), `raw.AsBuilder().UseTraceRecording(executorId, perExecutorTrace).Build()` suffices. Then construct the agent from the traced client before `WorkflowBuilder.BindAsExecutor(...)`. State the v0.12 transparent-hook (Phase 5b) is the future convenience.
- [ ] Section present; references the TripPlanner executor IDs (`["TripPlanner","FlightReservation","HotelReservation","Presenter"]`); flagged in release notes as the main v0.11 workflow fit-gap.

**Phase 5a DoD** — [ ] Docs build; pattern matches Phase 1 API exactly. · [ ] **Phase 5a-R — Revision Gate (§4.5)** green (focus: snippet compiles vs Phase 1 API; executor IDs correct).

---

### Phase 6 — Compliance evidence plumbing (`AgentEval.Compliance.*`) — v0.12

**Goal.** Surface per-turn gate/fidelity verdicts into GDPR (Art. 32) and EU AI Act (Art. 14) evidence packs, hash-anchored into the existing audit chain. **Additive, backwards-compatible.**
**Depends on.** Phase 3 ✅, Phase 4 ✅.
**Release.** v0.12.

📁 `src/AgentEval.Compliance.Core/*` (shared model/helper — **new project per ADR-018**) · 📁 `src/AgentEval.Compliance.Gdpr/*` · 📁 `src/AgentEval.Compliance.EuAiAct/*` · existing exporters (no exporter code change — schema-driven).

> ⚠️ Per **ADR-018** (merged in `744da5c`), regulation-neutral building blocks now live in `AgentEval.Compliance.Core` (the GDPR/EU AI Act packs share it). Put the **shared** per-turn-evidence model/extractor in `Compliance.Core`; keep only the regulation-specific wiring (which article/pillar it attaches to) in each pack. ⚠️ **Calibration is frozen** (ADR-018 hard constraint): this phase must NOT change any scoring weight, pass threshold, pillar/article definition, aggregation rule, or judge constant — the new evidence field is **purely additive metadata**, never an input to a score.

#### T6.1 — Optional evidence field
Add an **optional** evidence field (in `AgentEval.Compliance.Core`'s shared evidence model) that carries a compact per-turn summary (gate verdicts from `AgentTrace.Metadata` + trace-fidelity root score) sourced from the v1.1 trace. Populate it only when a v1.1 chat-boundary trace and/or a gate trace is present; leave null otherwise (so v1.0 runs are unchanged).
- [ ] Existing GDPR/EU AI Act evidence tests still pass unchanged (additive field defaults null).
- [ ] When a v1.1 trace with gate verdicts is supplied, the evidence pack includes the per-turn summary and it is hash-anchored (the existing `RunManifest.ContentHash`/audit-chain test still verifies).
- [ ] Exporters (JUnit/Markdown/JSON/SARIF/PDF) render the new field automatically (schema-driven) — assert at least the Markdown exporter shows it.

**Phase 6 DoD** — [ ] Build clean; compliance + exporter tests green; no v1.0 regression. · [ ] **Phase 6-R — Revision Gate (§4.5)** green (focus: calibration frozen — zero scoring/threshold change; additive field only).

---

### Phase 5b — Workflow transparent adapter hook (`AgentEval.MAF`) — v0.12

**Goal.** Auto-inject chat-boundary recording per executor by extending `MAFWorkflowAdapter`/`MAFWorkflowEventBridge` (which `WorkflowTraceRecorder` already builds on), populating each `ExecutorResult` — no user pre-wiring required.
**Depends on.** Phase 1 ✅, Phase 5a ✅ (documents the fallback).
**Release.** v0.12.

📁 `src/AgentEval.MAF/` (extend `MAFWorkflowAdapter`, `MAFWorkflowEventBridge`) · tests.

#### T5b.1 — Adapter hook
Add an opt-in hook so the adapter wraps each executor's `IChatClient` with `UseTraceRecording` (and optionally the gate) automatically, accumulating a per-executor `AgentTrace` exposed on the `WorkflowExecutionResult`/`ExecutorResult`.
- [ ] A workflow run produces a per-executor chat-boundary trace without the user wiring `UseTraceRecording` manually.
- [ ] ⚠️ Document the trade-off (proposal §11 Phase 5b): relies on adapter-level event fidelity; **not byte-identical** to direct chat-boundary capture. Release notes call this out.

**Phase 5b DoD** — [ ] Build clean; adapter test green; fallback (5a) still documented. · [ ] **Phase 5b-R — Revision Gate (§4.5)** green (focus: per-executor trace fidelity; not byte-identical caveat documented).

---

### Phase 7 — Mission Control GraphQL per-turn drill-down (`AgentEval.MissionControl` + SPA) — v0.13

**Goal.** Add a `ChatTurn` GraphQL type and a `Turns` projection so the per-turn timeline is browsable inside the eval-result tree. (Per C17, the raw trace waterfall already shows turns via REST after Phase 1; this is the eval-integrated view.)
**Depends on.** Phase 1 ✅ (trace), Phase 3 ✅ (eval tree).
**Release.** v0.13.

📁 `src/AgentEval.MissionControl/GraphQL/ChatTurnType.cs` (new) · 📁 `GraphQL/Query.cs` (edit) · 📁 `McHost.cs` (edit) · 📁 SPA: `src/lib/eval-tree.ts`, `components/ChatTurnsTimeline.tsx` (new), `pages/ScenarioTreePage.tsx` (edit).

#### T7.1 — GraphQL `ChatTurn` type + resolver
Add a `public record ChatTurn(string Role, string? Content, IReadOnlyList<string>? ToolDefinitions, int? PromptTokens, int? CompletionTokens, string? FinishReason, long? LatencyMs, IReadOnlyDictionary<string,string>? ProviderMetadata)` in `namespace AgentEval.MissionControl.GraphQL` (Hot Chocolate **convention discovery** — no `ObjectType<>`). Add a resolver on `Query` (e.g. `ChatTurns(runId, scenarioId)`) that reads the run's `AgentTrace` (via `IOutputStoreReader`) and projects `Scope==ChatTurn` entries.
- [ ] GraphQL schema exposes `ChatTurn` and the new query; existing queries unaffected.

#### T7.2 — Depth + SPA
Bump `McHost.cs` `MaxExecutionDepth` `10`→`12` (verify with a 4-level tree + turns test). Add the `ChatTurnNode` TS interface to `eval-tree.ts`, a `ChatTurnsTimeline.tsx` component, and wire it into `EvalResultNode.tsx`/`ScenarioTreePage.tsx` (hand-written query — **no codegen** today; update the query string).
- [ ] A GraphQL smoke test queries a 4-level tree + turns successfully at depth ≤12 and is rejected beyond.
- [ ] SPA renders a per-turn timeline section under a result node (manual run or component test).

**Phase 7 DoD** — [ ] Build clean; GraphQL smoke test green; SPA builds (`npm run build` in `…Spa`). · [ ] **Phase 7-R — Revision Gate (§4.5)** green (focus: depth-limit raised safely; existing GraphQL queries unaffected).

---

### Phase 8 — CLI `doctor`/`migrate` + Observability sample (`AgentEval.Cli` + `AgentEval.Samples`)

**Goal.** Teach `doctor` to warn on double-wrapping, `migrate` to handle v1.0→v1.1, and ship the first showcase sample.
**Depends on.** Phases 0–4 ✅.
**Release.** v0.11.

📁 `src/AgentEval.Cli/Commands/DoctorCommand.cs` (edit) · 📁 `src/AgentEval.Cli/Commands/MigrateCommand.cs` (edit) · 📁 `samples/AgentEval.Samples/Observability/01_GlassBoxFullStack.cs` (new) · 📁 `samples/AgentEval.Samples/Program.cs` (edit) · tests.

#### T8.1 — `doctor` double-wrapping check (per C16)
Add a sixth check to `DoctorCommand.RunAsync()`: when loading a trace, warn if it contains **both** agent-boundary entries (`EffectiveScope==AgentInvocation`, `Type∈{Request,Response}`) **and** `ChatTurn` entries that indicate the same agent was wrapped at both layers (heuristic: same `AgentName`, overlapping timestamps). Emit: *"Warning: chat-boundary recording detected inside an agent-boundary recorder; entries may be duplicated. See docs/tracing.md §Two recording layers."*
- [ ] A synthetic doubly-wrapped trace triggers the warning; a single-layer trace does not.

#### T8.2 — `migrate` v1.0→v1.1 (per C16)
Extend `MigrateCommand` to load v1.0 `.trace.json` files via `TraceSerializer` and rewrite them with `Version="1.1"` (no field transformation needed — additive). Print a per-file summary.
- [ ] Running migrate on the v1.0 fixture (T0.2) produces a `Version=="1.1"` file that still deserializes; entries' `Scope` remain null (`EffectiveScope==AgentInvocation`).

#### T8.3 — Observability sample
Create 📁 `samples/AgentEval.Samples/Observability/01_GlassBoxFullStack.cs` (`static class` with `RunAsync()`): a single agent whose `IChatClient` is built with the **corrected composition order** (recorder inner of FICC — see the Phase 1 composition rule):
```csharp
using var _corr = new ToolCorrelationScope("inv-glassbox-demo");   // caller establishes the per-invocation id
var client = raw.AsBuilder()
    .UseEvalGate(pre: [injectionGate])              // outermost — original user input
    .UseFunctionInvocation()                        // tool loop — calls inner once per round-trip
    .UseTraceRecording("demo", trace)               // INNER of FICC → one entry per real round-trip ✅
    .UseEvalGate(post: [piiGate])                   // post-gate over each model reply
    .Build();
```
with one tool wrapped via `.WithEvaluation(trace)` and deliberately blocked, then prints the resulting trace + a Trace Fidelity reconciliation. Register it in 📁 `Program.cs`'s menu under a new `Observability` group.
- [ ] Sample compiles and runs against `ScriptedChatClient` (offline) and prints per-turn entries (one per round-trip), a gate verdict, a blocked tool, and a fidelity score. All entries share `CorrelationId == "inv-glassbox-demo"`.

**Phase 8 DoD** — [ ] Build clean; T8.* green; sample appears in the samples menu. · [ ] **Phase 8-R — Revision Gate (§4.5)** green (focus: doctor/migrate don't regress existing checks; sample runs offline).

---

### Phase 9 — Auto-audit flagship sample + `bench autoaudit` (`AgentEval.Cli` + `AgentEval.Samples`)

**Goal.** The flagship: run the TripPlanner workflow against four endpoints (2 local, 2 hosted) with the full stack and produce a cross-endpoint comparison report.
**Depends on.** Phases 1–4 ✅, 5a ✅; reuses `WorkflowEvaluationHarness`, `RedTeamRunner`, GDPR/EU AI Act, `StochasticRunner`/`ModelComparer` (all existing).
**Release.** v0.11.

📁 `samples/AgentEval.Samples/Observability/02_AutoAudit.cs` (new) · 📁 `samples/.../endpoints.sample.yml` (new) · 📁 `src/AgentEval.Cli/Commands/BenchAutoAuditCommand.cs` (new) + `Program.cs`/`BenchListCommand` edits · 📁 `EndpointFactory`/`JudgeFactory` wiring (per C10) · tests.

#### T9.1 — Reuse the TripPlanner workflow
Call `TripPlannerWorkflow.Create()` (📁 `samples/AgentEval.TravelDemo/Workflows/TripPlannerWorkflow.cs`), pre-wire each executor's client with recording + gates (Phase 5a pattern), wrap tools with `.WithEvaluation`.
- [ ] Sample unpacks `(workflow, executorIds)` and applies per-executor recording.

#### T9.2 — Multi-endpoint config + `openai-compatible` wiring (per C10)
Define an `endpoints.yml` schema (4 entries: Ollama-Llama3.1, LM Studio Qwen via `EndpointFactory.CreateOpenAICompatible`; Azure GPT-4o-mini, DeepSeek-V3). Thread `--endpoint/--model/--api-key` (or the YAML) into the bench judge path by extending `JudgeFactory.Resolve(...)` to accept optional endpoint/model/apiKey and dispatch to `CreateOpenAICompatible` when present (Azure remains the default).
- [ ] `JudgeFactory` resolves an OpenAI-compatible judge when endpoint/model are supplied; Azure path unchanged when env vars are used.

#### T9.3 — `bench autoaudit` handler
Create 📁 `BenchAutoAuditCommand.cs`: `agenteval bench autoaudit --workflow <file> --endpoints <yml> --preset audit-grade --runs 5`. For each endpoint: run the workflow harness, GDPR + EU AI Act, RedTeam per executor, Trace Fidelity per executor (Phase 3), and `StochasticRunner`/`ModelComparer` for N runs. Emit a per-endpoint report and a cross-endpoint comparison (Markdown + PDF + SARIF) under `.agenteval/`, surfaced in Mission Control. Wire into `Program.cs` + `BenchListCommand.AnchorAssemblies()`.
- [ ] `agenteval bench --list` shows `autoaudit` (or it lives under `bench autoaudit`); `--help` documents options.
- [ ] A reduced offline run (`ScriptedChatClient` endpoints, `--runs 1`) produces a comparison report ranking endpoints on workflow correctness, compliance, fidelity, red-team resistance, and cost.
- [ ] Timing note recorded (proposal target: <15 min Standard; <90 min AuditGrade×5) — informational, not CI-gated.

#### T9.4 — Docs
Create 📁 `docs/showcase/auto-audit.md` documenting the scenario, the YAML, and the one-command invocation.
- [ ] Page exists; `endpoints.sample.yml` referenced.

**Phase 9 DoD** — [ ] Build clean; offline autoaudit run produces the comparison report; T9.* green. · [ ] **Phase 9-R — Revision Gate (§4.5)** green (focus: reuses existing runners DRY; endpoint config documented).

---

### Phase 10 — Consolidate + publish the CLI tool + ADRs (repo + `AgentEval.Cli`)

**Goal.** Finish the CLI-as-dotnet-tool job (most of it already in the csproj — per C9) and land the two ADRs.
**Depends on.** Phases 0–4 (so the shipped tool exercises the new surface).
**Release.** v0.11.

📁 `.github/workflows/release.yml` (edit) · 📁 `README.md`, `docs/cli.md`, `docs/getting-started.md` (edit) · 📁 `docs/adr/019-chat-boundary-two-layer-recording.md` (new) · 📁 `docs/adr/020-agenttrace-v1_1-schema.md` (new) · 📁 `docs/adr/README.md` (edit).

#### T10.1 — Release workflow pack/publish
The csproj already has `PackAsTool/ToolCommandName/PackageId` (C9). Add a pack+publish step to `release.yml` that runs `dotnet pack src/AgentEval.Cli -c Release -p:PackageVersion=$VERSION` and pushes the resulting `AgentEval.Cli` package. First release: `AgentEval.Cli 0.11.0-beta`.
- [ ] CI packs `AgentEval.Cli` and the umbrella `AgentEval` in one run; `dotnet tool install -g AgentEval.Cli --prerelease` then `agenteval doctor` works (verify locally with a local feed).

#### T10.2 — Docs lead with the tool
Update README/`docs/cli.md`/`docs/getting-started.md` to lead with `dotnet tool install -g AgentEval.Cli --prerelease`; keep `dotnet run --project src/AgentEval.Cli` as the from-source fallback. Add the standalone-repo deprecation note.
- [ ] Docs build; primary install path is the tool.

#### T10.3 — ADR-019 & ADR-020 (per C21; template §4.4)
- 📁 `019-chat-boundary-two-layer-recording.md`: the two-layer recording model (agent boundary + chat boundary), placement in `AgentEval.Core` (MEAI-only), and the `EvaluatingAIFunction` tool seam. Reference ADR-004/-016/-017/-018.
- 📁 `020-agenttrace-v1_1-schema.md`: the additive v1.1 fields, the nullable-`Scope` back-compat decision (C5), and the tool-definition de-dup helper.
- Add both rows to 📁 `docs/adr/README.md`.
- [ ] Both ADRs follow the template; README index updated; status `Accepted`.

**Phase 10 DoD** — [ ] Build/pack clean in one CI run; tool installs and runs; ADRs filed. · [ ] **Phase 10-R — Revision Gate (§4.5)** green (focus: `dotnet tool install` smoke test; ADR-019/020 indexed).

---

## 6. Sequencing & dependency graph

```
v0.11 ─┬─ Phase 0 (schema + ScriptedChatClient)        [no deps]
       ├─ Phase 1 (TraceRecordingChatClient)           ← 0
       ├─ Phase 2 (EvaluatingAIFunction)               ← 0
       ├─ Phase 3 (Trace Fidelity + CLI handler)       ← 0,1  (2 optional)
       ├─ Phase 4 (EvalGatingChatClient + IChatGate)   ← 0, 1 (T1.6 ToolCorrelationScope)
       ├─ Phase 5a (workflow pre-wiring docs)          ← 1
       ├─ Phase 8 (doctor/migrate + Observability sample) ← 0,1,2,3,4
       ├─ Phase 9 (auto-audit + bench autoaudit)       ← 1,2,3,4,5a
       └─ Phase 10 (publish CLI + ADRs)                ← 0,1,2,3,4

v0.12 ─┬─ Phase 6 (compliance plumbing)                ← 3,4
       └─ Phase 5b (workflow adapter hook)             ← 1,5a

v0.13 ─── Phase 7 (Mission Control per-turn)           ← 1,3
```

**Recommended build order:** 0 → 1 → 2 → 4 → 3 → 5a → 8 → 9 → 10  (then 6 → 5b → 7).
Rationale: Phase 4 before 3 lets the Observability sample (Phase 8) and auto-audit (Phase 9) use both the gate and fidelity; 3 needs 1's chat trace; 8/9 integrate everything; 10 publishes.

---

## 7. Master task checklist

**Phase 0** — [ ] T0.1 schema · [ ] T0.2 back-compat test + fixture · [ ] T0.3 de-dup helper · [ ] T0.4 ScriptedChatClient · [ ] DoD
**Phase 1** — [ ] T1.0 verify MEAI · [ ] T1.1 SamplePreset · [ ] T1.2 recorder · [ ] T1.3 builder ext · [ ] T1.4 tests · [ ] T1.5 docs · [ ] DoD
**Phase 2** — [ ] T2.1 correlation scope · [ ] T2.2 EvaluatingAIFunction · [ ] T2.3 ext · [ ] T2.4 tests · [ ] DoD
**Phase 3** — [ ] T3.1 rubric · [ ] T3.2 report · [ ] T3.3 runner · [ ] T3.4 family+registration · [ ] T3.5 CLI handler · [ ] T3.6 contract test · [ ] T3.7 reconciliation tests · [ ] T3.8 docs · [ ] DoD
**Phase 4** — [ ] T4.1 IChatGate/GateVerdict/policy/exception · [ ] T4.2 built-in gates · [ ] T4.3 EvalGatingChatClient · [ ] T4.4 builder ext + guard · [ ] T4.5 tests · [ ] T4.6 docs · [ ] DoD
**Phase 5a** — [ ] T5a.1 docs · [ ] DoD
**Phase 6** — [ ] T6.1 evidence field · [ ] DoD
**Phase 5b** — [ ] T5b.1 adapter hook · [ ] DoD
**Phase 7** — [ ] T7.1 GraphQL ChatTurn + resolver · [ ] T7.2 depth + SPA · [ ] DoD
**Phase 8** — [ ] T8.1 doctor check · [ ] T8.2 migrate · [ ] T8.3 sample · [ ] DoD
**Phase 9** — [ ] T9.1 workflow reuse · [ ] T9.2 endpoints + openai-compat · [ ] T9.3 autoaudit handler · [ ] T9.4 docs · [ ] DoD
**Phase 10** — [ ] T10.1 release pack/publish · [ ] T10.2 docs · [ ] T10.3 ADR-019/020 · [ ] DoD

---

## 8. Global "Definition of Done" (applies to the whole effort)

- [ ] `dotnet build AgentEval.sln -c Release` is clean (no new warnings on new files).
- [ ] `dotnet test AgentEval.sln -c Release` — zero new failures vs. the pre-Glass-Box baseline.
- [ ] No public API of an existing type changed (Glass Box is additive — §8 of the proposal). The only schema change is `AgentTrace.Version` `1.0`→`1.1` with additive nullable fields.
- [ ] **Surfaces touched (all additive):** `AgentEval.Core` (trace v1.1, chat client, gate, Trace Fidelity), `AgentEval.MAF` (tool wrapper), `AgentEval.MissionControl` (GraphQL, Phase 7), and the internal `AgentEval.Compliance.*` suites (Phase 6, bundled via `PrivateAssets=all`). **`AgentEval.Abstractions` is NOT touched** (the trace model lives in Core — see C2/C3). The result model, fluent-assertion DSL, existing metrics, and existing recorders are untouched (proposal §8.1).
- [ ] Every new public type has XML docs + SPDX header + file-scoped namespace under `AgentEval.*`.
- [ ] New benchmark factory in `namespace AgentEval.Benchmarks`; `BenchmarkNamespaceContractTests` green.
- [ ] `agenteval bench --list` shows `trace-fidelity`; `agenteval doctor`/`migrate` handle v1.1.
- [ ] ADR-019 and ADR-020 filed and indexed.
- [ ] An Opus-grade gap review of the actual changed files has been run after each implementation batch (project standing process).

---

## Appendix A — Open questions resolved by this plan (proposal §14)

| Proposal Q | Resolution baked into this plan |
|---|---|
| Q1 per-turn detail opt-in | `SamplePreset` (T1.1): AuditGrade = full verbatim (de-dup off); Smoke/Standard = de-dup on. |
| Q2 cross-layer correlation | `ToolCorrelationScope` (AsyncLocal, T2.1) + `CorrelationId` field. Sequential-tool correlation tested; parallel-tool caveat documented. |
| Q3 Trace Fidelity scope v0.11 | MAF-first canonical scenarios (T3.7); SK/raw-loop deferred. |
| Q4 fidelity scoring rubric | Severity-weighted, test-pinned (T3.1). |
| Q5 preset propagation | Explicit `SamplePreset` parameter per wrapper (debuggable), not ambient AsyncLocal. |

## Appendix B — Files created/edited (quick index)

**New (`src`):** `Core/Tracing/{SamplePreset,TraceRecordingChatClient,TraceRecordingChatClientExtensions,ToolDefinitionDeduplicator,ToolCorrelationScope}.cs` (⚠️ `ToolCorrelationScope` is in **Core**, T1.6 — not MAF); `Core/Testing/ScriptedChatClient.cs`; `Core/Benchmarks/TraceFidelity/{TraceFidelityRunner,TraceFidelityReport,TraceFidelityBenchmark,TraceFidelityBenchmarkRegistration,TraceFidelityRunnerHostingContext}.cs`; `Core/Guardrails/{IChatGate,GateVerdict,EvalGatePolicy,EvalGateRefusalException,EvalGatingChatClient,EvalGatingChatClientExtensions}.cs` + `Core/Guardrails/Gates/{RegexPiiGate,TokenInjectionGate,SafetyMetricGate}.cs`; `AgentEval.MAF/Tracing/{EvaluatingAIFunction,EvaluatingAIFunctionExtensions}.cs` (reads Core's `ToolCorrelationScope`); `AgentEval.Cli/Commands/{BenchTraceFidelityCommand,BenchAutoAuditCommand}.cs`; `AgentEval.MissionControl/GraphQL/ChatTurnType.cs`; samples `Observability/{01_GlassBoxFullStack,02_AutoAudit}.cs` + `endpoints.sample.yml`.
**Edited (`src`):** `Tracing/AgentTrace.cs` (v1.1); `AgentEval.Cli/{Program.cs, Commands/BenchListCommand.cs, Commands/DoctorCommand.cs, Commands/MigrateCommand.cs, Commands/JudgeFactory.cs}`; `AgentEval.MAF/{MAFWorkflowAdapter, MAFWorkflowEventBridge}` (5b); `AgentEval.MissionControl/{McHost.cs, GraphQL/Query.cs}`; `AgentEval.Compliance.{Gdpr,EuAiAct}` (Phase 6); SPA `lib/eval-tree.ts`, `components/{EvalResultNode,ChatTurnsTimeline}.tsx`, `pages/ScenarioTreePage.tsx`.
**Docs:** `tracing.md`, `workflows.md`, `guardrails.md`(new), `benchmarks/trace-fidelity.md`(new), `benchmarks.md`, `showcase/auto-audit.md`(new), `cli.md`, `getting-started.md`, `README.md`, `adr/{018,019}*`(new), `adr/README.md`.
**Tests:** `Tracing/{AgentTraceV11SchemaTests,TraceRecordingChatClientTests,EvaluatingAIFunctionTests}.cs`; `Testing/ScriptedChatClientTests.cs`; `Benchmarks/{TraceFidelity reconciliation + registry integration}` tests; `Guardrails/*` tests; fixture `Fixtures/trace-v1_0-sample.json`; edits to `Benchmarks/BenchmarkNamespaceContractTests.cs`.

*End of implementation plan.*



