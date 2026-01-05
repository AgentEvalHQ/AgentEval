# AgentEval - .NET AI Agent Testing Framework

> **The first .NET-native AI agent testing and evaluation framework**

**Version:** 1.0.0-alpha  
**Last Updated:** January 6, 2026  
**Status:** ✅ Core + Production Infrastructure Complete

## Executive Summary

AgentEval is a comprehensive testing, evaluation, and benchmarking framework designed specifically for AI agents built with Microsoft Agent Framework (MAF) and other .NET AI libraries. It fills a critical gap in the .NET ecosystem where no native AI agent testing framework exists.

### Value Proposition

| Current Pain Point | AgentEval Solution | Status |
|---|---|---|
| No .NET-native agent testing tools | First-class .NET library with fluent API | ✅ Implemented |
| Python tools don't integrate with .NET CI/CD | Native xUnit/NUnit/MSTest support | ✅ Implemented |
| Complex test setup for agents | Simple `TestHarness.RunTestAsync()` | ✅ Implemented |
| No tool call visibility | Full tool tracking with assertions | ✅ Implemented |
| Manual evaluation | AI-powered response evaluation | ✅ Implemented |
| No streaming metrics | Real-time performance tracking | ✅ Implemented |
| No CLI for CI/CD | `agenteval eval` with JUnit/Markdown export | ✅ Implemented |
| Manual dataset management | YAML/JSON/JSONL/CSV loaders | ✅ Implemented |
| No regression testing | Snapshot comparison with SnapshotComparer | ✅ Implemented |

### Relationship to Other .NET Evaluation Libraries

| Library | Focus | AgentEval Relationship |
|---------|-------|----------------------|
| **Microsoft.Extensions.AI.Evaluation** | Quality metrics (Fluency, Coherence, Relevance) | Complementary - can use as optional dependency |
| **kbeaugrand/KernelMemory.Evaluation** | RAGAS for Kernel Memory | Inspired by - similar RAG metrics, different scope |
| **AgentEval** | Agentic testing + tool tracking + benchmarking | Unique focus on agent-specific features |

**Strategic Position:** AgentEval focuses on what the others lack - **tool tracking, streaming metrics, fluent assertions, and benchmarking**. We can optionally integrate Microsoft's evaluators for quality metrics.

---

## Implementation Status

### ✅ Fully Implemented Features

| Feature | Files | Tests |
|---------|-------|-------|
| **Core Interfaces** | IMetric, IRAGMetric, IAgenticMetric, ITestableAgent, IStreamableAgent, ITestHarness, IEvaluator | - |
| **Core Utilities** | ToolUsageExtractor, EvaluationDefaults, LlmJsonParser | - |
| **Models** | ToolCallRecord, ToolUsageReport, PerformanceMetrics, TestCase, TestResult, TestSummary, FailureReport, ToolCallTimeline | 210 tests |
| **Logging** | IAgentEvalLogger, ConsoleAgentEvalLogger, MicrosoftExtensionsLoggingAdapter, NullAgentEvalLogger | ✅ |
| **Plugin System** | IAgentEvalPlugin, IMetricRegistry, AgentEvalBuilder | ✅ |
| **Tool Assertions** | HaveCalledTool, NotHaveCalledTool, HaveCallCount, HaveCallOrder, HaveNoErrors, BeforeTool, AfterTool, WithArgument, WithResultContaining, WithDurationUnder | ✅ |
| **Performance Assertions** | HaveTotalDurationUnder, HaveTimeToFirstTokenUnder, HaveTokenCountUnder, HaveEstimatedCostUnder, HaveToolCallCount | ✅ |
| **Response Assertions** | Contain, ContainAll, ContainAny, NotContain, MatchPattern, HaveLengthBetween, StartWith, EndWith | ✅ |
| **Cost Estimation** | ModelPricing with ConcurrentDictionary, 8+ models (GPT-4o, Claude, etc.), SetPricing for custom models | ✅ |
| **RAG Metrics** | FaithfulnessMetric, RelevanceMetric, ContextPrecisionMetric, ContextRecallMetric, AnswerCorrectnessMetric | ✅ (7 tests) |
| **Agentic Metrics** | ToolSelectionMetric, ToolArgumentsMetric, ToolSuccessMetric, TaskCompletionMetric, ToolEfficiencyMetric | ✅ (17 tests) |
| **Embedding Metrics** | AnswerSimilarityMetric, ResponseContextSimilarityMetric, QueryContextSimilarityMetric | ✅ |
| **Benchmarks** | PerformanceBenchmark (latency, throughput, cost), AgenticBenchmark (tool accuracy, task completion, multi-step) | ✅ |
| **MAF Integration** | MAFAgentAdapter, MAFTestHarness with streaming support, trace-first failure reporting | ✅ |
| **Testing Infrastructure** | FakeChatClient for mocking IChatClient without external dependencies | ✅ |
| **CLI Tool** | AgentEval.Cli with eval, init, list commands; cross-platform with NO_COLOR support | ✅ |
| **Result Exporters** | JSON, JUnit XML, Markdown, TRX formats via IResultExporter | ✅ |
| **Dataset Loaders** | JSON, JSONL, CSV, YAML with field aliasing via IDatasetLoader | ✅ |
| **Snapshot Testing** | SnapshotComparer, SnapshotStore with JSON diff and regex scrubbing | ✅ |
| **Multi-turn Testing** | ConversationRunner, ConversationalTestCase, ConversationCompletenessMetric | ✅ |
| **Sample Datasets** | travel-agent.yaml (agentic), rag-qa.yaml (RAG) with documentation | ✅ |

---

## Competitor Analysis

### 0. Microsoft.Extensions.AI.Evaluation (Official .NET) 🆕

**Status:** `[Experimental("AIEVAL001")]` - Active development in `dotnet/extensions`

**What it does well:**
- Official Microsoft package with enterprise backing
- Clean IEvaluator interface with EvaluationResult
- Quality evaluators: Fluency, Coherence, Relevance (1-5 scores)
- RAG evaluators: Groundedness, Equivalence, Completeness, Retrieval
- Agentic evaluators: TaskAdherence, IntentResolution, ToolCallAccuracy

**Key Interface:**
```csharp
public interface IEvaluator
{
    IReadOnlyCollection<string> EvaluationMetricNames { get; }
    ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default);
}
```

**Gaps we fill:**
- No tool timing/duration tracking
- No per-tool performance metrics
- No fluent assertion API
- No streaming TTFT metrics
- No cost estimation
- No benchmark infrastructure
- No MAF integration
- Uses 1-5 scale (we use 0-100)

---

### 1. DeepEval (Python) ⭐ 12.8k GitHub Stars

**What it does well:**
- 14+ agentic metrics (tool correctness, task completion)
- Red-teaming and vulnerability testing
- Synthetic dataset generation
- CI/CD integration with Confident AI platform

**Key Metrics:**
```python
# DeepEval's agentic metrics
- ToolCorrectnessMetric  # Did agent use right tools?
- TaskCompletionMetric   # Did agent complete the task?
- AgentRelevancyMetric   # Were agent actions relevant?
```

**Gaps we can fill:**
- Python-only (no .NET support)
- No streaming timing metrics
- Complex setup for simple tests

---

### 2. Promptfoo (Node.js) ⭐ 9.7k GitHub Stars

**What it does well:**
- YAML-based test configuration
- Red-teaming with 30+ vulnerability plugins
- Model comparison and A/B testing
- Self-hosted with privacy focus

**Architecture:**
```yaml
# Promptfoo's declarative approach
prompts:
  - "What is {{topic}}?"
providers:
  - openai:gpt-4
  - anthropic:claude-3
tests:
  - vars: { topic: "AI safety" }
    assert:
      - type: contains
        value: "alignment"
```

**Gaps we can fill:**
- Node.js only
- No native .NET integration
- Config-heavy vs code-first approach

---

### 3. RAGAS (Python) ⭐ 12k GitHub Stars

**What it does well:**
- RAG-specific metrics (faithfulness, relevance, context recall)
- Synthetic test data generation
- Academic rigor with published papers

**Key RAG Metrics:**
```python
# RAGAS metrics we should port
- Faithfulness      # Is answer grounded in context?
- AnswerRelevancy   # Does answer address the question?
- ContextPrecision  # Is retrieved context relevant?
- ContextRecall     # Was all needed context retrieved?
```

**Gaps we can fill:**
- Python-only
- RAG-focused, less agentic
- No tool tracking

---

### 4. LangSmith (SaaS by LangChain)

**What it does well:**
- Production monitoring and tracing
- Dataset management
- Human annotation workflows
- Beautiful UI/UX

**Gaps we can fill:**
- Paid SaaS model
- LangChain ecosystem lock-in
- No offline/local option

---

### 5. Inspect AI (UK AI Safety Institute)

**What it does well:**
- Government-backed safety focus
- Sandboxed tool execution
- Multi-turn conversation testing
- Agentic benchmarks

**Gaps we can fill:**
- Python-only
- Safety-focused, less general purpose
- Steep learning curve

---

### 6. Braintrust

**What it does well:**
- Production evals at scale
- Experiment tracking
- Multi-language SDKs (including TypeScript)

**Gaps we can fill:**
- SaaS-first model
- No native .NET SDK
- Enterprise pricing

---

## AgentEval Unique Differentiators

| Feature | DeepEval | Promptfoo | RAGAS | AgentEval |
|---------|----------|-----------|-------|-----------|
| .NET Native | ❌ | ❌ | ❌ | ✅ |
| Fluent API | ❌ | ❌ | ❌ | ✅ |
| MAF Integration | ❌ | ❌ | ❌ | ✅ |
| Tool Tracking | ✅ | ❌ | ❌ | ✅ |
| Streaming Timing | ❌ | ❌ | ❌ | ✅ |
| AI Evaluation | ✅ | ✅ | ✅ | ✅ |
| Cost Estimation | Partial | ❌ | ❌ | ✅ |
| xUnit/NUnit/MSTest | ❌ | ❌ | ❌ | ✅ |
| Offline/Local | ✅ | ✅ | ✅ | ✅ |

---

## Feature Roadmap

### Phase 1: Foundation ✅ COMPLETE
- [x] Basic test harness with AI evaluation
- [x] Tool tracking and extraction
- [x] Fluent assertion API for tools, performance, responses
- [x] Framework-agnostic exception-based assertions
- [x] Test result models with comprehensive metadata
- [x] Core interfaces: IMetric, IRAGMetric, IAgenticMetric

### Phase 2: Streaming & Performance ✅ COMPLETE
- [x] Streaming support with real-time callbacks
- [x] Per-tool timing (StartTime, EndTime, Duration)
- [x] Time to First Token (TTFT) tracking
- [x] Token counting and usage tracking
- [x] Cost estimation with model pricing (8+ models)

### Phase 3: Benchmarking ✅ COMPLETE
- [x] Performance benchmarks (latency, throughput, cost)
- [x] Agentic benchmarks (tool accuracy, task completion)
- [x] Statistical analysis (mean, p50, p90, p99)
- [x] Multi-step reasoning benchmarks

### Phase 4: Advanced Metrics ✅ COMPLETE
- [x] RAG metrics (faithfulness, relevance, precision, recall, correctness)
- [x] Agentic metrics (tool selection, arguments, success, efficiency)
- [x] Task completion with AI evaluation
- [x] Custom metric interface for extensibility

### Phase 5: Ecosystem 🔄 IN PROGRESS
- [ ] NuGet package publication
- [ ] Visual Studio test integration
- [ ] GitHub Actions templates
- [ ] Dashboard/reporting UI

---

## Architecture Design

### Implemented Package Structure

```
AgentEval/                        # ✅ Implemented as single project
├── Core/                         # ✅ Core abstractions
│   ├── IMetric.cs                # IMetric, IRAGMetric, IAgenticMetric, EvaluationContext, MetricResult
│   ├── ITestableAgent.cs         # ITestableAgent, IStreamableAgent, AgentResponse, TokenUsage
│   ├── ITestHarness.cs           # ITestHarness, IStreamingTestHarness, TestOptions, StreamingOptions
│   └── IEvaluator.cs             # IEvaluator, ChatClientEvaluator, EvaluationResult
│
├── Models/                       # ✅ Data models
│   ├── ToolCallRecord.cs         # Tool call with timing, arguments, results
│   ├── ToolUsageReport.cs        # Collection of tool calls with aggregations
│   ├── PerformanceMetrics.cs     # Timing, tokens, cost estimation, ModelPricing
│   └── TestModels.cs             # TestCase, TestResult, TestSummary
│
├── Assertions/                   # ✅ Fluent assertions
│   ├── AssertionExceptions.cs    # Exception hierarchy
│   ├── ToolUsageAssertions.cs    # Tool assertions + ToolCallAssertion
│   ├── PerformanceAssertions.cs  # Performance/cost/timing assertions
│   └── ResponseAssertions.cs     # String content assertions
│
├── Metrics/                      # ✅ Evaluation metrics
│   ├── RAG/
│   │   └── RAGMetrics.cs         # Faithfulness, Relevance, Precision, Recall, Correctness
│   └── Agentic/
│       └── AgenticMetrics.cs     # ToolSelection, ToolArguments, ToolSuccess, TaskCompletion, Efficiency
│
├── Benchmarks/                   # ✅ Benchmarking
│   ├── PerformanceBenchmark.cs   # Latency, Throughput, Cost benchmarks
│   └── AgenticBenchmark.cs       # Tool accuracy, Task completion, Multi-step reasoning
│
└── MAF/                          # ✅ Microsoft Agent Framework integration
    ├── MAFAgentAdapter.cs        # Adapts AIAgent to IStreamableAgent
    └── MAFTestHarness.cs         # Full test harness with streaming support
```

### Core Interfaces (Implemented)

```csharp
// Core agent abstraction
public interface ITestableAgent
{
    string Name { get; }
    Task<AgentResponse> InvokeAsync(string prompt, CancellationToken ct = default);
}

public interface IStreamableAgent : ITestableAgent
{
    IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(string prompt, CancellationToken ct = default);
}

// Core test harness
public interface ITestHarness
{
    Task<TestResult> RunTestAsync(
        ITestableAgent agent,
        TestCase testCase,
        TestOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface IStreamingTestHarness : ITestHarness
{
    Task<TestResult> RunTestStreamingAsync(
        IStreamableAgent agent,
        TestCase testCase,
        StreamingOptions? streamingOptions = null,
        TestOptions? options = null,
        CancellationToken cancellationToken = default);
}

// Evaluation metrics
public interface IMetric
{
    string Name { get; }
    string Description { get; }
    Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken ct = default);
}

public interface IRAGMetric : IMetric
{
    bool RequiresContext { get; }
    bool RequiresGroundTruth { get; }
}

public interface IAgenticMetric : IMetric
{
    bool RequiresToolUsage { get; }
}
```

---

## Multi-Framework Support Strategy

### Initial Focus: Microsoft Agent Framework (MAF)

**Why focus on MAF?**
1. First-party Microsoft framework with long-term support
2. Growing adoption in enterprise .NET shops
3. Clean abstractions (`AIAgent`, `ChatClientAgent`)
4. Rich tool/function calling support

### Python Version Consideration

**Should we build a Python version?**

| Consideration | Assessment |
|--------------|------------|
| Market need | Low - Python has DeepEval, RAGAS, etc. |
| Development cost | High - separate codebase |
| Maintenance burden | Doubles ongoing effort |
| Strategic value | Low - better to focus on .NET gap |

**Recommendation:** Focus on .NET excellence. Python developers have options; .NET developers don't.

---

## Streaming Implementation

### Current Non-Streaming Flow

```csharp
// Current implementation
public async Task<TestResult> RunTestAsync(
    AIAgent agent,
    string prompt,
    bool trackTools = true)
{
    var response = await agent.RunAsync([new ChatMessage(ChatRole.User, prompt)]);
    var toolUsage = trackTools ? ExtractToolUsage(response) : null;
    // ... evaluation
}
```

### Streaming Implementation Design

```csharp
public class StreamingOptions
{
    public Action<string>? OnTextChunk { get; set; }
    public Action<ToolCallRecord>? OnToolStart { get; set; }
    public Action<ToolCallRecord>? OnToolComplete { get; set; }
    public Action<PerformanceMetrics>? OnMetricsUpdate { get; set; }
}

public async Task<TestResult> RunTestStreamingAsync(
    AIAgent agent,
    string prompt,
    StreamingOptions? streamingOptions = null,
    bool trackTools = true)
{
    var metrics = new PerformanceMetrics();
    var toolCalls = new List<ToolCallRecord>();
    var responseText = new StringBuilder();
    var isFirstToken = true;
    
    metrics.StartTime = DateTimeOffset.UtcNow;
    
    await foreach (var update in agent.RunStreamingAsync([new ChatMessage(ChatRole.User, prompt)]))
    {
        // Track first token latency
        if (isFirstToken)
        {
            metrics.TimeToFirstToken = DateTimeOffset.UtcNow - metrics.StartTime;
            isFirstToken = false;
        }
        
        // Extract content from update
        foreach (var message in update.Messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        responseText.Append(text.Text);
                        streamingOptions?.OnTextChunk?.Invoke(text.Text);
                        break;
                        
                    case FunctionCallContent call:
                        var record = new ToolCallRecord
                        {
                            Name = call.Name,
                            CallId = call.CallId,
                            Arguments = call.Arguments,
                            StartTime = DateTimeOffset.UtcNow,
                            Order = toolCalls.Count
                        };
                        toolCalls.Add(record);
                        streamingOptions?.OnToolStart?.Invoke(record);
                        break;
                        
                    case FunctionResultContent result:
                        var existing = toolCalls.FirstOrDefault(t => t.CallId == result.CallId);
                        if (existing != null)
                        {
                            existing.EndTime = DateTimeOffset.UtcNow;
                            existing.Result = result.Result?.ToString();
                            existing.Exception = result.Exception?.Message;
                            streamingOptions?.OnToolComplete?.Invoke(existing);
                        }
                        break;
                }
            }
        }
    }
    
    metrics.EndTime = DateTimeOffset.UtcNow;
    metrics.TotalDuration = metrics.EndTime - metrics.StartTime;
    
    // ... evaluation continues
}
```

### Enhanced ToolCallRecord with Timing

```csharp
public class ToolCallRecord
{
    public required string Name { get; set; }
    public string? CallId { get; set; }
    public IReadOnlyDictionary<string, object?>? Arguments { get; set; }
    public string? Result { get; set; }
    public string? Exception { get; set; }
    public int Order { get; set; }
    
    // Timing (populated in streaming mode)
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public TimeSpan? Duration => EndTime - StartTime;
    
    // Helpers
    public bool HasTiming => StartTime.HasValue && EndTime.HasValue;
    public bool HasError => Exception != null;
}
```

---

## Performance Metrics

### PerformanceMetrics Class

```csharp
public class PerformanceMetrics
{
    // Timing
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public TimeSpan TotalDuration => EndTime - StartTime;
    public TimeSpan? TimeToFirstToken { get; set; }
    
    // Token Usage
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens => PromptTokens + CompletionTokens;
    
    // Tool Performance
    public int ToolCallCount { get; set; }
    public TimeSpan TotalToolTime { get; set; }
    public TimeSpan AverageToolTime => ToolCallCount > 0 
        ? TotalToolTime / ToolCallCount 
        : TimeSpan.Zero;
    
    // Cost Estimation
    public decimal? EstimatedCost { get; set; }
    public string? ModelUsed { get; set; }
}
```

### Cost Estimation (ModelPricing)

> **Note:** The actual implementation uses `ModelPricing` (not `CostEstimator`) with 
> `ConcurrentDictionary` for thread-safety. See [PerformanceMetrics.cs](Models/PerformanceMetrics.cs).

```csharp
public static class ModelPricing
{
    private static readonly ConcurrentDictionary<string, (decimal InputPer1K, decimal OutputPer1K)> _pricing = 
        new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI models
        ["gpt-4o"] = (0.005m, 0.015m),
        ["gpt-4o-mini"] = (0.00015m, 0.0006m),
        ["gpt-4-turbo"] = (0.01m, 0.03m),
        ["gpt-3.5-turbo"] = (0.0005m, 0.0015m),
        ["o1"] = (0.015m, 0.06m),
        ["o3-mini"] = (0.00165m, 0.0066m),
        
        // Anthropic models
        ["claude-3-5-sonnet"] = (0.003m, 0.015m),
        ["claude-3-opus"] = (0.015m, 0.075m),
        ["claude-3-haiku"] = (0.00025m, 0.00125m),
    };
    
    public static decimal? EstimateCost(string? modelName, int inputTokens, int outputTokens)
    {
        if (string.IsNullOrEmpty(modelName)) return null;
        var pricing = GetPricing(modelName);
        if (pricing == null) return null;
        return (inputTokens / 1000m * pricing.Value.InputPer1K) + 
               (outputTokens / 1000m * pricing.Value.OutputPer1K);
    }
    
    public static void SetPricing(string modelName, decimal inputPer1K, decimal outputPer1K)
    {
        _pricing.AddOrUpdate(modelName.ToLowerInvariant(), 
            (inputPer1K, outputPer1K), (_, _) => (inputPer1K, outputPer1K));
    }
}
```

### Performance Assertions

```csharp
public class PerformanceAssertions
{
    private readonly PerformanceMetrics _metrics;
    
    public PerformanceAssertions(PerformanceMetrics metrics) => _metrics = metrics;
    
    public PerformanceAssertions HaveTotalDurationUnder(TimeSpan max)
    {
        if (_metrics.TotalDuration > max)
            throw new PerformanceAssertionException(
                $"Expected duration under {max}, but was {_metrics.TotalDuration}");
        return this;
    }
    
    public PerformanceAssertions HaveTimeToFirstTokenUnder(TimeSpan max)
    {
        if (_metrics.TimeToFirstToken > max)
            throw new PerformanceAssertionException(
                $"Expected TTFT under {max}, but was {_metrics.TimeToFirstToken}");
        return this;
    }
    
    public PerformanceAssertions HaveTokenCountUnder(int max)
    {
        if (_metrics.TotalTokens > max)
            throw new PerformanceAssertionException(
                $"Expected tokens under {max}, but was {_metrics.TotalTokens}");
        return this;
    }
    
    public PerformanceAssertions HaveEstimatedCostUnder(decimal maxUsd)
    {
        if (_metrics.EstimatedCost > maxUsd)
            throw new PerformanceAssertionException(
                $"Expected cost under ${maxUsd}, but was ${_metrics.EstimatedCost}");
        return this;
    }
}

// Usage example
result.Performance
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(2))
    .HaveEstimatedCostUnder(0.05m);
```

---

## Benchmarking

### Performance Benchmark

```csharp
public class PerformanceBenchmark
{
    public async Task<BenchmarkResult> RunAsync(
        ITestableAgent agent,
        string prompt,
        int iterations = 10,
        int warmupIterations = 2)
    {
        var results = new List<PerformanceMetrics>();
        
        // Warmup
        for (int i = 0; i < warmupIterations; i++)
        {
            await agent.InvokeAsync(prompt);
        }
        
        // Benchmark runs
        for (int i = 0; i < iterations; i++)
        {
            var harness = new TestHarness();
            var result = await harness.RunTestStreamingAsync(agent, prompt);
            results.Add(result.Performance);
        }
        
        return new BenchmarkResult
        {
            Iterations = iterations,
            
            // Latency stats
            MeanLatency = TimeSpan.FromMilliseconds(results.Average(r => r.TotalDuration.TotalMilliseconds)),
            P50Latency = Percentile(results, 50, r => r.TotalDuration),
            P95Latency = Percentile(results, 95, r => r.TotalDuration),
            P99Latency = Percentile(results, 99, r => r.TotalDuration),
            
            // TTFT stats
            MeanTTFT = TimeSpan.FromMilliseconds(results.Average(r => r.TimeToFirstToken?.TotalMilliseconds ?? 0)),
            
            // Token stats
            MeanTokens = results.Average(r => r.TotalTokens ?? 0),
            
            // Cost stats
            TotalCost = results.Sum(r => r.EstimatedCost ?? 0),
            MeanCostPerRequest = results.Average(r => r.EstimatedCost ?? 0),
        };
    }
}
```

### Agentic Benchmark

```csharp
public class AgenticBenchmark
{
    public async Task<AgenticBenchmarkResult> RunAsync(
        ITestableAgent agent,
        IEnumerable<AgenticTestCase> testCases)
    {
        var results = new List<AgenticTestResult>();
        
        foreach (var testCase in testCases)
        {
            var harness = new TestHarness();
            var result = await harness.RunTestAsync(
                agent, 
                testCase.Prompt,
                trackTools: true);
            
            // Evaluate agentic metrics
            var metrics = new AgenticMetrics
            {
                TaskCompleted = EvaluateTaskCompletion(result, testCase.ExpectedOutcome),
                ToolAccuracy = EvaluateToolAccuracy(result.ToolUsage, testCase.ExpectedTools),
                StepEfficiency = EvaluateStepEfficiency(result.ToolUsage, testCase.OptimalSteps),
                ResponseQuality = result.EvaluationScore,
            };
            
            results.Add(new AgenticTestResult
            {
                TestCase = testCase,
                TestResult = result,
                Metrics = metrics,
            });
        }
        
        return new AgenticBenchmarkResult
        {
            TotalTests = results.Count,
            PassedTests = results.Count(r => r.Metrics.TaskCompleted),
            TaskCompletionRate = results.Average(r => r.Metrics.TaskCompleted ? 1.0 : 0.0),
            MeanToolAccuracy = results.Average(r => r.Metrics.ToolAccuracy),
            MeanStepEfficiency = results.Average(r => r.Metrics.StepEfficiency),
            MeanResponseQuality = results.Average(r => r.Metrics.ResponseQuality),
        };
    }
    
    private double EvaluateToolAccuracy(ToolUsageReport? usage, string[]? expectedTools)
    {
        if (usage == null || expectedTools == null) return 1.0;
        
        var actualTools = usage.UniqueToolNames.ToHashSet();
        var expected = expectedTools.ToHashSet();
        
        var correctCalls = actualTools.Intersect(expected).Count();
        var incorrectCalls = actualTools.Except(expected).Count();
        var missedCalls = expected.Except(actualTools).Count();
        
        return (double)correctCalls / (correctCalls + incorrectCalls + missedCalls);
    }
}
```

### Benchmark Assertions

```csharp
// Usage in tests
[Fact]
public async Task Agent_MeetsPerformanceSLA()
{
    var benchmark = new PerformanceBenchmark();
    var result = await benchmark.RunAsync(agent, "Plan a feature", iterations: 10);
    
    result.Should()
        .HaveP95LatencyUnder(TimeSpan.FromSeconds(5))
        .HaveMeanTTFTUnder(TimeSpan.FromSeconds(1))
        .HaveMeanCostUnder(0.10m);
}

[Fact]
public async Task Agent_MeetsAgenticQuality()
{
    var benchmark = new AgenticBenchmark();
    var result = await benchmark.RunAsync(agent, testCases);
    
    result.Should()
        .HaveTaskCompletionRateAbove(0.95)
        .HaveMeanToolAccuracyAbove(0.90)
        .HaveMeanResponseQualityAbove(0.85);
}
```

---

## RAG Metrics

### Porting from RAGAS

RAGAS provides academically rigorous RAG metrics. We should port the key ones:

```csharp
public interface IRAGMetric : IMetric
{
    bool RequiresContext { get; }
    bool RequiresGroundTruth { get; }
}

public class FaithfulnessMetric : IRAGMetric
{
    public string Name => "Faithfulness";
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => false;
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context)
    {
        // Uses LLM to check if answer is grounded in context
        // Extracts claims from answer, verifies each against context
        var prompt = $"""
            Given the context and answer, identify all claims in the answer 
            and verify if each claim is supported by the context.
            
            Context: {context.RetrievedContext}
            Answer: {context.Response}
            
            Return a JSON with:
            - claims: list of claims extracted from answer
            - supported: list of booleans for each claim
            - faithfulness_score: ratio of supported claims
            """;
            
        // ... LLM call and parsing
        return new MetricResult(Name, faithfulnessScore);
    }
}

public class AnswerRelevancyMetric : IRAGMetric
{
    public string Name => "AnswerRelevancy";
    public bool RequiresContext => false;
    public bool RequiresGroundTruth => false;
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context)
    {
        // Generates questions that the answer could respond to
        // Measures semantic similarity to original question
        // ...
    }
}

public class ContextPrecisionMetric : IRAGMetric
{
    public string Name => "ContextPrecision";
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => true;
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context)
    {
        // Evaluates if retrieved context is relevant to question
        // Uses ranking-aware precision (higher ranked = more important)
        // ...
    }
}
```

### .NET AI Extensions for RAG

Microsoft's `Microsoft.Extensions.AI` provides some building blocks but **no built-in RAG metrics**. We would need to implement them ourselves, potentially inspired by:

1. **RAGAS** - Academic rigor, well-documented formulas
2. **DeepEval** - Practical implementations
3. **Azure AI Evaluation** - Enterprise patterns

```csharp
// Potential integration with Microsoft.Extensions.AI
public class RAGEvaluator
{
    private readonly IChatClient _evaluatorClient;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    
    public async Task<RAGMetricsResult> EvaluateAsync(
        string question,
        string answer,
        IEnumerable<string> retrievedContexts,
        string? groundTruth = null)
    {
        var tasks = new List<Task<MetricResult>>();
        
        var context = new EvaluationContext
        {
            Question = question,
            Response = answer,
            RetrievedContext = string.Join("\n---\n", retrievedContexts),
            GroundTruth = groundTruth,
        };
        
        // Run metrics in parallel
        tasks.Add(new FaithfulnessMetric(_evaluatorClient).EvaluateAsync(context));
        tasks.Add(new AnswerRelevancyMetric(_evaluatorClient, _embeddings).EvaluateAsync(context));
        
        if (groundTruth != null)
        {
            tasks.Add(new ContextPrecisionMetric(_evaluatorClient).EvaluateAsync(context));
            tasks.Add(new ContextRecallMetric(_evaluatorClient).EvaluateAsync(context));
        }
        
        var results = await Task.WhenAll(tasks);
        
        return new RAGMetricsResult(results);
    }
}
```

---

## Implementation Status Summary

### What's Been Implemented

Based on complexity, value, and dependencies:

| Priority | Feature | Status | Files |
|----------|---------|--------|-------|
| 1 | **Streaming Support** | ✅ Complete | MAFTestHarness, IStreamableAgent |
| 2 | **Performance Metrics** | ✅ Complete | PerformanceMetrics, ModelPricing |
| 3 | **Performance Assertions** | ✅ Complete | PerformanceAssertions |
| 4 | **Cost Estimation** | ✅ Complete | ModelPricing (8+ models) |
| 5 | **Performance Benchmarking** | ✅ Complete | PerformanceBenchmark |
| 6 | **Agentic Benchmarking** | ✅ Complete | AgenticBenchmark |
| 7 | **RAG Metrics** | ✅ Complete | 5 metrics in RAGMetrics.cs |
| 8 | **Agentic Metrics** | ✅ Complete | 5 metrics in AgenticMetrics.cs |

### Test Coverage

- **210 unit tests** covering all core functionality
- Tests for: ToolCallRecord, ToolUsageReport, ToolUsageAssertions, ToolCallAssertion, PerformanceMetrics, PerformanceAssertions, ModelPricing, FailureReport, ToolCallTimeline, FaithfulnessMetric, ToolSelectionMetric, ToolSuccessMetric
- Testing infrastructure: FakeChatClient for mocking IChatClient without external dependencies
- All tests passing ✅

### Targets

- **net8.0** ✅
- **net9.0** ✅
- **net10.0** ✅

---

## API Reference (Implemented)

### Core API

```csharp
// Create a test harness with AI-powered evaluation
var harness = new MAFTestHarness(evaluatorClient, verbose: true);

// Or without AI evaluation (simpler tests)
var harness = new MAFTestHarness(verbose: true);

// Define test case
var testCase = new TestCase
{
    Name = "Feature Planning Test",
    Input = "Plan a user authentication feature",
    EvaluationCriteria = new[] { "Should include security considerations" },
    ExpectedTools = new[] { "FeatureTool", "SecurityTool" },
    PassingScore = 70
};

// Run test with MAF agent
var adapter = new MAFAgentAdapter(myAgent);
var result = await harness.RunTestAsync(adapter, testCase, new TestOptions
{
    TrackTools = true,
    TrackPerformance = true,
    EvaluateResponse = true
});

// Streaming test with callbacks
var result = await harness.RunTestStreamingAsync(adapter, testCase, new StreamingOptions
{
    OnTextChunk = chunk => Console.Write(chunk),
    OnToolStart = tool => Console.WriteLine($"🔧 Starting: {tool.Name}"),
    OnToolComplete = tool => Console.WriteLine($"✓ {tool.Name} ({tool.Duration?.TotalMilliseconds}ms)"),
    OnFirstToken = ttft => Console.WriteLine($"⚡ TTFT: {ttft.TotalMilliseconds}ms"),
    OnMetricsUpdate = metrics => Console.WriteLine($"📊 {metrics}")
});
```

### Assertions API (Implemented)

```csharp
// Tool assertions - fluent and chainable
result.ToolUsage!
    .Should()
    .HaveCalledTool("FeatureTool")
        .BeforeTool("SecurityTool")
        .WithArgument("type", "OAuth2")
        .WithResultContaining("success")
        .WithoutError()
        .WithDurationUnder(TimeSpan.FromSeconds(5))
    .And()
    .HaveCallCount(3)
    .HaveCallOrder("FeatureTool", "ValidationTool", "SecurityTool")
    .HaveNoErrors();

// Performance assertions
result.Performance!
    .Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
    .HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(2))
    .HaveTokenCountUnder(5000)
    .HaveEstimatedCostUnder(0.10m)
    .HaveAverageToolTimeUnder(TimeSpan.FromSeconds(1));

// Response assertions
result.ActualOutput!
    .Should()
    .ContainAll("security", "authentication", "authorization")
    .NotContain("error")
    .HaveLengthBetween(100, 2000)
    .MatchPattern(@"^## \w+");
```

### Benchmark API (Implemented)

```csharp
// Performance benchmark - latency, throughput, cost
var perfBenchmark = new PerformanceBenchmark(agent, new PerformanceBenchmarkOptions { Verbose = true });

var latencyResult = await perfBenchmark.RunLatencyBenchmarkAsync(
    prompt: "Complex planning task",
    iterations: 20,
    warmupIterations: 3);

Console.WriteLine($"Mean: {latencyResult.MeanLatency.TotalMilliseconds:F0}ms");
Console.WriteLine($"P50: {latencyResult.P50Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"P90: {latencyResult.P90Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"P99: {latencyResult.P99Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"Mean TTFT: {latencyResult.MeanTimeToFirstToken?.TotalMilliseconds:F0}ms");

var throughputResult = await perfBenchmark.RunThroughputBenchmarkAsync(
    prompt: "Quick task",
    concurrentRequests: 5,
    duration: TimeSpan.FromSeconds(30));

Console.WriteLine($"RPS: {throughputResult.RequestsPerSecond:F2}");

var costResult = await perfBenchmark.RunCostBenchmarkAsync(
    prompts: testPrompts,
    modelName: "gpt-4o");

Console.WriteLine($"Estimated Cost: ${costResult.EstimatedCostUSD:F6}");

// Agentic benchmark - tool accuracy, task completion, multi-step reasoning
var agentBenchmark = new AgenticBenchmark(agent, evaluator);

var toolAccuracy = await agentBenchmark.RunToolAccuracyBenchmarkAsync(new[]
{
    new ToolAccuracyTestCase
    {
        Name = "Feature Creation",
        Prompt = "Create a new user feature",
        ExpectedTools = new[] 
        { 
            new ExpectedTool { Name = "FeatureTool", RequiredParameters = new[] { "name", "type" } }
        }
    }
});

Console.WriteLine($"Tool Accuracy: {toolAccuracy.OverallAccuracy:P0}");

var multiStep = await agentBenchmark.RunMultiStepReasoningBenchmarkAsync(testCases);
Console.WriteLine($"Step Completion: {multiStep.AverageStepCompletion:P0}");
```

### RAG Metrics API (Implemented)

```csharp
// Create metrics with evaluator client
var faithfulness = new FaithfulnessMetric(chatClient);
var relevance = new RelevanceMetric(chatClient);
var contextPrecision = new ContextPrecisionMetric(chatClient);
var contextRecall = new ContextRecallMetric(chatClient);
var answerCorrectness = new AnswerCorrectnessMetric(chatClient);

// Evaluate
var context = new EvaluationContext
{
    Input = "What is the capital of France?",
    Output = "Paris is the capital of France.",
    Context = "France is a country in Europe. Paris is the capital city of France.",
    GroundTruth = "The capital of France is Paris."
};

var result = await faithfulness.EvaluateAsync(context);
Console.WriteLine($"Faithfulness: {result.Score}/100 - {result.Message}");
```

### Agentic Metrics API (Implemented)

```csharp
// Tool selection accuracy
var toolSelection = new ToolSelectionMetric(
    expectedTools: new[] { "FeatureTool", "SecurityTool" },
    strictOrder: true);

var context = new EvaluationContext
{
    Input = "Create a secure feature",
    Output = "Feature created with security.",
    ToolUsage = result.ToolUsage
};

var metricResult = await toolSelection.EvaluateAsync(context);

// Tool arguments validation
var toolArgs = new ToolArgumentsMetric(new Dictionary<string, IEnumerable<string>>
{
    ["FeatureTool"] = new[] { "name", "type" },
    ["SecurityTool"] = new[] { "level" }
});

// Tool efficiency
var efficiency = new ToolEfficiencyMetric(
    maxExpectedCalls: 5,
    maxExpectedDuration: TimeSpan.FromSeconds(10));

// Task completion with AI evaluation
var taskCompletion = new TaskCompletionMetric(chatClient, new[]
{
    "The feature was successfully created",
    "Security measures were applied"
});
```

---

## Trace-First Failure Reporting

When tests fail, AgentEval provides structured failure reports that prioritize actionable information:

### FailureReport Model

```csharp
public record FailureReport
{
    public required string FailureType { get; init; }     // "Evaluation", "Exception", etc.
    public required string Summary { get; init; }          // Brief failure description
    public required List<string> FailedCriteria { get; init; }
    public List<string>? Recommendations { get; init; }
    public string? ExceptionType { get; init; }
    public string? ExceptionMessage { get; init; }
    public string? StackTrace { get; init; }
}
```

### ToolCallTimeline Model

```csharp
public record ToolCallTimeline
{
    public required List<ToolCallTimelineEntry> Entries { get; init; }
    public TimeSpan TotalDuration => Entries.Count > 0 
        ? Entries.Max(e => e.EndTime) - Entries.Min(e => e.StartTime)
        : TimeSpan.Zero;
    public int TotalToolCalls => Entries.Count;
    public int FailedCalls => Entries.Count(e => e.HasError);
}
```

### IAgentEvalLogger Interface

```csharp
public interface IAgentEvalLogger
{
    void LogInformation(string message, params object[] args);
    void LogWarning(string message, params object[] args);
    void LogError(string message, params object[] args);
    void LogError(Exception exception, string message, params object[] args);
    void LogDebug(string message, params object[] args);
    void Log(LogLevel level, string message, params object[] args);
    void LogTestStart(string testName, string? description = null);
    void LogTestComplete(string testName, bool passed, TimeSpan duration);
    void LogToolCall(string toolName, TimeSpan duration, bool success, string? error = null);
    void LogFailure(FailureReport failure);  // Trace-first output
}
```

### Usage in MAFTestHarness

```csharp
// Failure reports are automatically created and logged
var result = await harness.RunTestAsync(adapter, testCase);

if (!result.Passed)
{
    // Failure report is attached to the result
    var failure = result.Failure;
    
    // Timeline provides detailed tool execution trace
    var timeline = result.Timeline;
    
    // Log output shows trace-first failure information
    // [FAILURE] Evaluation failure: Scored 65 < required 70
    // [FAILURE] Failed criteria: Should include security considerations
    // [FAILURE] Tool Timeline: 2 calls, 0 failed
    //   - FeatureTool: 234.5ms ✓
    //   - DocumentTool: 156.2ms ✓
    // [FAILURE] Recommendations: Ensure the agent addresses security in responses
}
```

---

## Testing Infrastructure

### FakeChatClient

A zero-dependency test double for `IChatClient` that enables testing LLM-based metrics without mocking libraries:

```csharp
public class FakeChatClient : IChatClient
{
    private readonly Queue<string> _responses = new();
    public List<IEnumerable<ChatMessage>> ReceivedMessages { get; } = new();
    public string? LastPrompt { get; }
    public bool ThrowOnNextCall { get; set; }
    
    // Constructor with initial responses
    public FakeChatClient(params string[] responses) { ... }
    
    // Fluent API for adding responses
    public FakeChatClient WithResponse(string response) => ...;
    
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (ThrowOnNextCall)
        {
            ThrowOnNextCall = false;
            throw new InvalidOperationException("Simulated API failure");
        }
        
        ReceivedMessages.Add(chatMessages.ToList());
        var responseText = _responses.Count > 0 ? _responses.Dequeue() : "{}";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }
}
}
```

### Usage in Tests

```csharp
[Fact]
public async Task FaithfulnessMetric_HighScore_WhenFullyGrounded()
{
    // Arrange
    var fakeClient = new FakeChatClient();
    fakeClient.EnqueueResponse("""{"score": 95, "explanation": "Well grounded"}""");
    
    var metric = new FaithfulnessMetric(fakeClient);
    var context = new EvaluationContext
    {
        Input = "What is the capital?",
        Output = "Paris is the capital.",
        Context = "Paris is the capital of France."
    };
    
    // Act
    var result = await metric.EvaluateAsync(context);
    
    // Assert
    Assert.True(result.Passed);
    Assert.Equal(95, result.Score);
    Assert.Contains("context", fakeClient.LastPrompt);  // Verify prompt
}
```

---

## NuGet Package Structure (Ready for Publication)

```xml
<!-- AgentEval.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>
    <PackageId>AgentEval</PackageId>
    <Version>1.0.0-alpha</Version>
    <Authors>AgentEval Contributors</Authors>
    <Description>The first .NET-native AI agent testing framework</Description>
    <PackageTags>ai;agent;testing;evaluation;maf;llm;openai;azure</PackageTags>
    <LangVersion>14</LangVersion>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="Microsoft.Agents.AI" Version="1.0.0-preview.251110.2" />
    <PackageReference Include="Microsoft.Extensions.AI" Version="10.0.0" />
    <PackageReference Include="Azure.AI.OpenAI" Version="2.5.0-beta.1" />
  </ItemGroup>
</Project>
```

---

## Success Metrics

How we'll know AgentEval is successful:

| Metric | Target | Current |
|--------|--------|---------|
| Core Features | Complete | ✅ 100% |
| Test Coverage | 80%+ | ✅ 210 tests |
| Framework Adapters | MAF | ✅ MAF Complete |
| Target Frameworks | 3+ | ✅ net8.0, net9.0, net10.0 |
| GitHub Stars | 500+ first year | 🔄 Not published yet |
| NuGet Downloads | 10,000+ monthly | 🔄 Not published yet |

---

## Next Steps

See **[AgentEval-plan-forward.md](AgentEval-plan-forward.md)** for detailed roadmap.

### Immediate Priorities

1. ✅ Document design (this file)
2. ✅ Implement streaming support
3. ✅ Add PerformanceMetrics
4. ✅ Implement performance assertions
5. ✅ Add benchmarking infrastructure
6. ✅ Port RAG metrics
7. ✅ Implement agentic metrics
8. 🔄 Extract to standalone repository
9. 🔄 Publish NuGet package

---

*Last Updated: January 2026*  
*Status: ✅ Core Implementation Complete*
