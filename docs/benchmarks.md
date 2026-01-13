# Benchmarks Guide

> **Running industry-standard AI benchmarks and creating custom benchmark suites with AgentEval**

---

## Implementation Status

| Feature | Status | Description |
|---------|--------|-------------|
| **Custom benchmark suites** | ✅ Available | Create your own domain-specific benchmarks |
| **Performance benchmarks** | ✅ Available | Latency, throughput, cost measurement |
| **Manual BFCL-style tests** | ✅ Available | Create test cases following BFCL patterns |
| **Manual GAIA-style tests** | ✅ Available | Create task completion test cases |
| **BFCL dataset loader** | 🚧 Planned Q1 2026 | Auto-download from HuggingFace |
| **GAIA dataset loader** | 🚧 Planned Q1 2026 | Auto-download from HuggingFace |
| **ToolBench loader** | 🚧 Planned Q2 2026 | Auto-download from GitHub |
| **HumanEval** | ❌ Out of Scope | Requires code execution sandbox |
| **WebArena** | ❌ Out of Scope | Requires browser simulation |

> 📖 See [ADR-009: Benchmark Strategy](adr/009-benchmark-strategy.md) for rationale.

---

## Quick Reference: What You Can Do Today

### ✅ Create Custom Benchmarks
Write your own benchmark test suites for your domain.

### ✅ Run Performance Benchmarks
Measure latency, throughput, and cost across your agents.

### ✅ Manual Benchmark-Style Tests
Create tests following BFCL/GAIA patterns and compare against published leaderboards.

### 🚧 Coming Soon: Dataset Loaders
One-line download and run of BFCL, GAIA, ToolBench benchmarks.

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
        Instructions = "You are a helpful assistant with access to various tools.",
        Tools = [/* Your tools here */]
    });

// For AI-powered evaluation, create an evaluator client
var evaluator = azureClient.GetChatClient("gpt-4o").AsIChatClient();
```

---

## Supported Benchmarks

| Benchmark | Focus | Status | What It Tests |
|-----------|-------|--------|---------------|
| **BFCL** | Function Calling | ✅ Manual / 🚧 Loader | Tool selection, arguments, multi-turn |
| **GAIA** | General AI Assistants | ✅ Manual / 🚧 Loader | Multi-step reasoning, tool use |
| **ToolBench** | API Tool Use | ✅ Manual / 🚧 Loader | Complex API workflows |
| **MINT** | Multi-turn Interaction | ✅ Manual | Conversation handling |
| **HumanEval** | Code Generation | ❌ Out of Scope | Requires code execution |
| **WebArena** | Web Browsing | ❌ Out of Scope | Requires browser simulation |

---

## Berkeley Function Calling Leaderboard (BFCL)

BFCL is the industry standard for evaluating function/tool calling accuracy.

### What You Can Do Today ✅

Create BFCL-style test cases manually:

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

### Coming Soon: Auto-Download 🚧

```csharp
// PLANNED - Not yet implemented
var loader = new BfclDatasetLoader();
var dataset = await loader.LoadAsync(BfclCategory.SimplePython);
var benchmark = new BfclBenchmark(dataset);
var result = await benchmark.RunAsync(agent, options);
```

### Compare Against Published Leaderboard

BFCL publishes scores for major models. Run your tests and compare:

| Model | Simple | Parallel | Multiple | Multi-turn | Overall |
|-------|--------|----------|----------|------------|---------|
| GPT-4o | 94.5% | 91.2% | 88.5% | 82.1% | 89.1% |
| Claude-3.5 | 92.1% | 88.7% | 85.2% | 79.8% | 86.5% |
| Gemini-1.5 | 91.8% | 87.5% | 84.1% | 78.2% | 85.4% |
| **Your Agent** | ?% | ?% | ?% | ?% | ?% |

---

## GAIA (General AI Assistants)

GAIA tests multi-step reasoning and real-world task completion.

### What You Can Do Today ✅

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

### Latency Benchmark ✅

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

### Throughput Benchmark ✅

```csharp
var throughputResults = await benchmark.RunThroughputBenchmarkAsync(
    testCases,
    durationSeconds: 60,
    maxConcurrency: 10);

Console.WriteLine($"Throughput Benchmark");
Console.WriteLine($"  Requests/sec: {throughputResults.RequestsPerSecond:F1}");
Console.WriteLine($"  Success Rate: {throughputResults.SuccessRate:P1}");
```

### Cost Benchmark ✅

```csharp
var costResults = await benchmark.RunCostBenchmarkAsync(testCases);

Console.WriteLine($"Cost Benchmark");
Console.WriteLine($"  Total Cost: ${costResults.TotalCost:F4}");
Console.WriteLine($"  Avg per Request: ${costResults.AverageCostPerRequest:F6}");
Console.WriteLine($"  Total Tokens: {costResults.TotalTokens}");
```

---

## Creating Custom Benchmark Suites

### Define Your Domain Benchmark ✅

```csharp
public class CustomerSupportBenchmark
{
    private readonly ITestableAgent _agent;
    private readonly IEvaluator _evaluator;
    
    public CustomerSupportBenchmark(ITestableAgent agent, IEvaluator evaluator)
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

## Benchmark Roadmap

### Phase 1: Core Infrastructure (Current)
- ✅ Manual BFCL/GAIA-style test cases
- ✅ Performance benchmarks (latency, throughput, cost)
- ✅ Custom benchmark suites
- ✅ Result aggregation and reporting

### Phase 2: Dataset Loaders (Q1 2026)
- 🚧 `BfclDatasetLoader` - Download from HuggingFace
- 🚧 `GaiaDatasetLoader` - Download from HuggingFace
- 🚧 Local caching with version management
- 🚧 Subset selection for quick validation

### Phase 3: Additional Benchmarks (Q2 2026)
- 🚧 `ToolBenchDatasetLoader` - Download from GitHub
- 🚧 CLI integration: `agenteval benchmark bfcl --model gpt-4o`
- 🚧 Leaderboard submission format export
- 🚧 Regression tracking across versions

### Phase 4: Future Considerations
- ❓ HumanEval (requires secure code execution sandbox)
- ❓ SWE-bench (requires git/code editing capabilities)
- ❓ WebArena (requires browser automation)

> 📖 See [Implementation Plan: Benchmarks](../strategy/Implementation-Plan-Benchmarks.md) for detailed implementation guidance.

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

## Why HumanEval is Out of Scope

HumanEval requires executing LLM-generated code, which presents:

1. **Security risks**: Untrusted code could be malicious
2. **Sandboxing complexity**: Requires Docker, process isolation, resource limits
3. **Language dependencies**: Python runtime, package management
4. **Maintenance burden**: Keeping sandbox secure is ongoing work

If you need HumanEval support, consider:
- [OpenAI's official human-eval](https://github.com/openai/human-eval)
- [Evalplus](https://github.com/evalplus/evalplus)
- Building your own Docker-based sandbox

> 📖 See [ADR-009](adr/009-benchmark-strategy.md) for full rationale and future considerations.

---

## See Also

- [ADR-009: Benchmark Strategy](adr/009-benchmark-strategy.md) - Architecture decision
- [Stochastic Testing](stochastic-testing.md) - Statistical testing for benchmarks
- [Model Comparison](model-comparison.md) - Compare models on benchmarks
- [Extensibility Guide](extensibility.md) - Creating custom metrics
