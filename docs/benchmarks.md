# Benchmarks Guide

> **Running industry-standard AI benchmarks and creating custom benchmark suites with AgentEval**

---

## What You Can Do

AgentEval provides comprehensive benchmarking capabilities for AI agents:

| Feature | Description |
|---------|-------------|
| **Performance benchmarks** | Measure latency, throughput, and cost |
| **BFCL-style evaluations** | Tool calling accuracy with industry-standard patterns |
| **GAIA-style evaluations** | Task completion with multi-step reasoning |
| **Multi-step reasoning** | Ordered tool chain validation with dependency tracking |
| **Custom benchmark suites** | Create your own domain-specific benchmarks |
| **JSONL datasets** | Load benchmark data from industry-standard JSONL files |

---

## Quick Start

### ✅ Run Performance Benchmarks
Measure latency, throughput, and cost across your agents.

### ✅ Run Agentic Benchmarks
Evaluate tool accuracy, task completion, and multi-step reasoning.

### ✅ Load from JSONL
Use `DatasetLoaderFactory` to load benchmark data from JSONL files — the industry-standard format used by BFCL, GAIA, MMLU, GSM8K, and ToolBench.

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

var adapter = new MAFAgentAdapter(agent);

// For AI-powered evaluation (task completion), create an evaluator
var evaluator = new ChatClientEvaluator(
    azureClient.GetChatClient("gpt-4o").AsIChatClient());
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
var benchmark = new AgenticBenchmark(adapter);
var results = await benchmark.RunToolAccuracyBenchmarkAsync(bfclTests);

Console.WriteLine($"BFCL-Style Results");
Console.WriteLine($"  Accuracy: {results.OverallAccuracy:P1}");
Console.WriteLine($"  Passed: {results.PassedTests}/{results.TotalTests}");
```

### Loading from JSONL

Instead of hardcoding test cases, load them from JSONL files using `DatasetLoaderFactory`:

```csharp
using AgentEval.DataLoaders;

// Load tool accuracy test cases from JSONL (industry standard!)
var dataset = await DatasetLoaderFactory.LoadAsync("benchmark-tool-accuracy.jsonl");
var toolCases = dataset.Select(dc => dc.ToToolAccuracyTestCase()).ToList();

var results = await benchmark.RunToolAccuracyBenchmarkAsync(toolCases);
```

Example JSONL file (`benchmark-tool-accuracy.jsonl`):
```json
{"id": "weather_simple", "input": "What is the weather in Seattle?", "expected_tools": ["GetWeather"], "metadata": {"required_params": {"GetWeather": ["city"]}}}
{"id": "calc_simple", "input": "Calculate 15 * 7 + 3", "expected_tools": ["Calculate"], "metadata": {"required_params": {"Calculate": ["expression"]}}}
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

var benchmark = new AgenticBenchmark(adapter, evaluator);
var results = await benchmark.RunTaskCompletionBenchmarkAsync(gaiaCases);

Console.WriteLine($"GAIA-Style Results");
Console.WriteLine($"  Average Score: {results.AverageScore:F1}/100");
Console.WriteLine($"  Pass Rate: {(double)results.PassedTests / results.TotalTests:P1}");
```

> **Note:** By default, `AgenticBenchmark` adds two standard criteria ("fully addresses the user's request", "complete and actionable") to every task completion evaluation. Set `AddDefaultCompletionCriteria = false` in `AgenticBenchmarkOptions` for strict criteria-only evaluation.

---

## Performance Benchmarks

Measure latency, throughput, and cost across your agents using `PerformanceBenchmark`.

### Latency Benchmark

```csharp
var benchmark = new PerformanceBenchmark(adapter);

// Single prompt — run multiple iterations
var result = await benchmark.RunLatencyBenchmarkAsync(
    "What is the capital of France?",
    iterations: 10,
    warmupIterations: 2);

Console.WriteLine($"Latency Benchmark");
Console.WriteLine($"  P50: {result.P50Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"  P90: {result.P90Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"  P99: {result.P99Latency.TotalMilliseconds:F0}ms");
Console.WriteLine($"  Mean: {result.MeanLatency.TotalMilliseconds:F0}ms");
Console.WriteLine($"  Min: {result.MinLatency.TotalMilliseconds:F0}ms");
Console.WriteLine($"  Max: {result.MaxLatency.TotalMilliseconds:F0}ms");
```

### Multi-Prompt Latency Benchmark

Use varied prompts to avoid server-side caching effects:

```csharp
// Multiple prompts — avoids LLM server-side caching
var result = await benchmark.RunLatencyBenchmarkAsync(
    new[] { "What is 2+2?", "Name three colors.", "Capital of France?" },
    iterationsPerPrompt: 3,
    warmupIterations: 1);

Console.WriteLine($"  Iterations: {result.Iterations}");   // 9 (3 prompts × 3)
Console.WriteLine($"  Mean: {result.MeanLatency.TotalMilliseconds:F0}ms");
```

### Throughput Benchmark

```csharp
var result = await benchmark.RunThroughputBenchmarkAsync(
    "What is 2+2?",
    concurrentRequests: 10,
    duration: TimeSpan.FromSeconds(60));

Console.WriteLine($"Throughput Benchmark");
Console.WriteLine($"  Requests/sec: {result.RequestsPerSecond:F1}");
Console.WriteLine($"  Completed: {result.CompletedRequests}");
Console.WriteLine($"  Errors: {result.ErrorCount}");
Console.WriteLine($"  Mean Latency: {result.MeanLatency.TotalMilliseconds:F0}ms");
```

### Cost Benchmark

```csharp
var result = await benchmark.RunCostBenchmarkAsync(
    new[] { "prompt1", "prompt2", "prompt3" },
    "gpt-4o");

Console.WriteLine($"Cost Benchmark");
Console.WriteLine($"  Total Tokens: {result.TotalTokens}");
Console.WriteLine($"  Avg Input/Prompt: {result.AverageInputTokensPerPrompt:F0}");
Console.WriteLine($"  Avg Output/Prompt: {result.AverageOutputTokensPerPrompt:F0}");
Console.WriteLine($"  Estimated Cost: ${result.EstimatedCostUSD:F6}");
```

---

## Workflow Performance Benchmarks

> **🚧 Planned Feature** — `WorkflowPerformanceBenchmark` is planned for a future release.
> Currently, use `PerformanceBenchmark` with individual agent adapters within your workflow,
> or use `MAFEvaluationHarness` with `TrackPerformance = true` for end-to-end workflow timing.
> See [Sample 09](samples/AgentEval.Samples/Sample09_WorkflowEvaluationReal.cs) (Sequential Workflows) and
> [Sample 10](samples/AgentEval.Samples/Sample10_WorkflowWithTools.cs) (Tool-Enabled Workflows) for workflow
> performance monitoring patterns.

---

## Creating Custom Benchmark Suites

Create domain-specific benchmarks tailored to your use case by composing
`AgenticBenchmark` and `PerformanceBenchmark`:

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
    
    public async Task<Dictionary<string, double>> RunFullSuiteAsync()
    {
        var scores = new Dictionary<string, double>();
        
        var benchmark = new AgenticBenchmark(_agent, _evaluator);
        var perfBenchmark = new PerformanceBenchmark(_agent);
        
        // Category 1: Tool accuracy
        var toolResults = await benchmark.RunToolAccuracyBenchmarkAsync(GetToolTestCases());
        scores["ToolAccuracy"] = toolResults.OverallAccuracy * 100;
        
        // Category 2: Task completion
        var taskResults = await benchmark.RunTaskCompletionBenchmarkAsync(GetTaskTestCases());
        scores["TaskCompletion"] = taskResults.AverageScore;
        
        // Category 3: Latency
        var latencyResult = await perfBenchmark.RunLatencyBenchmarkAsync(
            "What is my order status?", iterations: 5);
        scores["P90LatencyMs"] = latencyResult.P90Latency.TotalMilliseconds;
        
        // Overall score (weighted)
        scores["Overall"] = scores["ToolAccuracy"] * 0.4 + scores["TaskCompletion"] * 0.6;
        
        return scores;
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
var scores = await benchmark.RunFullSuiteAsync();

Console.WriteLine("BENCHMARK RESULTS");
foreach (var (category, score) in scores)
{
    Console.WriteLine($"  {category,-20} {score,8:F1}");
}
```

---

## Regression Detection Pattern

> **Note:** This is a recommended pattern — AgentEval does not yet provide built-in
> baseline storage or comparison infrastructure. You implement the baseline I/O yourself.

```csharp
// Save results as your baseline
var results = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);
File.WriteAllText("baseline.json", JsonSerializer.Serialize(new
{
    Date = DateTimeOffset.UtcNow,
    ToolAccuracy = results.OverallAccuracy,
    PassRate = (double)results.PassedTests / results.TotalTests
}));

// Later, compare against baseline
var baseline = JsonDocument.Parse(File.ReadAllText("baseline.json"));
var baselineAccuracy = baseline.RootElement.GetProperty("ToolAccuracy").GetDouble();

var current = await benchmark.RunToolAccuracyBenchmarkAsync(testCases);

if (current.OverallAccuracy < baselineAccuracy - 0.05)
{
    Console.WriteLine("⚠️ REGRESSION: Tool accuracy dropped!");
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
          dotnet run --project benchmarks/AgentBenchmarks.csproj
```

> **Tip:** Combine with the regression detection pattern above to fail CI on score drops.

---

## API Reference Summary

### PerformanceBenchmark

| Method | Parameters | Returns |
|--------|-----------|---------|
| `RunLatencyBenchmarkAsync` | `string prompt, int iterations, int warmupIterations` | `LatencyBenchmarkResult` |
| `RunLatencyBenchmarkAsync` | `IEnumerable<string> prompts, int iterationsPerPrompt, int warmupIterations` | `LatencyBenchmarkResult` |
| `RunThroughputBenchmarkAsync` | `string prompt, int concurrentRequests, TimeSpan duration` | `ThroughputBenchmarkResult` |
| `RunCostBenchmarkAsync` | `IEnumerable<string> prompts, string modelName` | `CostBenchmarkResult` |

### AgenticBenchmark

| Method | Parameters | Returns |
|--------|-----------|---------|
| `RunToolAccuracyBenchmarkAsync` | `IEnumerable<ToolAccuracyTestCase>` | `ToolAccuracyResult` |
| `RunTaskCompletionBenchmarkAsync` | `IEnumerable<TaskCompletionTestCase>` | `TaskCompletionResult` |
| `RunMultiStepReasoningBenchmarkAsync` | `IEnumerable<MultiStepTestCase>` | `MultiStepReasoningResult` |

### Key Result Properties

| Result Type | Key Properties |
|-------------|---------------|
| `LatencyBenchmarkResult` | `MeanLatency`, `MinLatency`, `MaxLatency`, `P50Latency`, `P90Latency`, `P99Latency`, `MeanTimeToFirstToken`, `AllLatencies` |
| `ThroughputBenchmarkResult` | `RequestsPerSecond`, `CompletedRequests`, `ErrorCount`, `MeanLatency`, `Duration` |
| `CostBenchmarkResult` | `EstimatedCostUSD`, `TotalTokens`, `TotalInputTokens`, `TotalOutputTokens`, `AverageInputTokensPerPrompt` |
| `ToolAccuracyResult` | `OverallAccuracy`, `PassedTests`, `TotalTests`, `Results` |
| `TaskCompletionResult` | `AverageScore`, `PassedTests`, `TotalTests`, `Results` |
| `MultiStepReasoningResult` | `AverageStepCompletion`, `PassedTests`, `TotalTests`, `Results` |

---

## See Also

- [Sample 06](../samples/AgentEval.Samples/Sample06_PerformanceProfiling.cs) - Performance profiling with MAFEvaluationHarness
- [Sample 23](../samples/AgentEval.Samples/Sample23_BenchmarkSystem.cs) - Benchmark system with JSONL data loading
- [Stochastic Evaluation](stochastic-evaluation.md) - Statistical evaluation for benchmarks
- [Model Comparison](model-comparison.md) - Compare models on benchmarks
- [Extensibility Guide](extensibility.md) - Creating custom metrics
