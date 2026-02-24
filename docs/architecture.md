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
│  │  │   PerformanceBenchmark  │  │        AgenticBenchmark             │   │ │
│  │  │   • Latency             │  │   • ToolAccuracy                    │   │ │
│  │  │   • Throughput          │  │   • TaskCompletion                  │   │ │
│  │  │   • Cost                │  │   • MultiStepReasoning              │   │ │
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
│  │  └─────────────┘  └─────────────┘  └─────────────┘  └─────────────┘    │ │
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

---

## Package Structure

```
AgentEval/
├── Core/                    # Core interfaces and utilities
│   ├── IMetric.cs
│   ├── IEvaluableAgent.cs
│   ├── IEvaluationHarness.cs
│   ├── IEvaluator.cs
│   ├── IAgentEvalLogger.cs
│   ├── IAgentEvalPlugin.cs
│   ├── IToolUsageExtractor.cs
│   ├── IWorkflowEvaluableAgent.cs
│   ├── AgentEvalBuilder.cs
│   ├── ChatClientAgentAdapter.cs
│   ├── MetricRegistry.cs
│   ├── ScoreNormalizer.cs
│   ├── RetryPolicy.cs
│   ├── LlmJsonParser.cs
│   └── EvaluationDefaults.cs
│
├── Models/                  # Data models
│   ├── TestModels.cs        # TestCase, TestResult, TestSummary
│   ├── WorkflowModels.cs    # WorkflowTestCase, WorkflowTestResult
│   ├── ToolCallRecord.cs
│   ├── ToolUsageReport.cs
│   ├── ToolCallTimeline.cs
│   ├── PerformanceMetrics.cs
│   └── FailureReport.cs
│
├── Metrics/                 # Metric implementations
│   ├── RAG/
│   │   ├── RAGMetrics.cs    # Faithfulness, Relevance, etc.
│   │   └── EmbeddingMetrics.cs
│   ├── Agentic/
│   │   └── AgenticMetrics.cs # ToolSelection, ToolSuccess, etc.
│   ├── Retrieval/
│   │   ├── MRRMetric.cs
│   │   └── RecallAtKMetric.cs
│   └── Safety/
│       └── SafetyMetrics.cs
│
├── Assertions/              # Fluent assertions
│   ├── ToolUsageAssertions.cs
│   ├── PerformanceAssertions.cs
│   ├── ResponseAssertions.cs
│   └── WorkflowResultAssertions.cs
│
├── Benchmarks/              # Benchmarking infrastructure
│   ├── PerformanceBenchmark.cs
│   └── AgenticBenchmark.cs
│
├── Calibration/             # Multi-judge calibration
│   ├── ICalibratedJudge.cs
│   ├── CalibratedJudge.cs
│   ├── CalibratedJudgeOptions.cs
│   ├── CalibratedResult.cs
│   └── VotingStrategy.cs
│
├── Comparison/              # Stochastic & model comparison
│   ├── IAgentFactory.cs
│   ├── IStatisticsCalculator.cs
│   ├── StochasticRunner.cs
│   ├── StochasticOptions.cs
│   ├── StochasticResult.cs
│   ├── ModelComparer.cs
│   └── ModelComparisonResult.cs
│
├── Tracing/                 # Trace record & replay
│   ├── AgentTrace.cs
│   ├── TraceRecordingAgent.cs
│   ├── TraceReplayingAgent.cs
│   ├── TraceSerializer.cs
│   ├── ChatTraceRecorder.cs
│   └── WorkflowTraceRecorder.cs
│
├── Adapters/                # Framework integrations
│   └── MicrosoftEvaluatorAdapter.cs
│
├── MAF/                     # Microsoft Agent Framework
│   ├── MAFAgentAdapter.cs
│   ├── MAFIdentifiableAgentAdapter.cs
│   ├── MAFEvaluationHarness.cs
│   ├── MAFWorkflowAdapter.cs
│   ├── MAFWorkflowEventBridge.cs
│   └── WorkflowEvaluationHarness.cs
│
├── Embeddings/              # Embedding utilities
│   ├── IAgentEvalEmbeddings.cs
│   └── EmbeddingSimilarity.cs
│
├── Exporters/               # Result exporters
│   ├── IResultExporter.cs
│   ├── JUnitXmlExporter.cs
│   ├── MarkdownExporter.cs
│   ├── JsonExporter.cs
│   └── TrxExporter.cs
│
├── DataLoaders/             # Dataset loaders
│   ├── IDatasetLoader.cs
│   ├── JsonlDatasetLoader.cs
│   ├── JsonDatasetLoader.cs
│   ├── YamlDatasetLoader.cs
│   └── CsvDatasetLoader.cs
│
├── Snapshots/               # Snapshot comparison
│   ├── ISnapshotComparer.cs # Interface for DI
│   ├── ISnapshotStore.cs    # Interface for DI
│   └── SnapshotComparer.cs  # SnapshotComparer, SnapshotStore, Options, Results
│
├── Output/                  # Output formatting utilities
│   ├── TableFormatter.cs
│   ├── EvaluationOutputWriter.cs
│   └── StochasticResultExtensions.cs
│
├── RedTeam/                 # Red team security evaluation
│   ├── RedTeamRunner.cs
│   ├── AttackPipeline.cs
│   ├── RedTeamAssertions.cs
│   ├── Attacks/             # Attack strategies
│   └── Evaluators/          # Attack evaluators
│
├── ResponsibleAI/           # Responsible AI metrics
│   ├── ToxicityMetric.cs
│   ├── BiasMetric.cs
│   └── MisinformationMetric.cs
│
├── DependencyInjection/     # DI registration
│   ├── AgentEvalServiceCollectionExtensions.cs
│   └── AgentEvalServiceOptions.cs
│
└── Testing/                 # Test utilities
    └── FakeChatClient.cs
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
    MetricCategory.LLMBased;
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

## See Also

- [Extensibility Guide](extensibility.md) - Creating custom metrics and plugins
- [Embedding Metrics](embedding-metrics.md) - Semantic similarity evaluation
- [Benchmarks Guide](benchmarks.md) - Running standard benchmarks
- [Metrics Reference](metrics-reference.md) - Complete metric catalog
- [Evaluation Guide](evaluation-guide.md) - Metric selection guidance
