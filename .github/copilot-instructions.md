# AgentEval - AI Coding Agent Instructions

AgentEval is **the comprehensive .NET evaluation toolkit for AI agents**, built primarily for **Microsoft Agent Framework (MAF)** with **Microsoft.Extensions.AI**. What RAGAS and DeepEval do for Python, AgentEval does for .NET—plus tool chain evaluation, behavioral policies, and calibrated multi-judge scoring.

## Architecture Overview

```
AgentEval/
├── src/
│   ├── AgentEval.Abstractions/  # Public contracts: IMetric, IEvaluableAgent, models
│   ├── AgentEval.Core/          # Implementations: metrics, assertions, comparison, tracing
│   ├── AgentEval.DataLoaders/   # Data loaders, exporters, output formatting
│   ├── AgentEval.MAF/           # Microsoft Agent Framework integration
│   ├── AgentEval.RedTeam/       # Security scanning, attack types, compliance
│   └── AgentEval/               # Umbrella packaging project (NuGet: AgentEval)
├── tests/AgentEval.Tests/       # xUnit tests, mirrors src/ structure
└── samples/AgentEval.Samples/   # runnable samples (organised in groups A–J)
```

All library sub-projects use `RootNamespace=AgentEval` to preserve original namespaces. Only the umbrella is `IsPackable=true`; the single NuGet package embeds every sub-project DLL per TFM. The CLI lives in **this** repository at `src/AgentEval.Cli/` and is the canonical source for the `AgentEval.Cli` NuGet package (packed as the `agenteval` dotnet tool); the former external `AgentEvalHQ/AgentEval.Cli` repository is being retired.

## Environment Setup

Required environment variables for samples and integration tests:
```powershell
$env:AZURE_OPENAI_ENDPOINT = "https://your-resource.openai.azure.com/"
$env:AZURE_OPENAI_API_KEY = "your-api-key"
# Optional: Secondary models for comparison
$env:AZURE_OPENAI_DEPLOYMENT = "gpt-4o"           # Primary model
$env:AZURE_OPENAI_DEPLOYMENT_2 = "gpt-4o-mini"    # Secondary model
```

## Build & Test Commands

```powershell
dotnet build                 # Build all projects
dotnet test                  # Run all tests (×3 TFMs)
dotnet run --project samples/AgentEval.Samples  # Run samples
dotnet pack src/AgentEval   # Create NuGet package
```

## Key Patterns & Conventions

### Metric Interface Hierarchy
```csharp
IMetric                    // Base: EvaluateAsync(EvaluationContext) → MetricResult
├── IRAGMetric            // RequiresContext, RequiresGroundTruth
└── IAgenticMetric        // RequiresToolUsage
```

### Metric Naming Prefixes (see docs/naming-conventions.md)
- `llm_` = LLM-evaluated (API cost) → `llm_faithfulness`, `llm_relevance`
- `code_` = Computed by code (free) → `code_tool_success`, `code_tool_efficiency`  
- `embed_` = Embedding-based ($) → `embed_answer_similarity`

### Fluent Assertion Pattern
```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("FeatureTool", because: "feature planning required")
        .BeforeTool("SecurityTool")
        .WithArgument("type", "OAuth2")
    .And()
    .HaveNoErrors();

result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveEstimatedCostUnder(0.10m);
```

### Test Naming Convention
```csharp
[Fact]
public async Task MethodName_StateUnderTest_ExpectedBehavior()
// Example: HaveCalledTool_WhenToolWasCalled_ShouldPass
```

## Trace Record & Replay Pattern

Capture agent executions for deterministic replay (no LLM calls needed):
```csharp
// RECORD: Capture execution
await using var recorder = new TraceRecordingAgent(realAgent, "weather_test");
var response = await recorder.InvokeAsync("query");
var trace = recorder.Trace;
await TraceSerializer.SaveToFileAsync(trace, "trace.json");

// REPLAY: Deterministic playback
var replayer = new TraceReplayingAgent(trace);
var replayed = await replayer.InvokeAsync("query"); // Identical response
```

Multi-turn conversations:
```csharp
await using var chatRecorder = new ChatTraceRecorder(chatAgent, "support_conv");
await chatRecorder.AddUserTurnAsync("Hello");
await chatRecorder.AddUserTurnAsync("Book a flight");
var trace = chatRecorder.ToAgentTrace();
```

Workflows:
```csharp
await using var workflowRecorder = new WorkflowTraceRecorder(workflowAgent, "workflow-name");
var result = await workflowRecorder.ExecuteWorkflowAsync("Plan trip");
await WorkflowTraceSerializer.SaveToFileAsync(workflowRecorder.Trace, "workflow.json");
```

## stochastic evaluation & Model Comparison

Handle LLM non-determinism by running tests multiple times:
```csharp
var stochasticRunner = new StochasticRunner(harness, statisticsCalculator: null, EvaluationOptions);
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase, 
    new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.8));

result.PrintTable("Metrics"); // Shows min/max/mean statistics
```

Compare models using Agent Factory pattern:
```csharp
public interface IAgentFactory {
    string ModelId { get; }
    string ModelName { get; }
    IEvaluableAgent CreateAgent();
}

// Run same test across multiple models
foreach (var factory in factories) {
    var agent = factory.CreateAgent();
    var result = await stochasticRunner.RunStochasticTestAsync(agent, testCase, options);
    modelResults.Add((factory.ModelName, result));
}
modelResults.PrintComparisonTable();
```

## Critical Implementation Details

### MAF Integration
- `MAFAgentAdapter` wraps MAF's `AIAgent` → implements `IStreamableAgent`
- `MAFEvaluationHarness` orchestrates tests with streaming, tool tracking, performance metrics
- Token usage extracted from `AgentResponse.Usage` property

### FakeChatClient for Testing
Use `AgentEval.Testing.FakeChatClient` to test metrics without external LLM calls:
```csharp
var fakeClient = new FakeChatClient("""{"score": 95, "explanation": "Good"}""");
var metric = new FaithfulnessMetric(fakeClient);
```

### Cost Estimation
`ModelPricing` (in `PerformanceMetrics.cs`) has built-in pricing for 8+ models:
```csharp
ModelPricing.SetPricing("custom-model", inputPer1K: 0.002m, outputPer1K: 0.006m);
```

### Assertion Exceptions
- `ToolAssertionException` → tool-related failures with Expected/Actual/Suggestions
- `PerformanceAssertionException` → performance/cost failures  
- `AgentEvalScope.FailWith()` → collects multiple failures before throwing

## Adding New Features

### New Metric
1. Create in appropriate sub-project:
   - `src/AgentEval.Core/Metrics/RAG/` for RAG metrics
   - `src/AgentEval.Core/Metrics/Agentic/` for agentic metrics
2. Implement `IRAGMetric` or `IAgenticMetric`
3. Use `llm_`/`code_`/`embed_` prefix in `Name` property
4. Add tests in `tests/AgentEval.Tests/Metrics/`

### New Assertion
1. Add method to `ToolUsageAssertions`, `PerformanceAssertions`, or `ResponseAssertions`
2. Use `[StackTraceHidden]` attribute
3. Call `AgentEvalScope.FailWith()` for rich error messages with suggestions

## Code Style

- **C# Preview features enabled** (`LangVersion=preview`)
- **Nullable enabled** - all types must handle nullability
- **File-scoped namespaces** preferred
- **Primary constructors** for simple types
- **`required` properties** over constructor params for models
- **XML docs** on all public APIs (CS1591 suppressed but encouraged)

## Architectural Principles (SOLID, DRY, KISS, CLEAN)

This codebase strictly follows architectural best practices. See `docs/adr/006-service-based-architecture-di.md` and `docs/architecture/service-gap-analysis.md` for verification.

### SOLID Principles
- **Single Responsibility**: Each service has one focused purpose (e.g., `IStatisticsCalculator` only does statistics)
- **Open/Closed**: Extend via interfaces (`IMetric`, `IResultExporter`) without modifying existing code
- **Liskov Substitution**: All interface implementations are interchangeable
- **Interface Segregation**: Separate interfaces for distinct capabilities (`IEvaluableAgent` vs `IStreamableAgent`)
- **Dependency Inversion**: All core services depend on abstractions (interfaces), not concretions

### DRY (Don't Repeat Yourself)
- Use existing utilities: `StatisticsCalculator`, `ToolUsageExtractor`, `DatasetLoaderFactory`
- Inherit from base classes where provided
- Share constants in centralized locations

### KISS (Keep It Simple)
- Favor readable code over clever optimizations
- Use explicit types when it aids comprehension
- Avoid over-engineering (see service-gap-analysis.md for "when NOT to add interfaces")

### CLEAN Architecture
- Core domain logic has zero external dependencies
- Dependencies flow inward (infrastructure → application → domain)
- Interfaces define contracts, implementations are pluggable

## Dependency Injection (DI/IOC)

AgentEval uses Microsoft.Extensions.DependencyInjection for service registration (ADR-006).

### Registration Pattern
```csharp
// Register all services (umbrella convenience):
services.AddAgentEvalAll();

// Or register selectively:
services.AddAgentEval();              // Core services
services.AddAgentEvalDataLoaders();   // DataLoaders + Exporters
services.AddAgentEvalRedTeam();       // Red Team security testing

// Resolved via DI
public class MyService(IStochasticRunner runner, IModelComparer comparer)
{
    // Constructor injection - depend on interfaces, not implementations
}
```

### Interface-First Development
All core services must:
1. **Define an interface** in `Core/` (e.g., `IStochasticRunner`)
2. **Implement the interface** (e.g., `StochasticRunner`)
3. **Register in DI** via `AgentEvalServiceCollectionExtensions`
4. **Inject dependencies** as interfaces, never concrete types

### Service Lifetimes
- `Singleton`: Stateless services (`IStatisticsCalculator`, `IToolUsageExtractor`)
- `Scoped`: Stateful per-operation services (`IStochasticRunner`, `IModelComparer`)
- `Transient`: Light disposable instances (rare in this codebase)

### When NOT to Create Interfaces
Per service-gap-analysis.md, avoid interfaces for:
- **Builders** (fluent API, e.g., `AgentEvalBuilder`)
- **Configuration objects** (POCOs like `StochasticOptions`)
- **Test-time tools** (e.g., `PerformanceBenchmark` - direct instantiation is appropriate)

## Strategy Drivers (Human-Friendly + Machine-Parseable)

All error messages must have: Expected/Actual/Suggestions structure. All assertions accept `because` parameter. Outputs should be CI/CD consumable (JUnit XML, JSON, Markdown).

## Dependencies

Core dependencies (see `Directory.Packages.props` for versions):
- `Microsoft.Agents.AI` - MAF framework
- `Microsoft.Extensions.AI` - Abstractions (IChatClient)
- `Azure.AI.OpenAI` - Azure OpenAI integration
- `YamlDotNet` - Dataset loading

## Documentation

- `docs/architecture.md` - Component diagrams, data flow
- `docs/assertions.md` - Complete assertion API with AgentEvalScope
- `docs/tracing.md` - Trace Record & Replay patterns
- `docs/conversations.md` - Multi-turn conversation testing
- `docs/workflows.md` - Multi-agent workflow testing
- `docs/naming-conventions.md` - Metric naming prefixes
- `docs/adr/` - Architecture Decision Records


## Using maf-autopilot (MAF Doctor) for Microsoft Agent Framework code

This repository uses Microsoft Agent Framework. The maf-autopilot MCP server
(also called "MAF Doctor") is installed and exposes tools for diagnosing,
fixing, and migrating MAF code.

**Before answering MAF questions or proposing changes:**

1. **Always call `MafDoctor` first** on the repo path to get the current
   health grade (A-F) and the top issues. Don't speculate about MAF
   quality without this baseline.

2. **For any `[Obsolete]` warning, `CS0618` / `CS0246` diagnostic, or build
   failure mentioning a MAF type** — call `MafRunCs0618Hunt` (full project
   scan) or `MafApiSafety` (single symbol) BEFORE suggesting a fix. The
   maf-autopilot registry has curated fix recipes that supersede your
   training data — MAF ships breaking changes every minor version, so
   training data is likely outdated.

3. **Before manually editing MAF code to fix an anti-pattern** — call
   `MafAutoFixAll --dry-run` and offer to apply the deterministic
   rewrites first. Manual edits drift; the rewrites are tested.

4. **When designing a new MAF agent or workflow** — call `MafNewAgent` /
   `MafNewExecutor` for scaffolds, or `MafSimulateWorkflow` for topology
   preview. Don't reconstruct patterns from memory.

5. **For deep architectural / security / migration questions** — use the
   `@maf-best-practice-reviewer`, `@maf-auditor`, `@maf-migration`, or
   `@maf-incident-responder` specialist agents.

maf-autopilot tools are MAF-version-aware via `applies_to_codebases` markers
in the registry — they know which fix applies to which MAF version. Defer to
the tools.
