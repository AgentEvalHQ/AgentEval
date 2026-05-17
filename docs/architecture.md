# AgentEval Architecture

> **Understanding the component structure and design patterns of AgentEval**

---

## Overview

AgentEval is designed with a layered architecture that separates concerns and enables extensibility. The framework follows SOLID principles, with interface segregation being particularly important for the metric hierarchy.

---

## Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              AgentEval                                       │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                           Core Layer                                    │ │
│  ├────────────────────────────────────────────────────────────────────────┤ │
│  │                                                                         │ │
│  │  Interfaces:                                                            │ │
│  │  ┌─────────────┐  ┌───────────────┐  ┌──────────────────┐  ┌──────────┐│ │
│  │  │   IMetric   │  │IEvaluableAgent│  │IEvaluationHarness│  │IEvaluator│ │ │
│  │  └─────────────┘  └───────────────┘  └──────────────────┘  └──────────┘│ │
│  │  ┌─────────────────┐                                                   │ │
│  │  │IExporterRegistry│                                                   │ │
│  │  └─────────────────┘                                                   │ │
│  │                                                                         │ │
│  │  Utilities:                                                             │ │
│  │  ┌─────────────┐  ┌──────────────┐  ┌─────────────┐  ┌──────────────┐  │ │
│  │  │MetricRegistry│ │ScoreNormalizer│ │LlmJsonParser│  │ RetryPolicy  │  │ │
│  │  └─────────────┘  └──────────────┘  └─────────────┘  └──────────────┘  │ │
│  │                                                                         │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                          Metrics Layer                                  │ │
│  ├────────────────────────────────────────────────────────────────────────┤ │
│  │                                                                         │ │
│  │  RAG Metrics:              Agentic Metrics:         Embedding Metrics:  │ │
│  │  ┌─────────────────┐       ┌─────────────────┐      ┌────────────────┐  │ │
│  │  │  Faithfulness   │       │  ToolSelection  │      │AnswerSimilarity│  │ │
│  │  │  Relevance      │       │  ToolArguments  │      │ContextSimilarity│ │ │
│  │  │  ContextPrecision│      │  ToolSuccess    │      │ QuerySimilarity│  │ │
│  │  │  ContextRecall  │       │  TaskCompletion │      └────────────────┘  │ │
│  │  │  AnswerCorrectness│     │  ToolEfficiency │                          │ │
│  │  └─────────────────┘       └─────────────────┘                          │ │
│  │                                                                         │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                        Assertions Layer                                 │ │
│  ├────────────────────────────────────────────────────────────────────────┤ │
│  │                                                                         │ │
│  │  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐  │ │
│  │  │ToolUsageAssertions  │  │PerformanceAssertions│  │ResponseAssertions│ │ │
│  │  │  .HaveCalledTool()  │  │  .HaveDurationUnder()│ │  .Contain()     │  │ │
│  │  │  .BeforeTool()      │  │  .HaveTTFTUnder()   │  │  .MatchPattern()│  │ │
│  │  │  .WithArguments()   │  │  .HaveCostUnder()   │  │  .HaveLength()  │  │ │
│  │  └─────────────────────┘  └─────────────────────┘  └─────────────────┘  │ │
│  │                                                                         │ │  │  ┌─────────────────────────────────────────────────────────────────────┐  │ │
  │  │                  WorkflowAssertions                                  │ │ │
  │  │  .HaveStepCount()      .ForExecutor()        .HaveGraphStructure()  │ │ │
  │  │  .HaveExecutedInOrder() .HaveCompletedWithin() .HaveTraversedEdge() │ │ │
  │  │  .HaveNoErrors()       .HaveNonEmptyOutput() .HaveExecutionPath()   │ │ │
  │  └─────────────────────────────────────────────────────────────────────┘  │ │
  │                                                                         │ │
  └────────────────────────────────────────────────────────────────────────┘ │
                                                                              │
  ┌────────────────────────────────────────────────────────────────────────┐ │
  │                     Workflow Evaluation Layer                          │ │
  ├────────────────────────────────────────────────────────────────────────┤ │
  │                                                                         │ │
  │  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐  │ │
  │  │ WorkflowEvaluationHarness │ │  MAFWorkflowAdapter │ │ MAFWorkflowEventBridge │ │ │
  │  │  .RunWorkflowTestAsync() │ │  .FromMAFWorkflow()  │ │ .ProcessEventsAsync() │ │ │
  │  │  .WithTimeout()        │ │  .ExtractGraph()     │ │ .HandleTimeout()    │ │ │
  │  │  .WithAssertions()     │ │  .TrackPerformance() │ │ .StreamEvents()     │ │ │
  │  └─────────────────────┘  └─────────────────────┘  └─────────────────┘  │ │
  │                                                                         │ │
  │  ┌─────────────────────┐  ┌─────────────────────┐  ┌─────────────────┐  │ │
  │  │WorkflowTraceRecorder│ │   WorkflowBuilder    │ │WorkflowAssemblyBinder│ │ │  
  │  │ .RecordStep()        │ │ .BindAsExecutor()    │ │ .BuildFromAssembly()│ │ │
  │  │ .ToAgentTrace()      │ │ .UseEventStreaming() │ │ .DiscoverAgents()   │ │ │
  │  │ .Serialize()         │ │ .WithTimeout()       │ │ .ValidateBinding()  │ │ │
  │  └─────────────────────┘  └─────────────────────┘  └─────────────────┘  │ │
  │                                                                         │ ││  └────────────────────────────────────────────────────────────────────────┘ │
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                        Benchmarks Layer                                 │ │
│  ├────────────────────────────────────────────────────────────────────────┤ │
│  │                                                                         │ │
│  │  ┌─────────────────────────┐  ┌─────────────────────────────────────┐   │ │
│  │  │   PerformanceBenchmark  │  │   AgenticBenchmark (preset factory) │   │ │
│  │  │   • Latency             │  │   • AgenticExecution                │   │ │
│  │  │   • Throughput          │  │   • ToolCallAccuracy / RagQuality   │   │ │
│  │  │   • Cost                │  │   • Safety / Conversational / …     │   │ │
│  │  │  (AgentEval.Core)       │  │  (AgentEval.Evals.Agentic)          │   │ │
│  │  └─────────────────────────┘  └─────────────────────────────────────┘   │ │
│  │                                                                         │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                       Integration Layer                                 │ │
│  ├────────────────────────────────────────────────────────────────────────┤ │
│  │                                                                         │ │
│  │  ┌─────────────────┐  ┌────────────────────────┐  ┌─────────────────┐   │ │
│  │  │  MAFEvaluationHarness │  │MicrosoftEvaluatorAdapter│ │ChatClientAdapter│   │ │
│  │  │  (MAF support)  │  │(MS.Extensions.AI.Eval) │  │ (Generic)       │   │ │
│  │  └─────────────────┘  └────────────────────────┘  └─────────────────┘   │ │
│  │                                                                         │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
│                                                                              │
│  ┌────────────────────────────────────────────────────────────────────────┐ │
│  │                    Production Infrastructure                            │ │
│  ├────────────────────────────────────────────────────────────────────────┤ │
│  │                                                                         │ │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐                      │ │
│  │  │IResultExporter│ │IDatasetLoader│ │  Tracing/   │                      │ │
│  │  │JUnit/MD/JSON │  │JSONL/YAML/CSV │  │Record+Replay│                      │ │
│  │  └─────────────┘  └─────────────┘  └─────────────┘                      │ │
│  │                                                                         │ │
│  │  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐  ┌─────────────┐    │ │
│  │  │  RedTeam/   │  │ResponsibleAI│  │ Calibration │  │ Comparison  │    │ │
│  │  │ Attack+Eval │  │Safety Metrics│  │Multi-Judge  │  │Stochastic   │    │ │
│  │  │IAttackType- │  └─────────────┘  └─────────────┘  └─────────────┘    │ │
│  │  │  Registry   │                                                        │ │
│  │  └─────────────┘                                                        │ │
│  │                                                                         │ │
│  └────────────────────────────────────────────────────────────────────────┘ │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Metric Hierarchy

AgentEval uses interface segregation to organize metrics by their requirements:

```
IMetric (base interface)
│
├── Properties:
│   ├── Name: string
│   └── Description: string
│
├── Methods:
│   └── EvaluateAsync(EvaluationContext, CancellationToken) -> MetricResult
│
├── IRAGMetric : IMetric
│   ├── RequiresContext: bool
│   ├── RequiresGroundTruth: bool
│   │
│   └── Implementations:
│       ├── FaithfulnessMetric      - Is response supported by context?
│       ├── RelevanceMetric         - Is response relevant to query?
│       ├── ContextPrecisionMetric  - Was context useful for the answer?
│       ├── ContextRecallMetric     - Does context cover ground truth?
│       └── AnswerCorrectnessMetric - Is response factually correct?
│
├── IAgenticMetric : IMetric
│   ├── RequiresToolUsage: bool
│   │
│   └── Implementations:
│       ├── ToolSelectionMetric   - Were correct tools called?
│       ├── ToolArgumentsMetric   - Were tool arguments correct?
│       ├── ToolSuccessMetric     - Did tool calls succeed?
│       ├── ToolEfficiencyMetric  - Were tools used efficiently?
│       └── TaskCompletionMetric  - Was the task completed?
│
└── IEmbeddingMetric : IMetric (implicit)
    ├── RequiresEmbeddings: bool
    │
    └── Implementations:
        ├── AnswerSimilarityMetric         - Response vs ground truth similarity
        ├── ResponseContextSimilarityMetric - Response vs context similarity
        └── QueryContextSimilarityMetric    - Query vs context similarity
```

---

## Data Flow

### Single Agent Evaluation
```
┌─────────────┐    ┌──────────────┐    ┌─────────────┐    ┌──────────────┐
│  Test Case  │───▶│ IEvaluationHarness │───▶│ Agent Under │───▶│   Response   │
│   (Input)   │    │              │    │    Test     │    │   (Output)   │
└─────────────┘    └──────────────┘    └─────────────┘    └──────────────┘
                          │                                       │
                          │                                       │
                          ▼                                       ▼
                   ┌──────────────┐                       ┌──────────────┐
                   │Tool Tracking │                       │  Evaluation  │
                   │ (timeline,   │                       │   Context    │
                   │  arguments)  │                       │              │
                   └──────────────┘                       └──────────────┘
                          │                                       │
                          └───────────────────┬───────────────────┘
                                              │
                                              ▼
                                    ┌──────────────────┐
                                    │  Metric Runner   │
                                    │  (evaluates all  │
                                    │   configured     │
                                    │   metrics)       │
                                    └──────────────────┘
                                              │
                                              ▼
                                    ┌──────────────────┐
                                    │   Test Result    │
                                    │  • Score         │
                                    │  • Passed/Failed │
                                    │  • ToolUsage     │
                                    │  • Performance   │
                                    │  • FailureReport │
                                    └──────────────────┘
                                              │
                                              ▼
                                    ┌──────────────────┐
                                    │  Result Exporter │
                                    │  • JUnit XML     │
                                    │  • Markdown      │
                                    │  • JSON          │
                                    └──────────────────┘
```

### Workflow Evaluation  
```
┌─────────────────┐    ┌────────────────────┐    ┌─────────────────┐
│ WorkflowTestCase│───▶│WorkflowEvaluationHarness │───▶│  MAFWorkflow    │
│ (Agents+Graph)  │    │                    │    │ (Multi-Agent)   │
└─────────────────┘    └────────────────────┘    └─────────────────┘
                              │                           │
                              │                           ▼
                              │                  ┌─────────────────┐
                              │                  │ WorkflowExecution│
                              │                  │ • Agent 1       │
                              │                  │ • Agent 2       │
                              │                  │ • Agent N       │
                              │                  │ • Event Stream  │
                              │                  │ • Graph Traversal│
                              │                  └─────────────────┘
                              │                           │
                              ▼                           ▼
                   ┌─────────────────────┐       ┌────────────────────┐
                   │ MAFWorkflowEventBridge │       │WorkflowExecutionResult│
                   │ • Event Processing  │       │ • Per-Executor Data│
                   │ • Timeout Handling  │       │ • Graph Definition │
                   │ • Tool Aggregation  │       │ • Tool Usage       │
                   │ • Performance Tracking│      │ • Performance      │
                   └─────────────────────┘       └────────────────────┘
                              │                           │
                              └─────────────┬─────────────┘
                                            │
                                            ▼
                                  ┌──────────────────────┐
                                  │ Workflow Assertions  │
                                  │ • Structure validation│
                                  │ • Per-executor checks│
                                  │ • Graph verification │
                                  │ • Tool chain analysis│
                                  │ • Performance bounds │
                                  └──────────────────────┘
                                            │
                                            ▼
                                  ┌──────────────────────┐
                                  │ WorkflowTestResult   │
                                  │ • Overall Pass/Fail  │
                                  │ • Per-Executor Results│
                                  │ • Graph Visualization│
                                  │ • Tool Usage Report  │
                                  │ • Performance Summary│
                                  └──────────────────────┘
```

---

## Key Models

### EvaluationContext

The central data structure passed to all metrics:

```csharp
public class EvaluationContext
{
    // Identification
    public string EvaluationId { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    // Core data
    public required string Input { get; init; }      // User query
    public required string Output { get; init; }     // Agent response
    
    // RAG-specific
    public string? Context { get; init; }            // Retrieved context
    public string? GroundTruth { get; init; }        // Expected answer
    
    // Agentic-specific
    public ToolUsageReport? ToolUsage { get; init; } // Tool calls made
    public IReadOnlyList<string>? ExpectedTools { get; init; }
    
    // Performance
    public PerformanceMetrics? Performance { get; init; }
    public ToolCallTimeline? Timeline { get; init; } // Execution trace
    
    // Extensibility
    public IDictionary<string, object?> Properties { get; }
}
```

### MetricResult

The result of evaluating a single metric:

```csharp
public class MetricResult
{
    public required string MetricName { get; init; }
    public required double Score { get; init; }       // 0-100 scale
    public bool Passed { get; init; }
    public string? Explanation { get; init; }
    public IDictionary<string, object>? Details { get; init; }
    
    // Factory methods
    public static MetricResult Pass(string name, double score, string? explanation = null);
    public static MetricResult Fail(string name, string explanation, double score = 0);
}
```

### ToolUsageReport

Tracks all tool calls made during an agent run:

```csharp
public class ToolUsageReport
{
    public IReadOnlyList<ToolCallRecord> Calls { get; }
    public int Count { get; }
    public int SuccessCount { get; }
    public int FailureCount { get; }
    public TimeSpan TotalDuration { get; }
    
    // Fluent assertions
    public ToolUsageAssertions Should();
}
```

### PerformanceMetrics

Captures timing and cost information:

```csharp
public class PerformanceMetrics
{
    public TimeSpan TotalDuration { get; set; }
    public TimeSpan? TimeToFirstToken { get; set; }
    public TokenUsage? Tokens { get; set; }
    public decimal? EstimatedCost { get; set; }
    
    // Fluent assertions
    public PerformanceAssertions Should();
}
```

### WorkflowExecutionResult

Result of workflow evaluation with multi-agent data:

```csharp
public class WorkflowExecutionResult
{
    public required string WorkflowId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required TimeSpan Duration { get; init; }
    
    // Graph structure
    public WorkflowGraphDefinition? GraphDefinition { get; init; }
    
    // Per-executor results
    public IReadOnlyDictionary<string, ExecutorResult> ExecutorResults { get; init; }
    
    // Aggregated data
    public ToolUsageReport? ToolUsage { get; init; }        // All tool calls
    public PerformanceMetrics? Performance { get; init; }   // Total cost/timing
    public string? FinalOutput { get; init; }               // Workflow output
    
    // Assertions
    public WorkflowResultAssertions Should();
}
```

### ExecutorResult

Individual agent performance within a workflow:

```csharp  
public class ExecutorResult
{
    public required string ExecutorId { get; init; }
    public required string AgentName { get; init; }
    public string? Input { get; init; }
    public string? Output { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public ToolUsageReport? ToolUsage { get; init; }
    public PerformanceMetrics? Performance { get; init; }
    public bool HasError { get; init; }
    public string? ErrorMessage { get; init; }
}
```

### WorkflowGraphDefinition

Represents the workflow structure and execution path:

```csharp
public class WorkflowGraphDefinition
{
    public IReadOnlyList<WorkflowNode> Nodes { get; init; }
    public IReadOnlyList<WorkflowEdge> Edges { get; init; }
    public string? EntryPoint { get; init; }
    public string? ExitPoint { get; init; }
    public IReadOnlyList<string>? ExecutionPath { get; init; }
    
    // Validation helpers
    public bool HasNode(string nodeId);
    public bool HasEdge(string source, string target);
    public IEnumerable<string> GetExecutionOrder();
}
```

---

## Design Patterns

### 1. Interface Segregation (ISP)

Metrics only require what they need:

```csharp
// RAG metrics need context
public interface IRAGMetric : IMetric
{
    bool RequiresContext { get; }
    bool RequiresGroundTruth { get; }
}

// Agentic metrics need tool usage
public interface IAgenticMetric : IMetric
{
    bool RequiresToolUsage { get; }
}
```

### 2. Adapter Pattern

Enables integration with different frameworks:

```csharp
// Adapt any IChatClient to IEvaluableAgent
public class ChatClientAgentAdapter : IEvaluableAgent
{
    private readonly IChatClient _chatClient;
    
    public async Task<AgentResponse> InvokeAsync(string input, CancellationToken ct)
    {
        var response = await _chatClient.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, input) }, ct);
        return new AgentResponse { Text = response.Message.Text };
    }
}

// Wrap Microsoft's evaluators for AgentEval
public class MicrosoftEvaluatorAdapter : IMetric
{
    private readonly IEvaluator _msEvaluator;
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct)
    {
        var msResult = await _msEvaluator.EvaluateAsync(...);
        return new MetricResult
        {
            Score = ScoreNormalizer.From1To5(msResult.Score),
            ...
        };
    }
}
```

### 3. Fluent API

Intuitive assertion chaining:

```csharp
result.ToolUsage!
    .Should()
    .HaveCalledTool("SearchTool")
        .BeforeTool("AnalyzeTool")
        .WithArguments(args => args.ContainsKey("query"))
    .And()
    .HaveNoErrors()
    .And()
    .HaveToolCountBetween(1, 5);

result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(1))
    .HaveEstimatedCostUnder(0.10m);
```

### 4. Registry Pattern

Centralized metric management:

```csharp
var registry = new MetricRegistry();
registry.Register(new FaithfulnessMetric(chatClient));
registry.Register(new ToolSelectionMetric(expectedTools));

// Run all registered metrics
foreach (var metric in registry.GetAll())
{
    var result = await metric.EvaluateAsync(context);
}
```

The registry pattern extends to exporters and attack types:

```csharp
// Exporter registry (auto-populated via DI)
var exporters = serviceProvider.GetRequiredService<IExporterRegistry>();
var jsonExporter = exporters.GetRequired("Json");
var allFormats = exporters.GetRegisteredFormats(); // Json, Junit, Markdown, Csv, Trx, ...

// Attack type registry (pre-populated with 9 built-in + DI-registered)
var attacks = serviceProvider.GetRequiredService<IAttackTypeRegistry>();
var promptInjection = attacks.GetRequired("PromptInjection");
var llm01 = attacks.GetByOwaspId("LLM01"); // All attacks for OWASP LLM01
```

---

## Package Structure

The codebase is organized into 6 internal projects (single NuGet package):

```
src/
├── AgentEval.Abstractions/       # Public contracts
│   ├── Core/                     # IMetric, IEvaluableAgent, IEvaluationHarness, etc.
│   ├── Models/                   # TestCase, TestResult, ToolCallRecord, PerformanceMetrics
│   ├── Embeddings/               # IAgentEvalEmbeddings
│   ├── Snapshots/                # ISnapshotComparer, ISnapshotStore
│   └── DependencyInjection/      # AgentEvalServiceOptions
│
├── AgentEval.Core/               # Implementations
│   ├── Assertions/               # ToolUsageAssertions, PerformanceAssertions, ResponseAssertions
│   ├── Metrics/                  # RAG/, Agentic/, Retrieval/, Safety/, Embedding
│   ├── Comparison/               # StochasticRunner, ModelComparer, StatisticsCalculator
│   ├── Tracing/                  # TraceRecordingAgent, TraceReplayingAgent, ChatTraceRecorder
│   ├── Calibration/              # CalibratedJudge, VotingStrategy
│   ├── Benchmarks/               # PerformanceBenchmark (the agentic preset factory lives in AgentEval.Evals.Agentic)
│   ├── Adapters/                 # MicrosoftEvaluatorAdapter, ChatClientAgentAdapter
│   ├── Testing/                  # FakeChatClient
│   └── DependencyInjection/      # AddAgentEval()
│
├── AgentEval.DataLoaders/        # Data loading and export
│   ├── DataLoaders/              # JSON, JSONL, YAML, CSV loaders
│   ├── Exporters/                # JUnit XML, Markdown, JSON, CSV, TRX exporters
│   ├── Output/                   # TableFormatter, AgentEvalTestBase, TimeTravelTrace
│   └── DependencyInjection/      # AddAgentEvalDataLoaders()
│
├── AgentEval.MAF/                # Microsoft Agent Framework
│   ├── MAFAgentAdapter.cs        # Wraps AIAgent → IStreamableAgent
│   ├── MAFEvaluationHarness.cs   # MAF-specific evaluation harness
│   ├── MAFWorkflowAdapter.cs     # Workflow integration
│   └── WorkflowEvaluationHarness.cs
│
├── AgentEval.RedTeam/            # Security testing
│   ├── RedTeamRunner.cs          # Orchestrator
│   ├── AttackPipeline.cs         # Attack execution
│   ├── Attacks/                  # 9 built-in attack types
│   ├── Evaluators/               # Probe evaluators
│   ├── ResponsibleAI/            # Toxicity, Bias, Misinformation metrics
│   └── DependencyInjection/      # AddAgentEvalRedTeam()
│
└── AgentEval/                    # Umbrella (packaging only)
    └── AddAgentEvalAll()         # Registers all services from all sub-projects
```

---

## Metrics Taxonomy

AgentEval organizes metrics into a clear taxonomy to aid discovery and selection. See [ADR-007](adr/007-metrics-taxonomy.md) for the formal decision.

### Categorization by Computation Method

| Prefix | Method | Cost | Use Case |
|--------|--------|------|----------|
| `llm_` | LLM-as-judge | API cost | High-accuracy quality assessment |
| `code_` | Code logic | Free | CI/CD, high-volume testing |
| `embed_` | Embedding similarity | Low API cost | Cost-effective semantic checks |

### Categorization by Evaluation Domain

| Domain | Interface | Examples |
|--------|-----------|----------|
| RAG | `IRAGMetric` | Faithfulness, Relevance, Context Precision |
| Agentic | `IAgenticMetric` | Tool Selection, Tool Success, Task Completion |
| Conversation | Special | ConversationCompleteness |
| Safety | `ISafetyMetric` | Toxicity, Groundedness |

### Category Flags (ADR-007)

Metrics can declare multiple categories via `MetricCategory` flags:

```csharp
public override MetricCategory Categories => 
    MetricCategory.RAG | 
    MetricCategory.RequiresContext | 
    MetricCategory.LLMEvaluated;
```

For complete metric documentation, see:
- [Metrics Reference](metrics-reference.md) - Complete catalog
- [Evaluation Guide](evaluation-guide.md) - How to choose metrics

---

## Calibration Layer

AgentEval provides judge calibration for reliable LLM-as-judge evaluations. See [ADR-008](adr/008-calibrated-judge-multi-model.md) for design decisions.

### CalibratedJudge Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                           CalibratedJudge                                    │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  Input:                                                                      │
│  ┌─────────────────┐    ┌─────────────────────────────────────────────────┐ │
│  │EvaluationContext│───▶│ Factory Pattern: Func<string, IMetric>          │ │
│  └─────────────────┘    │ Each judge gets its own metric with its client  │ │
│                         └─────────────────────────────────────────────────┘ │
│                                              │                               │
│  Parallel Execution:                         ▼                               │
│  ┌───────────────┐   ┌───────────────┐   ┌───────────────┐                  │
│  │  Judge 1      │   │  Judge 2      │   │  Judge 3      │                  │
│  │  (GPT-4o)     │   │  (Claude)     │   │  (Gemini)     │                  │
│  │  Score: 85    │   │  Score: 88    │   │  Score: 82    │                  │
│  └───────────────┘   └───────────────┘   └───────────────┘                  │
│         │                   │                   │                            │
│         └───────────────────┼───────────────────┘                            │
│                             ▼                                                │
│  Aggregation:    ┌─────────────────────────────────┐                        │
│                  │ VotingStrategy                  │                        │
│                  │ • Median (default, robust)      │                        │
│                  │ • Mean (equal weight)           │                        │
│                  │ • Unanimous (require consensus) │                        │
│                  │ • Weighted (custom weights)     │                        │
│                  └─────────────────────────────────┘                        │
│                             │                                                │
│  Output:                    ▼                                                │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ CalibratedResult                                                     │    │
│  │ • Score: 85.0 (median)                                               │    │
│  │ • Agreement: 96.2%                                                   │    │
│  │ • JudgeScores: {GPT-4o: 85, Claude: 88, Gemini: 82}                 │    │
│  │ • ConfidenceInterval: [81.5, 88.5]                                   │    │
│  │ • StandardDeviation: 3.0                                             │    │
│  │ • HasConsensus: true                                                 │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Key Classes

| Class | Purpose |
|-------|---------|
| `CalibratedJudge` | Coordinates multiple judges with parallel execution |
| `CalibratedResult` | Result with score, agreement, CI, per-judge scores |
| `VotingStrategy` | Aggregation method enum |
| `CalibratedJudgeOptions` | Configuration for timeout, parallelism, consensus |
| `ICalibratedJudge` | Interface for testability |

---

## Model Comparison Markdown Export

AgentEval provides rich Markdown export for model comparison results:

```csharp
// Full report with all sections
var markdown = result.ToMarkdown();

// Compact table with medals
var table = result.ToRankingsTable();

// GitHub PR comment with collapsible details
var comment = result.ToGitHubComment();

// Save to file
await result.SaveToMarkdownAsync("comparison.md");
```

### Export Options

```csharp
// Full report (default)
result.ToMarkdown(MarkdownExportOptions.Default);

// Minimal (rankings only)
result.ToMarkdown(MarkdownExportOptions.Minimal);

// Custom
result.ToMarkdown(new MarkdownExportOptions
{
    IncludeStatistics = true,
    IncludeScoringWeights = false,
    HeaderEmoji = "🔬"
});
```

---

## Behavioral Policy Assertions

Safety-critical assertions for enterprise compliance:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                      Behavioral Policy Assertions                            │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│  NeverCallTool("DeleteDatabase", because: "admin only")                     │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ Scans all tool calls for forbidden tool name                        │    │
│  │ Throws BehavioralPolicyViolationException with audit details        │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
│  NeverPassArgumentMatching(@"\d{3}-\d{2}-\d{4}", because: "SSN is PII")    │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ Scans all tool arguments with regex pattern                         │    │
│  │ Auto-redacts matched values in exception (e.g., "1***9")            │    │
│  │ Throws BehavioralPolicyViolationException with RedactedValue        │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
│  MustConfirmBefore("TransferFunds", because: "requires consent")            │
│  ┌─────────────────────────────────────────────────────────────────────┐    │
│  │ Checks that confirmation tool was called before action              │    │
│  │ Default confirmation tools: "get_confirmation", "confirm"           │    │
│  │ Throws if action was called without prior confirmation              │    │
│  └─────────────────────────────────────────────────────────────────────┘    │
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
```

### BehavioralPolicyViolationException

Structured exception for audit trails:

```csharp
catch (BehavioralPolicyViolationException ex)
{
    // Structured properties for logging/audit
    Console.WriteLine($"Policy: {ex.PolicyName}");       // "NeverCallTool(DeleteDB)"
    Console.WriteLine($"Type: {ex.ViolationType}");      // "ForbiddenTool"
    Console.WriteLine($"Action: {ex.ViolatingAction}");  // "Called DeleteDB 1 time(s)"
    Console.WriteLine($"Because: {ex.Because}");         // Developer's reason
    
    // For PII detection
    Console.WriteLine($"Pattern: {ex.MatchedPattern}");  // @"\d{3}-\d{2}-\d{4}"
    Console.WriteLine($"Value: {ex.RedactedValue}");     // "1***9" (auto-redacted)
    
    // Actionable suggestions
    foreach (var s in ex.Suggestions ?? [])
        Console.WriteLine($"  → {s}");
}
```

---

## Internal Project Structure

AgentEval ships as a **single NuGet package** (`AgentEval`) but is internally organized into 6 projects for maintainability and compile-time dependency enforcement (see [ADR-016](adr/016-monolith-modularization.md)).

### Dependency Graph

```
AgentEval (NuGet package — umbrella)
├── AgentEval.Abstractions     → M.E.AI.Abstractions
├── AgentEval.Core             → Abstractions + M.E.AI + M.E.AI.Eval.Quality + S.N.Tensors
├── AgentEval.DataLoaders      → Abstractions + Core + YamlDotNet
├── AgentEval.MAF              → Abstractions + Core + M.Agents.AI + M.Agents.AI.Workflows
└── AgentEval.RedTeam          → Abstractions + Core + M.E.AI + M.E.DI + PdfSharp-MigraDoc
```

### Project Responsibilities

| Project | Files | Purpose |
|---|---|---|
| `AgentEval.Abstractions` | ~48 | Public contracts: `IMetric`, `IEvaluableAgent`, models, enums |
| `AgentEval.Core` | ~63 | Implementations: metrics, assertions, comparison, tracing, testing |
| `AgentEval.DataLoaders` | ~23 | Data loaders (JSON/YAML/CSV/JSONL), exporters, output formatting |
| `AgentEval.MAF` | 7 | Microsoft Agent Framework adapters and harnesses |
| `AgentEval.RedTeam` | 61 | Security scanning, attack types, evaluators, compliance reports |
| `AgentEval` (umbrella) | 1 | Packaging + `AddAgentEvalAll()` DI convenience method |

All projects use `RootNamespace=AgentEval` so consumers see no namespace changes.

---

## Benchmark family registration

> **Architecture established by [ADR-017](adr/017-unified-benchmarks-namespace.md), implemented in v0.10.0-beta.**

AgentEval ships eight benchmark families — **Agentic, GDPR, EU AI Act, OWASP, MITRE, LongMemEval, Memory, Performance** — and is built to absorb future families (HIPAA, PCI-DSS, ISO 42001, NIS2, SOC 2, UK AI Bill, …) without touching the CLI or Mission Control. Every family plugs into a single source of truth: `AgentEval.Core.Benchmarks.BenchmarkFamilyRegistry`.

This section documents how to add a benchmark family. Most consumers don't need this — they just `using AgentEval.Benchmarks;` and call the static factories. This section is for AgentEval contributors and third-party plugin authors.

### Two registration shapes

Benchmark families register in one of two shapes, depending on whether their natural result type fits the `EvalInput → EvalResult` envelope:

#### Shape A — `CompositeEval`-native

Most benchmark families (Agentic, GDPR, EU AI Act, OWASP, MITRE, Performance) ship a static factory class in the `AgentEval.Benchmarks` namespace whose preset methods return `CompositeEval`. The composite flows through the unified `EvaluateAsync(EvalInput) → EvalResult` pipeline (Convention 2).

```csharp
// Factory — partial class declared per-assembly, all under AgentEval.Benchmarks
namespace AgentEval.Benchmarks;

public static partial class OwaspBenchmark
{
    public static OwaspBenchmarkRun Top10(IEvaluator? judge = null) => /* ... */;
    public static OwaspBenchmarkRun Smoke(IEvaluator? judge = null) => /* ... */;
    public static OwaspBenchmarkRun AuditGrade(IEvaluator? judge = null) => /* ... */;
    public static OwaspBenchmarkRun Top10ForRag(IEvaluator? judge = null) => /* ... */;
}

// Registration — internal, in the same assembly, runs on assembly load
namespace AgentEval.RedTeam.Compliance;

internal static class OwaspBenchmarkRegistration
{
    [ModuleInitializer]
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "owasp",
            description: "OWASP LLM Top 10 v2.0 red-team benchmark",
            defaultCostTier: CostTier.Medium,
            presets:
            [
                new("top10",     "All 9 attacks at Quick intensity (default)", CostTier.Medium),
                new("smoke",     "3 MVP attacks — CI-friendly",                 CostTier.Low),
                new("audit",     "All 9 attacks at Comprehensive intensity",    CostTier.High),
                new("top10-rag", "Comprehensive intensity, RAG-vector depth",  CostTier.High),
            ],
            runnerType: typeof(OwaspBenchmarkRun),
            runnerFactory: preset => ResolvePresetRun(preset, judge: null),
            evaluateAsync: async (input, judge, ct) =>
            {
                var presetName = input.Metadata?.TryGetValue("preset", out var p) == true
                    ? p?.ToString() ?? "top10"
                    : "top10";
                var run = ResolvePresetRun(presetName, judge);
                return await run.EvaluateAsync(input, ct);
            },
            docLinkUrl: "https://github.com/joslat/AgentEval/blob/main/docs/redteam/owasp.md",
            owningAssemblyName: typeof(OwaspBenchmark).Assembly.GetName().Name));
    }
}
```

#### Shape B — external-dataset / multi-turn

Some benchmarks don't fit the single-shot `EvalInput → EvalResult` shape because their natural semantics are "N questions → accuracy" (LongMemEval) or "stateful runner with required dependencies" (Memory). They register a runner type plus a runner factory; `EvaluateAsync` is null and the registry surfaces them in `bench --list` as Shape B.

```csharp
namespace AgentEval.Memory.External.LongMemEval;

internal static class LongMemEvalBenchmarkRegistration
{
    [ModuleInitializer]
    public static void Register()
    {
        BenchmarkFamilyRegistry.Register(new BenchmarkFamily(
            name: "longmemeval",
            description: "LongMemEval (ICLR 2025) — academic memory benchmark",
            defaultCostTier: CostTier.Medium,
            presets:
            [
                new("subset", "Embedded 30-question stratified sample", CostTier.Medium),
                new("full",   "Full ~500-question dataset (requires download)", CostTier.High),
            ],
            runnerType: typeof(LongMemEvalBenchmarkRunner),
            runnerFactory: preset =>
            {
                var client = LongMemEvalRunnerHostingContext.Current?.ChatClient
                    ?? throw new InvalidOperationException("Populate LongMemEvalRunnerHostingContext first.");
                return preset switch
                {
                    "subset" => LongMemEvalBenchmark.Subset(client),
                    "full"   => LongMemEvalBenchmark.Full(client),
                    _ => throw new ArgumentException($"Unknown preset '{preset}'.")
                };
            },
            evaluateAsync: null,  // Shape B — semantics don't map onto (EvalInput) → EvalResult
            docLinkUrl: "https://arxiv.org/abs/2410.10813",
            owningAssemblyName: typeof(LongMemEvalBenchmark).Assembly.GetName().Name));
    }
}
```

Both shapes are equally first-class in `bench --list` — Shape B families just expose their custom runner type via `RunnerType` so CLI / Mission Control can produce typed-output hints.

### The four conventions at a glance

ADR-017 establishes four durable conventions that apply to every benchmark family, current and future:

1. **Top-level factory namespace** = `AgentEval.Benchmarks`. The factory class is `public static partial class {Family}Benchmark`. Pinned by `BenchmarkNamespaceContractTests`.
2. **`EvaluateAsync(EvalInput, CT) → EvalResult` adapter** is the canonical result-type homogenisation primitive. Every benchmark family that ships a non-`CompositeEval`-native result type (e.g. `LatencyBenchmarkResult`, `OWASPComplianceReport`, `MITREATLASReport`) provides this adapter so its results flow through the same `IRunOutputStore` / audit-chain / Mission Control rendering pipeline. The natural result type is preserved in `Provenance` for downstream consumers that want richer data. Pinned by `PerformanceBenchmarkAdapterTests` + `OwaspBenchmarkTests` round-trip + `MitreBenchmarkTests` round-trip.
3. **`BenchmarkFamilyRegistry` is canonical**. Every family auto-registers via `[ModuleInitializer]`. The CLI / Mission Control read from the registry — there are no hardcoded family lists anywhere. Pinned by `BenchmarkFamilyRegistryTests` (12 tests) + `BenchListCommandTests.OutputComesFromRegistry` (extensibility test that registers a synthetic UUID-named family at runtime and asserts it appears in `bench --list`).
4. **Opus gate-review after every phase** of an architectural arc. Process convention, not code. Sign-off docs live in `strategy/FutureFeatures/todo/lastreview/`.

See [ADR-017 §"Conventions established by this ADR"](adr/017-unified-benchmarks-namespace.md#conventions-established-by-this-adr) for the full normative text and [§"Verification"](adr/017-unified-benchmarks-namespace.md#verification) for the contract-test mapping.

### Adding a new benchmark family — 5-step walkthrough

To add a new benchmark family (say, HIPAA compliance):

1. **csproj** — Create `src/AgentEval.Compliance.Hipaa/` with `<RootNamespace>AgentEval.Compliance.Hipaa</RootNamespace>` and `<IsPackable>false</IsPackable>`. Reference `AgentEval.Abstractions` + `AgentEval.Core` (+ `AgentEval.DataLoaders` if loading embedded YAML/JSON). Add `PrivateAssets="all"` ProjectReference to it from `src/AgentEval/AgentEval.csproj` (the umbrella).
2. **Factory** — Add `HipaaBenchmark.cs` with `namespace AgentEval.Benchmarks; public static partial class HipaaBenchmark { ... }`. Expose preset factory methods (`Standard()`, `Strict()`, etc.) returning `CompositeEval` for Shape A, or a runner type for Shape B.
3. **`EvaluateAsync` adapter** (Shape A with bespoke result type only) — If your preset returns a custom result record alongside `EvalResult`, add an `EvaluateAsync(EvalInput, CancellationToken) → EvalResult` method that synthesises an `EvalResult` whose `SubResults` enumerate per-leaf metrics and preserves the custom record in `Provenance`.
4. **Registration** — Add `HipaaBenchmarkRegistration.cs` with `internal static class HipaaBenchmarkRegistration { [ModuleInitializer] public static void Register() { BenchmarkFamilyRegistry.Register(new BenchmarkFamily(...)); } }`. Suppress CA2255 inline with a one-line justification comment.
5. **Contract test inclusion** — Add `HipaaBenchmark` to the reflection enumerator in `BenchmarkNamespaceContractTests` (or just let the enumerator pick it up automatically — it scans `*Benchmark`-suffixed types across umbrella sub-assemblies). Add an integration test in `BenchmarkFamilyRegistryIntegrationTests` asserting the family registers on assembly load.

Done. The CLI's `bench --list` will pick up the new family on next run; `bench hipaa --help` will enumerate its presets from the registry. No changes to `src/AgentEval.Cli/` are required.

### OWASP preset cost gradient (concrete example)

The four `OwaspBenchmark` presets demonstrate a clean depth/cost gradient on the same 9-attack roster:

| Preset | Attacks | Intensity | Timeout | Cost tier | Use case |
|---|---|---|---|---|---|
| `Smoke` | 3 | Quick | 10 min | Low | CI-friendly quick check (PromptInjection + Jailbreak + PIILeakage) |
| `Top10` | 9 | Quick | 10 min | Medium | Standard OWASP LLM Top 10 sweep |
| `Top10ForRag` | 9 | **Comprehensive** | **20 min** | **High** | RAG threat model — indirect-injection coverage depth |
| `AuditGrade` | 9 | Comprehensive | 30 min | High | Full audit-grade evidence pack |

`Top10ForRag` sits between `Top10` and `AuditGrade` — same Comprehensive intensity as `AuditGrade` (an attacker needs only one working poisoned-document payload, so the defender needs *coverage depth* on injection techniques), but a tighter 20-minute timeout to differentiate it as RAG-triage rather than audit-grade evidence. Two divergence-pinning tests (`Top10ForRag_IsMateriallyDistinctFromTop10_DeepProbeCoverage` + `Top10ForRag_ProbeDepth_MatchesAuditGrade_NotTop10`) prevent future regressions from collapsing it back to a label-only duplicate of `Top10`.

The cost-tier gradient (Low → Medium → High → High) is surfaced by `bench --list` so operators can pick the right preset for their CI / pre-merge / audit-pipeline budgets without having to read the source.

---

## See Also

- [Extensibility Guide](extensibility.md) - Creating custom metrics and plugins
- [Embedding Metrics](embedding-metrics.md) - Semantic similarity evaluation
- [Benchmarks Guide](benchmarks.md) - Running standard benchmarks
- [Metrics Reference](metrics-reference.md) - Complete metric catalog
- [Evaluation Guide](evaluation-guide.md) - Metric selection guidance
- [ADR-017: Unified Benchmarks Namespace](adr/017-unified-benchmarks-namespace.md) - Architectural rationale for the registry + namespace + conventions
