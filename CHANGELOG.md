# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

---

## [0.5.2-beta] - 2026-02-28

**MAF RC2 Dependency Upgrade** ⬆️

### Changed
- **MAF upgraded from 1.0.0-rc1 to 1.0.0-rc2** — All three MAF package references (`Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Agents.AI.Workflows`) updated to 1.0.0-rc2. Zero public API breaking changes — every AgentEval dependency is byte-identical between RC1 and RC2. RC2 contains only internal telemetry restructuring (session-level OTel spans in Workflows), two internal resource leak fixes, and three new additive `[Experimental]` APIs (Agent Skills, builder-level context providers, stored-output-disabled client). Transitive `Microsoft.Agents.ObjectModel` bumped `2026.2.3.1 → 2026.2.4.1`. No AgentEval source code changes required. All 7,345 tests pass across 3 TFMs. See [MAF-Upgrade-Plan.md](src/AgentEval.MAF/MAF-Upgrade-Plan.md) for full diff analysis.

---

## [0.5.1-beta] - 2026-02-28

**Modularization, Cross-Framework, CLI, DI & Extensibility** 🏗️🔌

Major architectural release: monolith split into 6 sub-projects (ADR-016), universal IChatClient adapter, CLI tool, dependency injection architecture, rich evaluation output, extensibility framework, and 27 runnable samples. 7,345 tests × 3 TFMs passing.

### Added
- **Monolith Modularization (ADR-016)** — Split single `src/AgentEval` project (~203 files, ~35K lines) into 6 internal sub-projects while shipping a single NuGet package. Resolves dependency coupling: non-MAF users no longer pull `Microsoft.Agents.AI`, non-RedTeam users no longer pull `PdfSharp-MigraDoc`. Compiler-enforced dependency direction: Abstractions → Core → DataLoaders/MAF/RedTeam → Umbrella.
  - `AgentEval.Abstractions` (~48 files) — Public contracts: `IMetric`, `IEvaluableAgent`, `IStreamableAgent`, models
  - `AgentEval.Core` (~63 files) — Implementations: metrics, assertions, tracing, comparison, DI registration
  - `AgentEval.DataLoaders` (~23 files) — Dataset loaders (JSON/JSONL/CSV/YAML), exporters, output formatting
  - `AgentEval.MAF` (7 files) — Microsoft Agent Framework integration (`MAFAgentAdapter`, `MAFEvaluationHarness`)
  - `AgentEval.RedTeam` (61 files) — Security scanning, attack types, compliance reporting, PDF export
  - `AgentEval` (umbrella) — Single NuGet package containing all 6 DLLs per TFM via `TargetsForTfmSpecificBuildOutput`
  - All sub-projects use `RootNamespace=AgentEval` — zero namespace changes, zero API surface changes
  - `PrivateAssets="all"` on umbrella ProjectReferences with explicit NuGet dependency declarations
  - `InternalsVisibleTo` on all sub-projects → `AgentEval.Tests`
  - Phase 0: Fixed 11 cross-cutting coupling anomalies before split
  - See [ADR-016](docs/adr/016-monolith-modularization.md) for full rationale and alternatives considered
- **Cross-Framework IChatClient Support** — Universal adapter pattern for evaluating any `IChatClient`-based AI agent regardless of underlying framework (Azure OpenAI, Ollama, Groq, LM Studio, Semantic Kernel, etc.):
  - `IChatClient.AsEvaluableAgent()` extension method — One-liner wrapping any `IChatClient` as `IStreamableAgent` for evaluation. Located in `AgentEval.Core.ChatClientExtensions`. Parallels `.AsIChatClient()` from Microsoft.Extensions.AI.
  - `TestSummary.ToEvaluationReport()` extension method — Bridges evaluation pipeline (`TestSummary`) to export pipeline (`EvaluationReport` for `IResultExporter`). Derives time boundaries from `PerformanceMetrics`, maps `MetricResults` to `MetricScores`, supports `agentName`/`modelName`/`endpoint` provenance, sets `Category` for JUnit XML grouping.
  - **NuGetConsumer Semantic Kernel demo** — Real SK with `[KernelFunction]` plugins (`FlightPlugin.cs`) evaluated by AgentEval via the `AIFunctionFactory.Create()` bridge pattern. 8-step demo: Kernel build → plugin registration → SK↔M.E.AI bridge → tool assertions → code metrics → LLM-as-judge → performance summary. Isolated project with `Microsoft.SemanticKernel 1.72.0` and `Azure.AI.OpenAI 2.7.0-beta.2`. Located in `samples/AgentEval.NuGetConsumer/`.
  - **Sample 27: Cross-Framework Evaluation** — Universal IChatClient adapter pattern: `IChatClient` → `AsEvaluableAgent()` → evaluate → `ToEvaluationReport()` → export to Markdown.
  - **Documentation** — `docs/cross-framework.md` with capability table, SK bridge code example, NuGetConsumer link.
- **AgentEval CLI (`agenteval eval`)** — Evaluate any OpenAI-compatible AI agent from the command line without writing C#. Supports all providers (OpenAI, Ollama, Groq, vLLM, LM Studio, Azure OpenAI, etc.) via the Chat Completions API standard. Features: 15 CLI options, 7 export formats (json, junit, xml, markdown, md, trx, csv), LLM-as-judge via `--judge`, system prompt from file, stderr progress reporting for Unix piping, and CI/CD exit codes (0=pass, 1=fail, 2=usage error, 3=runtime error). Packaged as a .NET tool (`dotnet tool install AgentEval.Cli`). Located in `src/AgentEval.Cli/`.
- **Dependency Injection architecture (ADR-006)** — All core services registered via `services.AddAgentEval()`, `services.AddAgentEvalDataLoaders()`, `services.AddAgentEvalRedTeam()`, or `services.AddAgentEvalAll()`. Interface-first design: `IStochasticRunner`, `IModelComparer`, `IStatisticsCalculator`, `IToolUsageExtractor`, `ISnapshotComparer`, `ISnapshotStore`, and all exporters/loaders registered with appropriate lifetimes. Configurable via `AgentEvalServiceOptions` (lifetime, harness factory, logger factory). See `AgentEvalServiceCollectionExtensions`.
- **Rich Evaluation Output subsystem** — Structured output formatting moved to `AgentEval.DataLoaders/Output/` during modularization, contracts split to `AgentEval.Abstractions/Output/`:
  - `TableFormatter` — `PrintTable()`, `PrintComparisonTable()`, `PrintPerformanceSummary()`, `PrintToolSummary()` with dynamic column selection and ANSI variance color-coding.
  - `StochasticResultExtensions` — Fluent `result.PrintTable("Metrics")`, `result.PrintSummary()`, `result.PrintPerformanceSummary()`, `result.PrintToolSummary()`, `result.ToTableString()`.
  - `ComparisonResultExtensions` — `modelResults.PrintComparisonTable()`, `modelResults.ToComparisonTableString()`.
  - `OutputOptions` — 15+ toggle properties (`ShowScore`, `ShowPassRate`, `ShowDuration`, `ShowTTFT`, `ShowTokens`, `ShowCost`, `ShowToolCalls`, `ShowConfidenceInterval`, etc.) with `Default`, `Minimal`, `Full` static presets and fluent `With()` copy method.
  - `VerbosityLevel` enum (`None`/`Summary`/`Detailed`/`Full`), `VerbositySettings`, `VerbosityConfiguration` with environment variable support (`AGENTEVAL_VERBOSITY`, `AGENTEVAL_SAVE_TRACES`, `AGENTEVAL_TRACE_DIR`).
  - `EvaluationOutputWriter` — 4-mode writer (Summary/Detailed/Full/None) producing tool timelines, performance sections, metric sections, and full JSON trace to any `TextWriter`.
  - `AgentEvalTestBase` — xUnit test base class with automatic tracing, `RecordResult()`, `SaveTrace()`, `CreateResult()` fluent builder pattern (`TestResultBuilder`).
  - `TimeTravelTrace` — 22+ model classes for time-travel debugging (`ExecutionStep`, 13 `StepType` values, `ToolCallStepData`, `AgentHandoffStepData`, etc.).
  - `TraceArtifactManager` — `SaveTestResult()`, `SaveTrace()`, `LoadTrace()`, `ListTraceFiles()`, `GetMostRecentTrace()`, `CleanupOldTraces()`.
- **Exporter registry and DI auto-discovery** — Extensible exporter system with runtime registration:
  - `IExporterRegistry` interface (in Abstractions) — `Register()`, `Get()`, `GetRequired()`, `GetAll()`, `GetRegisteredFormats()`, `Contains()`, `Remove()`, `Clear()`.
  - `ExporterRegistry` implementation — Thread-safe `ConcurrentDictionary`, pre-populated with 5 built-in exporters (JSON, JUnit XML, Markdown, TRX, CSV) via DI.
  - DI auto-discovery: custom `IResultExporter` services registered in DI are automatically picked up by the registry.
  - `FormatName` default interface member on `IResultExporter` for string-based lookup.
  - `ResultExporterFactory` — Static factory with `Create(ExportFormat)` and `CreateFromExtension(string)`.
- **DataLoader factory and DI architecture** — Extensible dataset loading with runtime registration:
  - `IDatasetLoaderFactory` interface (in Abstractions) — `CreateFromExtension()`, `Create()`, `Register()`.
  - `DefaultDatasetLoaderFactory` implementation — Dictionary-based registry for `.jsonl`, `.ndjson`, `.json`, `.csv`, `.tsv`, `.yaml`, `.yml`. Constructor accepts `IEnumerable<IDatasetLoader>` for DI auto-discovery of custom loaders.
  - `DatasetLoaderFactory` refactored to static convenience façade delegating to `DefaultDatasetLoaderFactory`.
  - `IsTrulyStreaming` property on `IDatasetLoader` — distinguishes JSONL/CSV true streaming from JSON/YAML buffered loading.
  - `.ndjson` and `.tsv` file extension support added.
  - `DatasetTestCaseBenchmarkExtensions` — `ToToolAccuracyTestCase()` and `ToTaskCompletionTestCase()` bridging dataset test cases to benchmark types with `required_params` metadata mapping.
- **Benchmarking improvements** — DI integration and multi-prompt support:
  - `AgenticBenchmark` now accepts `IToolUsageExtractor?` via DI (defaults to `DefaultToolUsageExtractor.Instance` for non-DI usage).
  - `PerformanceBenchmark.RunLatencyBenchmarkAsync()` gained multi-prompt overload (`IEnumerable<string> prompts`) to avoid server-side caching and produce more representative latency measurements.
  - `AgenticBenchmarkOptions.AddDefaultCompletionCriteria` — boolean controlling auto-appended standard criteria.
  - Throughput benchmark `Task.Yield()` fixes for both success and error paths preventing deadlocks with synchronous agents.
- **Extensibility framework** — Plugin system and registry pattern for custom extensions:
  - `IMetricRegistry` — now DI-registered as singleton with auto-population from `IMetric` services.
  - `IAgentEvalPlugin` lifecycle interface — `InitializeAsync()`, `OnBeforeEvaluationAsync()`, `OnAfterEvaluationAsync()`, `ShutdownAsync()`, with `PluginId`, `Name`, `Version`, `Dependencies`.
  - `IPluginContext` — provides `Metrics` (IMetricRegistry), `Logger`, `Configuration`, `GetConfig<T>()`.
  - `IResultTransformer` — Post-processing with `Priority` ordering for composable result pipelines.
  - See Sample 26 for custom metrics, exporters, loaders, and attack registration via DI.
- **Sample 22: Responsible AI** — Toxicity, bias, misinformation metrics with counterfactual testing.
- **Sample 23: Benchmark System** — JSONL-loaded benchmarks: tool accuracy, latency, cost analysis with `DatasetTestCaseBenchmarkExtensions`.
- **Sample 24: Calibrated Evaluator** — Multi-model consensus evaluation with calibrated scoring.
- **Sample 25: Dataset Loaders** — Multi-format dataset pipeline: JSONL, JSON, YAML, CSV with `IDatasetLoaderFactory`.
- **Sample 26: Extensibility** — DI registries, custom metrics/exporters/loaders/attacks demonstrating all extension points.

### Changed
- **Snapshot Evaluation comprehensive review (28+ fixes)** — Major audit and hardening of the snapshot comparison and storage system:
  - *Interfaces & DI:* Added `ISnapshotComparer` and `ISnapshotStore` interfaces with DI registration (ADR-006 compliance). Added `InternalsVisibleTo` for test project access to internal helpers.
  - *Security:* Sanitized suffix parameter in `GetSnapshotPath` to prevent path traversal (CODE-22). Added `basePath` validation in `SnapshotStore` constructor (CODE-21). Fixed `SanitizeFileName` collision resistance with SHA256 hash suffix (CODE-17).
  - *Correctness:* Fixed `JsonValueKind.Null` handling in element comparison (CODE-12). Fixed boolean type guard treating `True`/`False` as compatible types (CODE-30). Fixed `SemanticComparisonResult` to store scrubbed values (CODE-33). Fixed `ComputeSimpleSimilarity` to split on all whitespace (CODE-32). Fixed `CompareArrays` to continue comparing after length mismatch (CODE-23). Fixed `LoadAsync` TOCTOU with try/catch pattern (CODE-26/35). Fixed GUID regex word boundaries (CODE-16). Fixed duration regex word boundaries to prevent false positives (CODE-15). Fixed field name passed as parameter through recursion (CODE-20/34).
  - *Validation:* Added `SemanticThreshold` [0.0, 1.0] range validation (CODE-31). Added null guards on `Compare` method (TEST-12).
  - *New features:* Added `AllowExtraProperties` option (CODE-6). Added `Delete`, `ListSnapshots`, and `Count` to `SnapshotStore` (CODE-9/18). Added epsilon-based floating-point comparison (CODE-10). Added `CancellationToken` support on all async methods (CODE-7).
  - *Performance:* Added `RegexOptions.Compiled` on all default patterns (CODE-13). Made `JsonSerializerOptions` static in `SnapshotStore` (CODE-14).
  - *Testing:* Expanded test coverage from 23 to 51+ tests. Moved tests from `Benchmarks/` to `Snapshots/` directory (TEST-1/7). Added thread safety documentation (CODE-19). Documentation aligned with code defaults and APIs.
- **Sample 27 simplified** — Removed redundant MAF flight agent (Part B, ~350 lines) already demonstrated in Samples 2-3, 9-10, and NuGetConsumer. Now focused solely on the unique Universal IChatClient Adapter pattern.
- **Cross-framework documentation fixed** — Fixed broken Semantic Kernel code example in `docs/cross-framework.md` (replaced non-existent `AsChatClient()` method with working `AIFunctionFactory.Create()` bridge pattern). Added NuGetConsumer SK demo link. Fixed capability table footnote.
- **README updated** — Sample count corrected from 26 to 27 with Sample 27 row added. Test counts now use qualitative descriptions instead of hard-coded numbers. Added CLI, DI, and cross-framework to Key Features. Expanded documentation table.
- **Roadmap updated** — Marked Red Team and CLI as shipped; added CLI Phase 2, MCP Server, Benchmark runner, and Verify.Xunit to "What's Next". Updated version history table through 0.6.0-beta.
- **System.CommandLine upgraded from 2.0.0-beta4 to 2.0.3 stable** — Breaking API change: `SetHandler` → `SetAction`, `IsRequired` → `Required`, `AddOption()` → `Options.Add()`, `AddAlias()` → constructor aliases, `root.InvokeAsync(args)` → `root.Parse(args)` then `parseResult.InvokeAsync()`. Only affects the new CLI project; no existing code referenced System.CommandLine.
- **Test count increased from 6,573 to 7,345** — 2,435 (net8.0) + 2,455 (net9.0) + 2,455 (net10.0). New tests for DI service registration, snapshot evaluation improvements, CLI commands, cross-framework adapter, and export pipeline bridging.

### Fixed
- **Streaming tool extraction for ChatClientAgentAdapter** — `InvokeStreamingAsync` now yields `ToolCallStarted` and `ToolCallCompleted` chunks when the underlying `IChatClient` streams `FunctionCallContent`/`FunctionResultContent`. Previously, streaming evaluations via `RunEvaluationStreamingAsync` produced empty `ToolUsageReport` for all `IChatClient`-based agents. Non-streaming path was unaffected.

---

## [0.4.0-beta] - 2026-02-22

**Security, Responsible AI & MAF RC1** 🛡️🤖

Major feature release: Red Team security scanning, Responsible AI metrics, Calibrated multi-model evaluation, MAF RC1 upgrade, and comprehensive tracing improvements. 42 commits, 2,191 tests × 3 TFMs = **6,573 total tests passing**.

### ⚠️ BREAKING CHANGES

- **MAF RC1 Upgrade** - Upgraded from `Microsoft.Agents.AI 1.0.0-preview.251110.2` to `1.0.0-rc1`
  - `Microsoft.Extensions.AI` upgraded from `10.0.0` to `10.3.0`
  - `Microsoft.Extensions.AI.OpenAI` upgraded from `10.0.0-preview.1.25559.3` to `10.3.0` (preview → stable)
  - `Microsoft.Extensions.AI.Evaluation.Quality` upgraded from `9.5.0` to `10.3.0`
  - `System.Numerics.Tensors` bumped from `10.0.0` to `10.0.3` (transitive compatibility)
  - Event hierarchy fix: `AgentResponseUpdateEvent` now inherits `WorkflowOutputEvent` (critical switch restructuring in `MAFWorkflowEventBridge`)
  - Type renames: `AgentThread` → `AgentSession`, `GetNewThread()` → `CreateSessionAsync()` (sync → async)
  - Method renames: `StreamAsync` → `RunStreamingAsync`, `AddFanInEdge` → `AddFanInBarrierEdge`
  - Naming conflict resolved: `using AgentResponse = AgentEval.Core.AgentResponse;` alias in adapter files
  - `ChatClientAgentOptions.Instructions` → `ChatOptions.Instructions` across all samples (26 occurrences in 14 files)
  - **Breaking change (MAF adapters only):** Helper methods on `MAFAgentAdapter` and `MAFIdentifiableAgentAdapter` were renamed and made async: `ResetThread()` → `ResetSessionAsync()`, `GetNewThread()` → `CreateSessionAsync()`, and constructor parameter type `AgentThread?` → `AgentSession?`. Core evaluation interfaces (`IEvaluableAgent`, `IStreamableAgent`) are unchanged; only code that calls these helper methods directly must be updated.

### Added
- **Red Team Security Testing Module** - Comprehensive AI agent security evaluation
  - **9 attack types**: PromptInjection, Jailbreak, PIILeakage (LLM02), SystemPromptExtraction (LLM07), IndirectInjection, ExcessiveAgency (LLM06), InsecureOutput (LLM05), InferenceAPIAbuse (LLM10), EncodingEvasion
  - **192 total probes** across all attack categories (expanded InsecureOutput from 18→33)
  - **60% OWASP LLM Top 10 2025 coverage** (6/10): LLM01, LLM02, LLM05, LLM06, LLM07, LLM10
  - **6 MITRE ATLAS techniques**: AML.T0024, AML.T0037, AML.T0043, AML.T0045, AML.T0051, AML.T0054
  - **6 export formats**: JSON, JUnit XML, SARIF (GitHub Security), Markdown, PDF, CSV
  - **4 compliance reports**: OWASP, MITRE, SOC2, ISO27001
  - Fluent assertions: `result.Should().HaveOverallScoreAbove(85)`
  - Attack pipeline API: `AttackPipeline.Create().WithAllAttacks().ScanAsync(agent)`
  - Baseline comparison for CI/CD regression tracking
  - Real-time progress reporting with `ScanProgress` callback
  - Rich console output with emoji, colors, and detailed breakdowns
- **Responsible AI Metrics** (`AgentEval.Metrics.ResponsibleAI` namespace)
  - `ToxicityMetric` - Pattern + LLM hybrid toxicity detection
  - `BiasMetric` - LLM-based bias detection with counterfactual testing
  - `MisinformationMetric` - Claim verification and calibration assessment
- **Calibrated Evaluator** - Multi-model criteria-based evaluation with `CalibratedEvaluator` for consensus-driven scoring
- **CSV Export Format** - New `CsvExporter` for Excel and business intelligence tools
- **Sample 23: Responsible AI** - Toxicity, bias, misinformation metrics with counterfactual testing
- **Sample 24: Benchmark System** - Performance, agentic, standard, and cost benchmarks with comparative analysis
- **SPDX License Identifiers** - Added to all source and test files for compliance

### Changed
- **Trace Record & Replay Improvements** (9 improvements from comprehensive audit)
  - Added `IsComplete` property to `TraceReplayingAgent` for cleaner replay loops
  - Implemented `RecordStreamingChunks` conditional check — streaming chunks now only recorded when option is enabled
  - Wired up `SanitizeToolResult` in streaming recording — tool results are sanitized consistently
  - Implemented `MaxTurns` enforcement in `ChatTraceRecorder` — throws `InvalidOperationException` when limit reached
  - Fixed documentation API names across `docs/tracing.md`, `docs/conversations.md`, `docs/workflows.md`, and `docs/adr/004-trace-recording-replay.md`
  - Added cross-reference sections in `docs/conversations.md` and `docs/workflows.md` linking to tracing guide
  - Updated ADR-004 phase status to reflect current implementation state
  - Sample 13 Demos 3 & 4 rewritten from mocked to fully operational real AI workflows
  - Added 12 new tracing tests (Contains matching, Warn/Ignore mismatch, sanitization, MaxTurns)
- **Sample 13 Audit Fixes** — fixed prompt display mismatch, added `DelayMultiplier = 0.1` for fast workflow replay, removed unused `System.Text.Json` import, corrected Key Takeaways API names
- **docs/tracing.md** Performance Baseline example fixed: `Entries[0].Duration` → `Entries.First(e => e.Type == TraceEntryType.Response).DurationMs`
- Added `ConfigureAwait(false)` to MAF adapter async calls for reliability
- Replaced `Assert.True` with `Assert.Contains` for improved test readability
- Removed hardcoded version strings from documentation

---

## [0.3.0-beta] - 2026-01-25

**Brand Alignment: Evaluation-First Naming** 🎯

This release implements comprehensive renamed APIs to better reflect AgentEval's primary identity as an **AI Agent Evaluation Toolkit**. All "Test" terminology in public APIs has been renamed to "Evaluation" to align with the framework's positioning.

### ⚠️ BREAKING CHANGES

#### Interface Renames
| Old Name | New Name |
|----------|----------|
| `ITestHarness` | `IEvaluationHarness` |
| `IStreamingTestHarness` | `IStreamingEvaluationHarness` |
| `ITestableAgent` | `IEvaluableAgent` |
| `IWorkflowTestableAgent` | `IWorkflowEvaluableAgent` |

#### Class Renames
| Old Name | New Name |
|----------|----------|
| `MAFTestHarness` | `MAFEvaluationHarness` |
| `WorkflowTestHarness` | `WorkflowEvaluationHarness` |
| `TestOptions` | `EvaluationOptions` |
| `TestOutputWriter` | `EvaluationOutputWriter` |
| `TestMetadata` | `EvaluationMetadata` |

#### Method Renames
| Old Name | New Name |
|----------|----------|
| `RunTestAsync()` | `RunEvaluationAsync()` |
| `RunTestStreamingAsync()` | `RunEvaluationStreamingAsync()` |
| `RunTestSuiteAsync()` | `RunEvaluationSuiteAsync()` |
| `TestHarnessFactory` property | `EvaluationHarnessFactory` property |

#### File Renames
| Old Name | New Name |
|----------|----------|
| `ITestHarness.cs` | `IEvaluationHarness.cs` |
| `ITestableAgent.cs` | `IEvaluableAgent.cs` |
| `MAFTestHarness.cs` | `MAFEvaluationHarness.cs` |
| `WorkflowTestHarness.cs` | `WorkflowEvaluationHarness.cs` |
| `TestModels.cs` | `EvaluationModels.cs` |
| `TestOutputWriter.cs` | `EvaluationOutputWriter.cs` |
| `stochastic-testing.md` | `stochastic-evaluation.md` |
| `Sample14_StochasticTesting.cs` | `Sample14_StochasticEvaluation.cs` |

### Unchanged (Universal Terminology)
The following names are **intentionally kept** as they represent universal industry terminology:
- `TestCase` - Standard testing terminology used across all frameworks
- `TestResult` - Conflict resolution with existing `Core.EvaluationResult` type
- `TestSummary` - Consistent with TestResult
- `AgentEvalTestBase` - xUnit integration base class
- `StochasticRunner` - Neutral name, not test-specific
- `*Tests.cs` files - xUnit naming convention

### Changed
- **Terminology:** "stochastic testing" → "stochastic evaluation" throughout codebase and documentation
- **Terminology:** "test harness" → "evaluation harness" throughout codebase and documentation
- **XML Documentation:** Updated all public API comments with evaluation-first language
- **C# Naming Conventions:** Fixed parameter names to use camelCase (`evaluationOptions` instead of `EvaluationOptions`)
- **Documentation:** Title case capitalization fixes in markdown headers
- **Documentation:** Fixed all broken links to `stochastic-testing.md` (now `stochastic-evaluation.md`)
- **TOC:** API Reference section now renders consistently with other menu items

### Migration Guide

Update your code to use the new names:

```csharp
// Before (0.2.x)
var harness = new MAFTestHarness(evaluatorClient);
var result = await harness.RunTestAsync(agent, testCase, options);

// After (0.3.0)
var harness = new MAFEvaluationHarness(evaluatorClient);
var result = await harness.RunEvaluationAsync(agent, testCase, options);
```

```csharp
// Before (0.2.x)
public class MyAgent : ITestableAgent { }

// After (0.3.0)
public class MyAgent : IEvaluableAgent { }
```

### Documentation
- Brand Positioning Guidelines created at `strategy/plans/Implementation-Plan-Brand-Positioning-Guidelines.md`
- All documentation files updated with evaluation-first messaging
- Code examples in documentation updated to use new API names

---

## [0.2.1-beta] - 2026-01-24

**Features + Documentation & Messaging Refresh** 🚀📝

This release adds new features (enhanced token tracking, Sample 19) and updates AgentEval's positioning to better reflect its core value as an **evaluation toolkit** for AI agents.

### Added (Features)
- **Enhanced Token Usage Tracking** - Improved token usage extraction and cost estimation in `MAFTestHarness` and `PerformanceMetrics`
  - More accurate cost calculation across streaming and async scenarios
  - Better handling of model pricing for cost estimation
- **Sample 19: Streaming vs Async Performance Comparison** - New sample demonstrating:
  - Side-by-side streaming vs async performance measurement
  - Time-to-first-token (TTFT) tracking for streaming scenarios
  - Token usage comparison between execution modes
- **Interactive Demo Menu** - Enhanced samples with interactive selection and demo inputs
- **NuGetConsumer Sample Project Enhancements** - Additional demos and offline testing patterns

### Added (Documentation)
- **"Who Is AgentEval For?"** section to README.md and docs/index.md
  - .NET Teams Building AI Agents
  - Microsoft Agent Framework (MAF) Developers
  - ML Engineers Evaluating LLM Quality
- **".NET Advantage"** comparison table to README.md showing AgentEval vs Python alternatives
- **CLI Tool & Samples** section to docs/index.md
- License badge to docs/index.md

### Changed
- **New Positioning:** "The .NET Evaluation Toolkit for AI Agents" (previously "testing framework")
  - Evaluation leads (50% of codebase), followed by testing (25%) and benchmarking (25%)
  - Clearer differentiation vs Python alternatives (RAGAS, DeepEval)
- Updated test count badge to **3000+** across 3 TFMs
- Fixed version references from 1.0.0-alpha to 0.2.0-beta in all documentation
- Updated NuGet tags: added `rag` and `agentic` keywords
- Simplified `docs/roadmap.md` - removed internal planning details, shows only shipped features and general direction

### Removed
- `src/AgentEval/AgentEval-Design.md` - Internal design document with outdated information
- `docs/why-agenteval.md` - Content merged into docs/index.md for unified landing page

### Fixed
- Removed inaccurate "Native xUnit/NUnit/MSTest support" claim (AgentEval works WITH test frameworks, doesn't provide native integration)
- Removed fabricated testimonials from documentation
- Fixed trace replay description accuracy
- Documentation site toc.yml updated for removed files

### Documentation
- All 18+ documentation files updated with consistent messaging
- NuGet README now shows correct positioning tagline
- Strategy documents aligned with new positioning

---

## [0.2.0-beta] - 2026-01-24

**AgentEval Public Beta Release** 🎉

This release marks the transition from alpha to beta. The framework is now feature-complete for core scenarios and ready for community feedback.

### Added
- **Codecov Badge** - Coverage visibility in README.md
- **NuGet Consumer Sample** (`samples/AgentEval.NuGetConsumer/`) - Standalone project showcasing all major features
  - Tool chain assertions (HaveCalledTool, WithArgument, BeforeTool, AfterTool)
  - Performance assertions (Duration, TTFT, Cost, Token limits)
  - Behavioral policies (NeverCallTool, MustConfirmBefore, NeverPassArgumentMatching)
  - Response assertions (Contain, NotContain, length validation)
  - Mock testing with FakeChatClient
  - Stochastic testing examples
  - Model comparison patterns
  - Agentic metrics overview
  - Works offline with mock data - no Azure OpenAI required
- **Custom Domain** - AgentEval.dev documentation site with GitHub Pages
- **Comprehensive Documentation** - 25+ documentation pages with zero DocFX warnings
- **Security Scanning** - Enhanced pipeline with secret detection and dependency scanning

### Changed
- Updated README test count badge to 3000+ (reflecting 1000+ tests × 3 TFMs)
- Documentation navigation reorganized with improved feature grouping
- Security scanning patterns refined to reduce false positives
- Version bumped from 0.1.3-alpha to 0.2.0-beta signaling production readiness

### Documentation
- Getting Started, Assertions, Metrics Reference, Model Comparison guides
- Trace Record & Replay, Stochastic Testing, Benchmarks documentation
- CI/CD Integration guide with GitHub Actions examples
- Migration guide for Python/Node.js developers

---

## [0.1.3-alpha] - 2026-01-18

### Added
- **Security Scanning Pipeline** - Comprehensive automated security analysis
  - DevSkim static analysis integrated into CI/CD
  - NuGet dependency vulnerability scanning
  - Secret detection to prevent credential leaks
  - SARIF output to GitHub Security tab
  - Weekly scheduled scans plus on push/PR triggers
- **CLI Baseline Comparison** - Compare against golden files
  - `--baseline` option for snapshot testing workflow
  - Human-readable diff output with color coding
  - Exit code 2 for baseline mismatches (distinct from test failures)
- **Security Documentation** - Comprehensive security guidance
  - [SECURITY.md](SECURITY.md) - Vulnerability reporting process
  - [docs/security-scanning.md](docs/security-scanning.md) - Tech stack and architecture
  - [strategy/Implementation-Plan-Security-Hardening.md](strategy/Implementation-Plan-Security-Hardening.md) - Security roadmap
- **Input Validation Hardening** - Defense against path traversal attacks
  - CLI file path validation with directory allowlist
  - Path normalization and canonicalization
  - Extension validation for dataset files
- **Security Workflow** (`.github/workflows/security.yml`)
  - Runs on all pushes to main/develop branches
  - Runs on all pull requests
  - Scheduled weekly Monday scans for dependency updates

### Changed
- Project version bumped to 0.1.3-alpha across all packages
- Enhanced CI/CD with security gate requirements

### Security
- Implemented OWASP Top 10 mitigations for web-adjacent attack vectors
- Added anti-glassworm protections in development workflow
- PII detection in `NeverPassArgumentMatching` uses redaction by default

---

## [0.1.2-alpha] - 2026-01-04

### Added
- **Behavioral Policy Assertions** - Safety-critical assertions for enterprise compliance
  - `NeverCallTool(toolName, because)` - Assert forbidden tools were never called
  - `NeverPassArgumentMatching(pattern, because, options)` - Detect PII/secrets via regex with automatic redaction
  - `MustConfirmBefore(toolName, because, confirmationToolName)` - Require confirmation before risky actions
  - `BehavioralPolicyViolationException` with structured properties (PolicyName, ViolationType, ViolatingAction, RedactedValue)
  - 16 unit tests for behavioral policy assertions
  - Updated Sample12 with new behavioral policy examples
  - See [ADR-008](docs/adr/008-calibrated-judge-multi-model.md) for design decisions
- **Judge Calibration** - Multi-model consensus for reliable LLM-as-judge evaluations
  - `CalibratedJudge` - Wrapper for running evaluations with multiple LLM judges
  - `VotingStrategy` enum: Median, Mean, Unanimous, Weighted
  - `CalibratedResult` with Agreement %, Confidence Intervals, per-judge scores
  - `ICalibratedJudge` interface for testability
  - `CalibratedJudgeOptions` with configurable timeouts, parallelism, consensus tolerance
  - Factory pattern: `metricFactory(judgeName)` for per-judge metric instantiation
  - Parallel judge execution with graceful degradation
  - 17 unit tests for calibrated judge
  - Sample18_JudgeCalibration demonstration
  - See [ADR-008](docs/adr/008-calibrated-judge-multi-model.md) for design decisions
- **Model Comparison Markdown Export** - Shareable comparison reports
  - `ToMarkdown()` extension for `ModelComparisonResult` - Full report with all sections
  - `ToRankingsTable()` - Compact table with medal emojis (🥇🥈🥉)
  - `ToDetailedMetricsTable()` - Pass rate, latency, cost metrics
  - `ToStatisticsTable()` - Mean, median, percentiles, confidence intervals
  - `ToGitHubComment()` - Collapsible PR comment format
  - `SaveToMarkdownAsync()` - File export
  - `MarkdownExportOptions` with Default and Minimal presets
  - Batch comparison support for multiple test cases
  - 20 unit tests for markdown export
  - Updated Sample15 with markdown export demonstration
- **Trace Record & Replay (Phase 8)** - Deterministic testing and time-travel debugging
  - `TraceRecordingAgent` - Wraps any agent to capture all executions with full fidelity
  - `TraceReplayingAgent` - Replays recorded traces deterministically without LLM calls
  - `ChatTraceRecorder` - Records multi-turn conversations with turn tracking
  - `ChatExecutionResult` - Complete conversation result with aggregate performance
  - `WorkflowTraceRecorder` - Records multi-agent workflow orchestrations
  - `WorkflowTraceReplayingAgent` - Replays workflow traces step-by-step
  - `TraceSerializer` / `WorkflowTraceSerializer` - JSON serialization for traces
  - `AgentTrace`, `WorkflowTrace` - Rich trace models with metadata and performance
  - `TraceEntry`, `WorkflowTraceStep` - Detailed per-invocation/step records
  - `TraceTokenUsage`, `TraceToolCall`, `TraceError` - Supporting models
  - Streaming support for recording/replaying chunked responses
  - 168 new tests covering all tracing functionality
  - Comprehensive [tracing documentation](docs/tracing.md)
  - Sample 13: Trace Record & Replay demonstration
- **Enhanced Fluent Assertions** - Improved xUnit assertion failure experience inspired by FluentAssertions/Shouldly
  - **`because` parameter** on all assertions for documenting test intent (e.g., `HaveCalledTool("SearchTool", because: "user query requires search")`)
  - **`AgentEvalScope`** for collecting multiple assertion failures into a single exception with all failures listed
  - **Rich structured error messages** with Expected/Actual values, context, tool timeline, and actionable suggestions
  - **`[StackTraceHidden]`** attribute on assertion methods for cleaner failure stack traces
  - **`CallerArgumentExpression`** for automatic subject name capture in ResponseAssertions
  - New `AgentEvalScopeException` for batch failure reporting
  - Comprehensive [assertions documentation](docs/assertions.md) with examples
- **CLI eval command** with real dataset validation
  - Loads datasets from YAML, JSON, JSONL, and CSV files
  - Validates test case completeness, ground truth, expected tools, and context
  - Outputs results in JSON, JUnit XML, Markdown, or TRX formats
  - Cross-platform color support with NO_COLOR environment variable respect
- **Sample datasets** for quick start
  - `samples/datasets/travel-agent.yaml` - agentic evaluation with tool usage
  - `samples/datasets/rag-qa.yaml` - RAG evaluation with context documents
  - `samples/datasets/README.md` - comprehensive dataset format documentation
- **YAML dataset loader** with flexible field aliasing
  - Supports both `expected_output` and `expectedOutput` naming conventions
  - Supports `ground_truth`, `expected_tools`, and `context` fields
  - Full YAML 1.2 compliance via YamlDotNet
- **Workflow Testing Support (Phase 6B)** - Per-executor visibility for multi-agent workflows
  - `WorkflowExecutionResult` - Captures per-executor output, timing, and tool calls
  - `ExecutorStep` and `WorkflowError` models for detailed workflow analysis
  - `IWorkflowEvaluableAgent` - Extended interface for workflow-aware agents
  - `MAFWorkflowAdapter` - Adapter for MAF Workflows with streaming event capture
  - `WorkflowEvaluationHarness` - evaluation harness for workflow testing with assertions
  - `WorkflowAssertions` - Fluent assertion API for workflow execution results
  - Supports executor order validation, step timing, tool call tracking
  - 71 new tests for workflow components
- **Workflow Edge/Graph Support (Phase 6B+)** - Full DAG structure for complex workflows
  - `EdgeType` enum - Sequential, Conditional, Switch, ParallelFanOut, ParallelFanIn, Loop, Error, Terminal
  - `WorkflowEdge` - Static edge definitions with conditions and switch labels
  - `EdgeExecution` - Runtime edge traversal with routing decisions and data transfer
  - `ParallelBranch` - Tracks parallel execution branches
  - `WorkflowNode` - Node definitions with entry/exit point markers
  - `WorkflowGraphSnapshot` - Complete DAG topology with nodes, edges, and execution path
  - `RoutingDecision` - Captures conditional/switch routing decisions
  - New workflow events: `EdgeTraversedEvent`, `RoutingDecisionEvent`, `ParallelBranchStartEvent`, `ParallelBranchEndEvent`
  - Edge assertions: `HaveTraversedEdge()`, `HaveConditionalRouting()`, `HaveParallelExecution()`, `ForEdge().BeOfType()`
  - Step edge assertions: `HaveIncomingEdge()`, `HaveBeenConditionallyRouted()`, `BeInParallelBranch()`
  - `MAFWorkflowAdapter.WithGraph()` and `FromConditionalSteps()` factory methods
  - 66 new tests for edge models and assertions

### Changed
- **Test project reorganization** into logical folder structure:
  - `Core/` - AgentEvalBuilder, Logger, MetricRegistry, Retry, Normalizer, Concurrency tests
  - `Metrics/RAG/` - Faithfulness, Relevance, Context Precision/Recall, Answer Correctness
  - `Metrics/Agentic/` - Tool Selection, Arguments, Success, Efficiency, Task Completion
  - `DataLoaders/` - Dataset loader and serialization tests
  - `Exporters/` - Result exporter tests
  - `Testing/` - FakeChatClient, ConversationRunner, ConversationalTestCase tests
  - `Assertions/` - Tool usage and response assertion tests
  - `Models/` - Domain model tests
  - `Benchmarks/` - Performance and agentic benchmark tests
  - `MAF/` - Microsoft Agent Framework integration tests
- **CLI ConsoleHelper** for improved cross-platform terminal support
  - Detects NO_COLOR environment variable
  - Detects TERM=dumb terminals
  - Gracefully handles output redirection (piping to files)

### Fixed
- YAML loader tests now use correct 4-space indentation matching YAML standards
- Removed invalid `include-prerelease` input from CI workflow (actions/setup-dotnet@v4 compatibility)

---

## [0.1.2-alpha] - 2026-01-04

### Added
- Additional test coverage for core components
- XML documentation generation enabled in project configuration
- DocFX build scripts (PowerShell and Batch) for automated API documentation generation
- Comprehensive documentation guides (GENERATE-DOCS.md, DOCUMENTATION-SUMMARY.md)

### Changed
- Project now generates XML documentation files for all target frameworks (net8.0, net9.0, net10.0)
- Suppressed CS1591 warnings for undocumented members

---

## [0.1.1-alpha] - 2026-01-03

### Added
- SourceLink support for debugging into source code
- Symbol packages (.snupkg) published to NuGet.org
- NuGet package icon (AgentEvalNugetLogoAE.png)
- Azure OpenAI environment variables in CI/CD workflows

### Changed
- Repository restructured to standard .NET layout (src/, samples/, tests/, docs/)
- Central package management with `Directory.Packages.props`
- Shared build configuration with `Directory.Build.props`
- GitHub Actions CI now tests on .NET 8, 9, and 10 across Ubuntu and Windows
- CI workflow optimized with NuGet caching and fail-fast disabled

### Infrastructure
- GitHub Actions CI workflow for automated build and test
- GitHub Actions release workflow for NuGet publishing
- DocFX documentation scaffolding
- EditorConfig for consistent code style

---

## [0.1.0-alpha] - 2026-01-02

### Added

#### Core Framework
- First .NET-native AI agent testing, evaluation, and benchmarking framework
- Full Microsoft Agent Framework (MAF) integration via `MAFAgentAdapter` and `MAFTestHarness`
- Extensible adapter pattern supporting `IChatClient` and other frameworks
- Plugin system with `IAgentEvalPlugin` interface

#### Tool Usage Tracking & Assertions
- `ToolCallRecord` for capturing tool invocations with timing, arguments, results, and errors
- `ToolCallTimeline` for visualizing parallel tool execution
- Fluent assertions: `HaveCalledTool()`, `BeforeTool()`, `WithArgument()`, `HaveNoErrors()`
- Tool usage reports with success/failure metrics

#### Performance Metrics
- Real-time performance tracking with TTFT (Time To First Token)
- Per-tool timing and execution waterfall data
- Token counting (prompt/completion/total)
- Cost estimation for 8+ models (GPT-4o, GPT-4o-mini, Claude 3.5, Claude 3 Opus, GPT-4 Turbo, GPT-3.5 Turbo, o1-preview, o1-mini)
- Performance assertions: `HaveTotalDurationUnder()`, `HaveTimeToFirstTokenUnder()`, `HaveEstimatedCostUnder()`

#### RAG Metrics
- Faithfulness metric (grounded in context)
- Relevance metric (response addresses query)
- Context Precision metric
- Context Recall metric
- Answer Correctness metric

#### Agentic Metrics
- Tool Selection metric (chose appropriate tools)
- Tool Arguments metric (correct arguments passed)
- Tool Success metric (tools executed successfully)
- Task Completion metric (agent completed the task)
- Efficiency metric (minimal steps, tokens, time)

#### Benchmarks
- `PerformanceBenchmark` for latency/throughput/cost analysis
- `AgenticBenchmark` for multi-step agentic task evaluation
- Percentile statistics (p50, p90, p95, p99)
- Summary statistics (mean, min, max, standard deviation)

#### Testing Infrastructure
- `FakeChatClient` for zero-dependency unit testing
- `TestCase` model with inputs, expected outputs, evaluation criteria
- `TestResult` with comprehensive run data
- Trace-first failure reporting with structured diagnostics

#### Observability
- `IAgentEvalLogger` abstraction with console and Microsoft.Extensions.Logging adapters
- Run artifacts for debugging and "time travel" inspection
- Designed for OpenTelemetry (OTel) integration

### Technical Details
- 210+ unit tests with comprehensive coverage
- Multi-target framework support: .NET 8.0, 9.0, 10.0
- Zero-dependency core (optional integrations for MAF, Azure OpenAI)

---

## Future Releases

### Planned Packages
- `AgentEval` (core) ✅ This release
- `AgentEval.Maf` (MAF integration) - planned
- `AgentEval.TestKit` (fixtures/builders/helpers) - planned
- `AgentEval.Tracing` (OTel + run artifacts) - planned
- `AgentEval.Studio` (workflow visualizer / time-travel UI) - future

[Unreleased]: https://github.com/joslat/AgentEval/compare/v0.5.2-beta...HEAD
[0.5.2-beta]: https://github.com/joslat/AgentEval/compare/v0.5.1-beta...v0.5.2-beta
[0.5.1-beta]: https://github.com/joslat/AgentEval/compare/v0.4.0-beta...v0.5.1-beta
[0.4.0-beta]: https://github.com/joslat/AgentEval/compare/v0.3.0-beta...v0.4.0-beta
[0.3.0-beta]: https://github.com/joslat/AgentEval/compare/v0.2.1-beta...v0.3.0-beta
[0.2.1-beta]: https://github.com/joslat/AgentEval/compare/v0.2.0-beta...v0.2.1-beta
[0.2.0-beta]: https://github.com/joslat/AgentEval/compare/v0.1.3-alpha...v0.2.0-beta
[0.1.3-alpha]: https://github.com/joslat/AgentEval/compare/v0.1.2-alpha...v0.1.3-alpha
[0.1.2-alpha]: https://github.com/joslat/AgentEval/compare/v0.1.1-alpha...v0.1.2-alpha
[0.1.1-alpha]: https://github.com/joslat/AgentEval/compare/v0.1.0-alpha...v0.1.1-alpha
[0.1.0-alpha]: https://github.com/joslat/AgentEval/releases/tag/v0.1.0-alpha
