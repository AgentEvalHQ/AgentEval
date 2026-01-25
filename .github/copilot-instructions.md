# AgentEval - AI Coding Agent Instructions

AgentEval is **the comprehensive .NET evaluation toolkit for AI agents**, built primarily for **Microsoft Agent Framework (MAF)**. What RAGAS and DeepEval do for Python, AgentEval does for .NET—plus tool chain evaluation, behavioral policies, and calibrated multi-judge scoring.

## Architecture Overview

```
AgentEval/
├── src/AgentEval/          # Main library (multi-target: net8.0, net9.0, net10.0)
│   ├── Core/               # Interfaces: IMetric, IEvaluableAgent, IEvaluationHarness
│   ├── Assertions/         # Fluent assertions: ToolUsageAssertions, PerformanceAssertions
│   ├── Comparison/         # StochasticRunner, IAgentFactory, ModelComparer
│   ├── MAF/                # Microsoft Agent Framework: MAFEvaluationHarness, MAFAgentAdapter
│   ├── Metrics/            # RAG/ (Faithfulness, Relevance) + Agentic/ (ToolSelection, etc.)
│   ├── Models/             # TestCase, TestResult, ToolCallRecord, PerformanceMetrics
│   ├── Tracing/            # TraceRecordingAgent, ChatTraceRecorder, WorkflowTraceRecorder
│   └── Testing/            # FakeChatClient for mocking IChatClient
├── tests/AgentEval.Tests/  # xUnit tests, mirrors src/ structure
└── samples/AgentEval.Samples/  # 18 runnable samples (Sample01_HelloWorld, etc.)
```

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
dotnet test                  # Run all 1,000+ tests (×3 TFMs = 3,000+ total)
dotnet run --project samples/AgentEval.Samples  # Run samples
dotnet pack src/AgentEval   # Create NuGet package
```

## CLI Tool Commands

```bash
agenteval eval --dataset tests.yaml --format junit --output results.xml
agenteval eval --dataset data.jsonl --pass-threshold 80
agenteval init --format yaml --output agenteval.yaml
agenteval list metrics      # List available metrics
agenteval list assertions   # List assertion types
agenteval list formats      # List output formats
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
var recorder = new TraceRecordingAgent(realAgent);
var response = await recorder.ExecuteAsync("query");
var trace = recorder.GetTrace();
TraceSerializer.Save(trace, "trace.json");

// REPLAY: Deterministic playback
var replayer = new TraceReplayingAgent(trace);
var replayed = await replayer.ReplayNextAsync(); // Identical response
```

Multi-turn conversations:
```csharp
var chatRecorder = new ChatTraceRecorder(chatAgent);
await chatRecorder.AddUserTurnAsync("Hello");
await chatRecorder.AddUserTurnAsync("Book a flight");
var trace = chatRecorder.ToAgentTrace();
```

Workflows:
```csharp
var workflowRecorder = new WorkflowTraceRecorder("workflow-name");
workflowRecorder.RecordStep(new WorkflowTraceStep { ... });
WorkflowTraceSerializer.Save(workflowRecorder.GetTrace(), "workflow.json");
```

## stochastic evaluation & Model Comparison

Handle LLM non-determinism by running tests multiple times:
```csharp
var stochasticRunner = new StochasticRunner(harness, EvaluationOptions);
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
- Token usage extracted from `AgentRunResponse.Usage` property

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
1. Create in `src/AgentEval/Metrics/RAG/` or `Metrics/Agentic/`
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
// In Program.cs or Startup.cs
services.AddAgentEval(options =>
{
    options.DefaultModelId = "gpt-4o";
});

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
