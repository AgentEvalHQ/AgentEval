# Benchmarks Guide

> **Running industry-standard AI benchmarks and creating custom benchmark suites with AgentEval**

---

## What You Can Do

AgentEval provides comprehensive benchmarking capabilities for AI agents:

| Feature | Description |
|---------|-------------|
| **Custom benchmark suites** | Create your own domain-specific benchmarks |
| **Performance benchmarks** | Measure latency, throughput, and cost |
| **Workflow performance benchmarks** | Multi-agent pipeline performance and scaling analysis |
| **BFCL-style evaluations** | Tool calling accuracy with industry-standard patterns |
| **GAIA-style evaluations** | Task completion with multi-step reasoning |
| **Regression detection** | Track scores over time, fail CI on regressions |

---

## Quick Start

### ✅ Create Custom Benchmarks
Write your own benchmark suites for your domain.

### ✅ Run Performance Benchmarks
Measure latency, throughput, and cost across your agents.

### ✅ Workflow Performance Analysis
Evaluate multi-agent pipeline performance, bottlenecks, and scaling characteristics.

### ✅ Industry-Standard Patterns
Create evaluations following BFCL/GAIA patterns and compare against published leaderboards.

---

## Prerequisites

All benchmark examples assume you have created a MAF agent:

```csharp
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

var azureClient = new AzureOpenAIClient(
    new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
    new Azure.AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!));

var chatClient = azureClient
    .GetChatClient("gpt-4o")
    .AsIChatClient();

var agent = new ChatClientAgent(
    chatClient,
    new ChatClientAgentOptions
    {
        Name = "BenchmarkAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a helpful assistant with access to various tools.",
            Tools = [/* Your tools here */]
        }
    });

// For AI-powered evaluation, create an evaluator client
var evaluator = azureClient.GetChatClient("gpt-4o").AsIChatClient();
```

---

## Berkeley Function Calling Leaderboard (BFCL)

BFCL is the industry standard for evaluating function/tool calling accuracy. AgentEval supports creating BFCL-style evaluation cases:

```csharp
var bfclTests = new List<ToolAccuracyTestCase>
{
    // Simple function call (BFCL simple_python category)
    new ToolAccuracyTestCase
    {
        Name = "simple_math",
        Prompt = "Calculate 15 factorial",
        ExpectedTools = new[]
        {
            new ExpectedTool { Name = "factorial", RequiredParameters = new[] { "n" } }
        }
    },
    
    // Parallel calls (BFCL parallel category)
    new ToolAccuracyTestCase
    {
        Name = "parallel_weather",
        Prompt = "What's the weather in NYC and LA?",
        ExpectedTools = new[]
        {
            new ExpectedTool { Name = "get_weather", RequiredParameters = new[] { "location" } },
            new ExpectedTool { Name = "get_weather", RequiredParameters = new[] { "location" } }
        }
    },
    
    // Sequential calls (BFCL multiple category)
    new ToolAccuracyTestCase
    {
        Name = "sequential_booking",
        Prompt = "Find flights to Paris and book the cheapest one",
        ExpectedTools = new[]
        {
            new ExpectedTool { Name = "search_flights", RequiredParameters = new[] { "destination" } },
            new ExpectedTool { Name = "book_flight", RequiredParameters = new[] { "flight_id" } }
        }
    }
};

// Run and measure
var benchmark = new AgenticBenchmark(agent);
var results = await benchmark.RunToolAccuracyBenchmarkAsync(bfclTests);

Console.WriteLine($"BFCL-Style Results");
Console.WriteLine($"  Accuracy: {results.OverallAccuracy:P1}");
Console.WriteLine($"  Passed: {results.PassedTests}/{results.TotalTests}");
```

### Compare Against Published Leaderboard

BFCL publishes scores for major models. Run your evaluations and compare:

| Model | Simple | Parallel | Multiple | Multi-turn | Overall |
|-------|--------|----------|----------|------------|---------|
| GPT-4o | 94.5% | 91.2% | 88.5% | 82.1% | 89.1% |
| Claude-3.5 | 92.1% | 88.7% | 85.2% | 79.8% | 86.5% |
| Gemini-1.5 | 91.8% | 87.5% | 84.1% | 78.2% | 85.4% |
| **Your Agent** | ?% | ?% | ?% | ?% | ?% |

---

## GAIA (General AI Assistants)

GAIA tests multi-step reasoning and real-world task completion:

```csharp
var gaiaCases = new List<TaskCompletionTestCase>
{
    // Level 1: Simple
    new TaskCompletionTestCase
    {
        Name = "gaia_level1_001",
        Prompt = "What is the population of Tokyo?",
        CompletionCriteria = new[]
        {
            "Provides a specific number",
            "Number is approximately correct (within 5%)",
            "Cites a source or explains where the data comes from"
        },
        PassingScore = 70
    },
    
    // Level 2: Moderate
    new TaskCompletionTestCase
    {
        Name = "gaia_level2_015",
        Prompt = "Find the contact email for customer support at Microsoft",
        CompletionCriteria = new[]
        {
            "Provides an email address",
            "Email appears to be a valid Microsoft domain",
            "Explains how to reach support"
        },
        PassingScore = 70
    },
    
    // Level 3: Complex
    new TaskCompletionTestCase
    {
        Name = "gaia_level3_042",
        Prompt = "Research the top 3 electric vehicle manufacturers by market cap and compare their 2024 revenue",
        CompletionCriteria = new[]
        {
            "Identifies 3 companies correctly",
            "Provides market cap figures",
            "Provides revenue figures",
            "Makes a coherent comparison"
        },
        PassingScore = 80
    }
};

var benchmark = new AgenticBenchmark(agent, evaluator);
var results = await benchmark.RunTaskCompletionBenchmarkAsync(gaiaCases);

Console.WriteLine($"GAIA-Style Results");
Console.WriteLine($"  Average Score: {results.AverageScore:F1}/100");
Console.WriteLine($"  Pass Rate: {(double)results.PassedTests / results.TotalTests:P1}");
```

---

## Performance Benchmarks

Measure latency, throughput, and cost across your agents.

### Latency Benchmark

```csharp
var benchmark = new PerformanceBenchmark(agent);

var latencyResults = await benchmark.RunLatencyBenchmarkAsync(
    testCases,
    iterations: 10,
    warmupIterations: 2);

Console.WriteLine($"Latency Benchmark");
Console.WriteLine($"  P50: {latencyResults.P50Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"  P90: {latencyResults.P90Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"  P99: {latencyResults.P99Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"  Avg: {latencyResults.AverageLatency.TotalMilliseconds:F0}ms");
```

### Throughput Benchmark

```csharp
var throughputResults = await benchmark.RunThroughputBenchmarkAsync(
    testCases,
    durationSeconds: 60,
    maxConcurrency: 10);

Console.WriteLine($"Throughput Benchmark");
Console.WriteLine($"  Requests/sec: {throughputResults.RequestsPerSecond:F1}");
Console.WriteLine($"  Success Rate: {throughputResults.SuccessRate:P1}");
```

### Cost Benchmark

```csharp
var costResults = await benchmark.RunCostBenchmarkAsync(testCases);

Console.WriteLine($"Cost Benchmark");
Console.WriteLine($"  Total Cost: ${costResults.TotalCost:F4}");
Console.WriteLine($"  Avg per Request: ${costResults.AverageCostPerRequest:F6}");
Console.WriteLine($"  Total Tokens: {costResults.TotalTokens}");
```

---

## Workflow Performance Benchmarks

Multi-agent workflows introduce additional performance considerations beyond single-agent benchmarks: orchestration overhead, inter-agent communication, and pipeline bottlenecks.

### Workflow Latency Benchmark

Measure end-to-end workflow execution time and per-executor breakdown:

```csharp
using AgentEval.MAF;
using AgentEval.Benchmarks;

// Create workflow (using MAF WorkflowBuilder)
var workflow = new WorkflowBuilder()
    .BindAsExecutor("Planner", plannerAgent, emitEvents: true)
    .BindAsExecutor("Researcher", researcherAgent, emitEvents: true)
    .BindAsExecutor("Writer", writerAgent, emitEvents: true)
    .BindAsExecutor("Editor", editorAgent, emitEvents: true)
    .Build();

var workflowAdapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "ContentPipeline");

// Run workflow latency benchmark
var workflowBenchmark = new WorkflowPerformanceBenchmark(workflowAdapter);
var latencyResults = await workflowBenchmark.RunLatencyBenchmarkAsync(
    workflowTestCases,
    iterations: 10,
    warmupIterations: 2);

Console.WriteLine($"Workflow Latency Benchmark");
Console.WriteLine($"  End-to-End P50: {latencyResults.OverallP50.TotalSeconds:F1}s");
Console.WriteLine($"  End-to-End P90: {latencyResults.OverallP90.TotalSeconds:F1}s");
Console.WriteLine($"  Pipeline Overhead: {latencyResults.OrchestrationOverhead.TotalMilliseconds:F0}ms");

// Per-executor breakdown
foreach (var (executorId, timing) in latencyResults.PerExecutorTimings)
{
    Console.WriteLine($"  {executorId}: {timing.AverageLatency.TotalMilliseconds:F0}ms avg");
}
```

### Workflow Throughput Benchmark

Measure workflow pipeline throughput with concurrent execution:

```csharp
var throughputResults = await workflowBenchmark.RunThroughputBenchmarkAsync(
    workflowTestCases,
    durationMinutes: 5,
    maxConcurrentWorkflows: 3);  // Limited by workflow complexity

Console.WriteLine($"Workflow Throughput Benchmark");
Console.WriteLine($"  Workflows/hour: {throughputResults.WorkflowsPerHour:F1}");
Console.WriteLine($"  Avg Workflow Duration: {throughputResults.AverageWorkflowDuration.TotalMinutes:F1}min");
Console.WriteLine($"  Success Rate: {throughputResults.SuccessRate:P1}");
Console.WriteLine($"  Bottleneck Executor: {throughputResults.BottleneckExecutor}");
```

### Workflow Cost Benchmark

Analyze cost distribution across multiple agents and identify expensive executors:

```csharp
var costResults = await workflowBenchmark.RunCostBenchmarkAsync(workflowTestCases);

Console.WriteLine($"Workflow Cost Benchmark");
Console.WriteLine($"  Total Cost per Workflow: ${costResults.AverageCostPerWorkflow:F4}");
Console.WriteLine($"  Most Expensive: {costResults.MostExpensiveExecutor} (${costResults.HighestExecutorCost:F4})");
Console.WriteLine($"  Cost Efficiency: {costResults.TokensPerDollar:F0} tokens/$");

// Per-executor cost breakdown
foreach (var (executorId, cost) in costResults.PerExecutorCosts)
{
    var percentage = (cost / costResults.AverageCostPerWorkflow) * 100;
    Console.WriteLine($"  {executorId}: ${cost:F4} ({percentage:F1}% of total)");
}
```

### Workflow Scaling Benchmark

Test how workflow performance scales with different configurations:

```csharp
// Test different workflow configurations
var configurations = new[]
{
    new WorkflowConfiguration { MaxConcurrency = 1, TimeoutMinutes = 10 },
    new WorkflowConfiguration { MaxConcurrency = 2, TimeoutMinutes = 8 },
    new WorkflowConfiguration { MaxConcurrency = 3, TimeoutMinutes = 6 }
};

foreach (var config in configurations)
{
    var results = await workflowBenchmark.RunScalingBenchmarkAsync(
        workflowTestCases, 
        config);
    
    Console.WriteLine($"Concurrency {config.MaxConcurrency}:");
    Console.WriteLine($"  Success Rate: {results.SuccessRate:P1}");
    Console.WriteLine($"  Avg Duration: {results.AverageDuration.TotalMinutes:F1}min");
    Console.WriteLine($"  Timeout Rate: {results.TimeoutRate:P1}");
}
```

### Workflow Quality vs Performance Trade-offs

Measure the relationship between workflow speed and output quality:

```csharp
var tradeoffResults = await workflowBenchmark.RunQualityPerformanceTradeoffAsync(
    workflowTestCases,
    timeoutConfigurations: new[] { 
        TimeSpan.FromMinutes(2),    // Fast but potentially lower quality
        TimeSpan.FromMinutes(5),    // Balanced
        TimeSpan.FromMinutes(10)    // Thorough but slower
    });

Console.WriteLine($"Quality vs Performance Trade-offs:");
foreach (var result in tradeoffResults)
{
    Console.WriteLine($"  {result.Timeout.TotalMinutes}min timeout:");
    Console.WriteLine($"    Quality Score: {result.AverageQualityScore:F1}%");
    Console.WriteLine($"    Success Rate: {result.SuccessRate:P1}");
    Console.WriteLine($"    Cost: ${result.AverageCost:F4}");
}
```

**Key Workflow Performance Metrics:**

| Metric | Description | Optimal Range |
|--------|-------------|---------------|
| **End-to-End Latency** | Total workflow completion time | < 5 minutes for most use cases |
| **Orchestration Overhead** | Time spent on workflow coordination | < 5% of total time |
| **Executor Load Balance** | Variance in executor durations | Low variance indicates good balance |
| **Pipeline Efficiency** | Useful work time / total time | > 85% |
| **Cost per Workflow** | Total cost across all executors | Domain-specific target |
| **Bottleneck Detection** | Which executor limits throughput | Minimize via optimization |

> **💡 Workflow Performance Tip:** [Sample 09](https://github.com/joslat/AgentEval/tree/main/samples/AgentEval.Samples) (Sequential Workflows) and [Sample 10](https://github.com/joslat/AgentEval/tree/main/samples/AgentEval.Samples) (Tool-Enabled Workflows) demonstrate performance monitoring techniques for multi-agent systems.

---

## Creating Custom Benchmark Suites

Create domain-specific benchmarks tailored to your use case:

```csharp
public class CustomerSupportBenchmark
{
    private readonly IEvaluableAgent _agent;
    private readonly IEvaluator _evaluator;
    
    public CustomerSupportBenchmark(IEvaluableAgent agent, IEvaluator evaluator)
    {
        _agent = agent;
        _evaluator = evaluator;
    }
    
    public async Task<BenchmarkReport> RunFullSuiteAsync()
    {
        var report = new BenchmarkReport
        {
            BenchmarkName = "CustomerSupport-v1",
            AgentName = _agent.Name,
            RunDate = DateTimeOffset.UtcNow
        };
        
        var benchmark = new AgenticBenchmark(_agent, _evaluator);
        var perfBenchmark = new PerformanceBenchmark(_agent);
        
        // Category 1: Tool accuracy
        var toolResults = await benchmark.RunToolAccuracyBenchmarkAsync(GetToolTestCases());
        report.CategoryScores["ToolAccuracy"] = toolResults.OverallAccuracy * 100;
        
        // Category 2: Task completion
        var taskResults = await benchmark.RunTaskCompletionBenchmarkAsync(GetTaskTestCases());
        report.CategoryScores["TaskCompletion"] = taskResults.AverageScore;
        
        // Category 3: Latency
        var latencyResults = await perfBenchmark.RunLatencyBenchmarkAsync(GetToolTestCases());
        report.CategoryScores["P90LatencyMs"] = latencyResults.P90Latency.TotalMilliseconds;
        
        // Overall score (weighted)
        report.OverallScore = 
            report.CategoryScores["ToolAccuracy"] * 0.4 +
            report.CategoryScores["TaskCompletion"] * 0.6;
        
        return report;
    }
    
    private List<ToolAccuracyTestCase> GetToolTestCases() => new()
    {
        new ToolAccuracyTestCase
        {
            Name = "password_reset",
            Prompt = "I forgot my password, help me reset it",
            ExpectedTools = new[] { new ExpectedTool { Name = "reset_password" } }
        },
        new ToolAccuracyTestCase
        {
            Name = "order_lookup",
            Prompt = "Where is my order #12345?",
            ExpectedTools = new[] { new ExpectedTool { Name = "track_order" } }
        }
    };
    
    private List<TaskCompletionTestCase> GetTaskTestCases() => new()
    {
        new TaskCompletionTestCase
        {
            Name = "refund_request",
            Prompt = "I want a refund for my last order",
            CompletionCriteria = new[]
            {
                "Acknowledges the refund request",
                "Retrieves order details",
                "Explains refund policy or timeline"
            }
        }
    };
}
```

### Run and Report

```csharp
var benchmark = new CustomerSupportBenchmark(agent, evaluator);
var report = await benchmark.RunFullSuiteAsync();

Console.WriteLine($"\n{'=',-60}");
Console.WriteLine($"BENCHMARK REPORT: {report.BenchmarkName}");
Console.WriteLine($"{'=',-60}");
Console.WriteLine($"Agent: {report.AgentName}");
Console.WriteLine($"Date: {report.RunDate:yyyy-MM-dd HH:mm}");
Console.WriteLine();
Console.WriteLine("Category Scores:");
foreach (var (category, score) in report.CategoryScores)
{
    var bar = new string('█', (int)(score / 5));
    Console.WriteLine($"  {category,-20} {score,6:F1}  {bar}");
}
Console.WriteLine();
Console.WriteLine($"OVERALL SCORE: {report.OverallScore:F1}/100");
```

Output:
```
============================================================
BENCHMARK REPORT: CustomerSupport-v1
============================================================
Agent: CustomerSupportAgent
Date: 2026-01-13 14:30

Category Scores:
  ToolAccuracy         92.5  ██████████████████
  TaskCompletion       88.2  █████████████████
  P90LatencyMs        1250  (ms)

OVERALL SCORE: 90.0/100
```

---

## Regression Detection

Track benchmark scores over time:

```csharp
// Load previous baseline
var baseline = await LoadBaselineAsync("baseline-v1.0.json");

// Run current benchmark
var current = await benchmark.RunFullSuiteAsync();

// Compare
var regressions = new List<(string Category, double Delta)>();

foreach (var (category, score) in current.CategoryScores)
{
    if (baseline.CategoryScores.TryGetValue(category, out var baselineScore))
    {
        var delta = score - baselineScore;
        if (delta < -5.0)  // More than 5% regression
        {
            regressions.Add((category, delta));
        }
    }
}

if (regressions.Any())
{
    Console.WriteLine("⚠️ REGRESSIONS DETECTED:");
    foreach (var (category, delta) in regressions)
    {
        Console.WriteLine($"  {category}: {delta:+0.0;-0.0}%");
    }
    Environment.ExitCode = 1;  // Fail CI/CD
}
```

---

## CI/CD Integration

### GitHub Actions

```yaml
name: Benchmark

on:
  push:
    branches: [main]
  pull_request:

jobs:
  benchmark:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Run Benchmarks
        env:
          AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
          AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
        run: |
          dotnet run --project benchmarks/AgentBenchmarks.csproj \
            --output results.json \
            --baseline baselines/main.json \
            --fail-on-regression
      
      - name: Upload Results
        uses: actions/upload-artifact@v4
        with:
          name: benchmark-results
          path: results.json
```

---

## See Also

- [Stochastic Evaluation](stochastic-evaluation.md) - Statistical evaluation for benchmarks
- [Model Comparison](model-comparison.md) - Compare models on benchmarks
- [Extensibility Guide](extensibility.md) - Creating custom metrics
