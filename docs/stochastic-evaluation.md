# stochastic evaluation Guide

> **LLMs are non-deterministic. Your tests should account for that.**

---

## The Problem: "It Worked When I Tried It"

You run a test. It passes. You run it again. It fails. Welcome to LLM testing.

```
Run 1: ✅ Pass (score: 95)
Run 2: ✅ Pass (score: 92)
Run 3: ❌ Fail (score: 68)
Run 4: ✅ Pass (score: 88)
Run 5: ✅ Pass (score: 91)
```

**Was that a bug? Random variation? How do you know if your agent "works"?**

Traditional unit testing assumes determinism. LLM testing requires **statistical thinking**.

---

## The Solution: stochastic evaluation

AgentEval's `StochasticRunner` handles LLM non-determinism properly:

```csharp
var stochasticRunner = new StochasticRunner(harness, EvaluationOptions);

var result = await stochasticRunner.RunStochasticTestAsync(
    agent, 
    testCase, 
    new StochasticOptions(
        Runs: 10,                    // Run the test 10 times
        SuccessRateThreshold: 0.8    // Expect 80%+ success
    ));

// Assert on statistical behavior
result.Statistics.SuccessRate.Should().BeGreaterThan(0.85);
result.Statistics.MeanScore.Should().BeGreaterThan(90.0);
```

**Instead of asking "did it pass?", ask "how often does it pass?"**

---

## Quick Start

### Basic Stochastic Test

```csharp
using AgentEval.Comparison;
using AgentEval.Core;

[Fact]
public async Task Agent_ShouldHaveHighSuccessRate()
{
    // Arrange
    var harness = new MAFEvaluationHarness(chatClient, tools, EvaluationOptions);
    var stochasticRunner = new StochasticRunner(harness, EvaluationOptions);
    
    var testCase = new TestCase
    {
        Input = "What's the weather in Seattle?",
        ExpectedOutput = "Contains temperature and conditions"
    };
    
    var options = new StochasticOptions(
        Runs: 10,
        SuccessRateThreshold: 0.8
    );
    
    // Act
    var result = await stochasticRunner.RunStochasticTestAsync(
        agent, testCase, options);
    
    // Assert
    result.Passed.Should().BeTrue();
    result.Statistics.SuccessRate.Should().BeGreaterThan(0.8);
}
```

### Accessing Full Statistics

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase, options);

var stats = result.Statistics;

// Central tendency
Console.WriteLine($"Mean Score: {stats.MeanScore:F1}");
Console.WriteLine($"Median Score: {stats.MedianScore:F1}");

// Variability
Console.WriteLine($"Std Dev: {stats.StandardDeviation:F2}");
Console.WriteLine($"Min: {stats.MinScore:F1}");
Console.WriteLine($"Max: {stats.MaxScore:F1}");

// Percentiles
Console.WriteLine($"25th Percentile: {stats.Percentile25:F1}");
Console.WriteLine($"75th Percentile: {stats.Percentile75:F1}");
Console.WriteLine($"95th Percentile: {stats.Percentile95:F1}");

// Success tracking
Console.WriteLine($"Success Rate: {stats.SuccessRate:P0}");
Console.WriteLine($"Runs: {stats.TotalRuns}");
Console.WriteLine($"Successes: {stats.SuccessCount}");
```

---

## StochasticOptions Reference

```csharp
var options = new StochasticOptions(
    Runs: 10,                         // Number of test iterations
    SuccessRateThreshold: 0.8,        // Minimum success rate (0.0-1.0)
    ScoreThreshold: 70.0,             // Minimum score to count as "success"
    ParallelExecution: false,         // Run tests in parallel?
    ContinueOnFailure: true,          // Continue after first failure?
    WarmupRuns: 1                     // Warm-up runs (not counted)
);
```

### Parameters Explained

| Parameter | Default | Description |
|-----------|---------|-------------|
| `Runs` | 10 | Total test iterations |
| `SuccessRateThreshold` | 0.8 | Pass if success rate ≥ this |
| `ScoreThreshold` | 70.0 | Score ≥ this counts as success |
| `ParallelExecution` | false | Run iterations concurrently |
| `ContinueOnFailure` | true | Run all iterations even if some fail |
| `WarmupRuns` | 0 | Initial runs to discard (cache warming) |

---

## Statistical Assertions

### Success Rate Assertions

```csharp
// Basic success rate check
result.Statistics.SuccessRate.Should().BeGreaterThan(0.8);

// At least N successes
result.Statistics.SuccessCount.Should().BeGreaterOrEqualTo(8);

// Exact success rate
result.Statistics.SuccessRate.Should().BeApproximately(0.9, 0.05);
```

### Score Distribution Assertions

```csharp
// Mean score threshold
result.Statistics.MeanScore.Should().BeGreaterThan(85.0);

// Low variability (consistent behavior)
result.Statistics.StandardDeviation.Should().BeLessThan(10.0);

// No catastrophic failures
result.Statistics.MinScore.Should().BeGreaterThan(50.0);

// High ceiling
result.Statistics.MaxScore.Should().BeGreaterThan(95.0);
```

### Percentile Assertions

```csharp
// 95th percentile (worst common case)
result.Statistics.Percentile95.Should().BeGreaterThan(75.0);

// Median (typical case)
result.Statistics.MedianScore.Should().BeGreaterThan(88.0);

// Interquartile range (middle 50%)
var iqr = result.Statistics.Percentile75 - result.Statistics.Percentile25;
iqr.Should().BeLessThan(15.0);  // Tight distribution
```

---

## Visual Output

### Console Table

```csharp
result.PrintTable("Weather Agent Stochastic Results");
```

Output:
```
┌────────────────────────────────────────────────────────┐
│          Weather Agent Stochastic Results              │
├────────────────────────────────────────────────────────┤
│ Metric              │ Value                            │
├─────────────────────┼──────────────────────────────────┤
│ Total Runs          │ 10                               │
│ Success Count       │ 9                                │
│ Success Rate        │ 90.0%                            │
│ Mean Score          │ 88.3                             │
│ Median Score        │ 90.5                             │
│ Std Deviation       │ 7.2                              │
│ Min Score           │ 68.0                             │
│ Max Score           │ 97.0                             │
│ 95th Percentile     │ 96.1                             │
│ Status              │ ✅ PASSED                        │
└─────────────────────┴──────────────────────────────────┘
```

### Individual Run Details

```csharp
foreach (var run in result.IndividualRuns)
{
    var status = run.Passed ? "✅" : "❌";
    Console.WriteLine($"Run {run.RunNumber}: {status} Score={run.Score:F1}");
}
```

---

## Advanced Patterns

### stochastic evaluation Across Test Cases

```csharp
var testCases = new[]
{
    new TestCase { Input = "Weather in Seattle", /* ... */ },
    new TestCase { Input = "Weather in Tokyo", /* ... */ },
    new TestCase { Input = "Weather in London", /* ... */ },
};

var results = new List<StochasticResult>();

foreach (var testCase in testCases)
{
    var result = await stochasticRunner.RunStochasticTestAsync(
        agent, testCase, options);
    results.Add(result);
}

// Overall statistics
var overallSuccessRate = results.Average(r => r.Statistics.SuccessRate);
overallSuccessRate.Should().BeGreaterThan(0.85);
```

### Comparing Stochastic Results

```csharp
// Before optimization
var beforeResult = await stochasticRunner.RunStochasticTestAsync(
    oldAgent, testCase, options);

// After optimization
var afterResult = await stochasticRunner.RunStochasticTestAsync(
    newAgent, testCase, options);

// Assert improvement
afterResult.Statistics.SuccessRate.Should()
    .BeGreaterThan(beforeResult.Statistics.SuccessRate);

afterResult.Statistics.MeanScore.Should()
    .BeGreaterThan(beforeResult.Statistics.MeanScore);
```

### Regression Detection

```csharp
// Load baseline from saved results
var baseline = LoadBaseline("weather-agent-baseline.json");

var current = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase, options);

// Detect regression (>5% drop in success rate)
var successRateDrop = baseline.SuccessRate - current.Statistics.SuccessRate;
successRateDrop.Should().BeLessThan(0.05, 
    because: "we should not regress more than 5%");

// Detect score degradation
var meanDrop = baseline.MeanScore - current.Statistics.MeanScore;
meanDrop.Should().BeLessThan(5.0,
    because: "mean score should not drop more than 5 points");
```

---

## CI/CD Integration

### Setting Appropriate Thresholds

```csharp
// Development: lenient thresholds
var devOptions = new StochasticOptions(
    Runs: 5,
    SuccessRateThreshold: 0.6
);

// Staging: stricter thresholds
var stagingOptions = new StochasticOptions(
    Runs: 10,
    SuccessRateThreshold: 0.8
);

// Production: strictest thresholds
var prodOptions = new StochasticOptions(
    Runs: 20,
    SuccessRateThreshold: 0.95
);
```

### GitHub Actions Example

```yaml
jobs:
  stochastic-tests:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Run Stochastic Tests
        env:
          AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
          AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
          STOCHASTIC_RUNS: 10
          SUCCESS_THRESHOLD: 0.8
        run: |
          dotnet test --filter "Category=Stochastic" \
            --logger "trx;LogFileName=stochastic-results.trx"
      
      - name: Upload Results
        uses: actions/upload-artifact@v4
        with:
          name: stochastic-results
          path: '**/stochastic-results.trx'
```

### Cost Control in CI

```csharp
// Fewer runs in CI to control costs
var ciRuns = int.Parse(
    Environment.GetEnvironmentVariable("STOCHASTIC_RUNS") ?? "5");

var options = new StochasticOptions(
    Runs: ciRuns,
    SuccessRateThreshold: 0.8
);
```

---

## Combining with Trace Replay

Run stochastic tests without API costs by recording once, replaying many times:

```csharp
// RECORD: Capture 10 real executions
var traces = new List<AgentTrace>();
for (int i = 0; i < 10; i++)
{
    var recorder = new TraceRecordingAgent(realAgent);
    await recorder.ExecuteAsync(testCase.Input);
    traces.Add(recorder.GetTrace());
}
TraceSerializer.SaveMany(traces, "stochastic-traces.json");

// REPLAY: Run stochastic analysis without API calls
var savedTraces = TraceSerializer.LoadMany("stochastic-traces.json");
var replayResults = new List<TestResult>();

foreach (var trace in savedTraces)
{
    var replayer = new TraceReplayingAgent(trace);
    var response = await replayer.ReplayNextAsync();
    // Evaluate the replayed response
    replayResults.Add(EvaluateResponse(response));
}

// Calculate statistics
var stats = StatisticsCalculator.Calculate(replayResults);
Console.WriteLine($"Success Rate: {stats.SuccessRate:P0}");
```

---

## When to Use stochastic evaluation

### ✅ Use stochastic evaluation For:

| Scenario | Why |
|----------|-----|
| Critical user-facing features | Know actual reliability, not lucky-run rate |
| LLM/model upgrades | Detect regressions with statistical confidence |
| Prompt changes | Measure impact across multiple runs |
| A/B testing agents | Compare with proper statistics |
| SLA validation | "95% of requests succeed" needs measurement |

### ❌ Don't Use stochastic evaluation For:

| Scenario | Why |
|----------|-----|
| Deterministic code | No benefit (same result every time) |
| Trace replay tests | Already deterministic |
| Quick feedback loops | Too slow (run unit tests instead) |
| Cost-sensitive CI | API costs multiply by run count |

---

## Best Practices

### 1. Choose the Right Number of Runs

```
5 runs    → Quick feedback, high variance
10 runs   → Good balance (default)
20 runs   → Reliable statistics
50+ runs  → Research/benchmarking
```

### 2. Set Realistic Thresholds

Don't set 100% success rate as threshold—LLMs have inherent variability.

```csharp
// ❌ Unrealistic
new StochasticOptions(Runs: 10, SuccessRateThreshold: 1.0)

// ✅ Realistic
new StochasticOptions(Runs: 10, SuccessRateThreshold: 0.8)
```

### 3. Track Trends, Not Absolutes

```csharp
// Store results over time
var historicalResults = LoadHistoricalResults();
var current = await RunStochasticTest();

// Plot trend
var trend = new SuccessRateTrend(historicalResults.Append(current));
if (trend.IsDecreasing)
{
    Console.WriteLine("⚠️ Success rate trending downward");
}
```

### 4. Use Warm-up Runs for Cold Starts

```csharp
var options = new StochasticOptions(
    Runs: 10,
    WarmupRuns: 2,  // First 2 runs not counted
    SuccessRateThreshold: 0.8
);
```

---

## Troubleshooting

### High Variance in Results

**Symptom:** Standard deviation > 15

**Causes:**
- Prompt is ambiguous
- Edge cases in test input
- Model temperature too high

**Solutions:**
```csharp
// Lower temperature if possible
var chatClient = new AzureOpenAIChatClient(
    endpoint, credential, deployment,
    new ChatCompletionOptions { Temperature = 0.3f });

// More specific prompts
var testCase = new TestCase
{
    Input = "What is the current temperature in Seattle, WA in Fahrenheit?"
    // Not: "What's the weather like?"
};
```

### Inconsistent Tool Calls

**Symptom:** Sometimes calls tool, sometimes doesn't

**Solution:** Use tool-specific assertions in stochastic context

```csharp
var toolCallRates = result.IndividualRuns
    .Where(r => r.ToolUsage?.ToolCalls.Any(t => t.Name == "WeatherAPI") == true)
    .Count() / (double)result.Statistics.TotalRuns;

toolCallRates.Should().BeGreaterThan(0.9, 
    because: "WeatherAPI should be called 90%+ of the time");
```

---

## Summary

| Concept | Traditional Testing | stochastic evaluation |
|---------|--------------------|--------------------|
| Question | "Did it pass?" | "How often does it pass?" |
| Result | Boolean | Statistics |
| Threshold | Pass/Fail | Success Rate |
| Confidence | Low (1 sample) | High (N samples) |
| Cost | 1 API call | N API calls |

**stochastic evaluation transforms LLM testing from "hope it works" to "know how often it works."**

---

## Next Steps

- [Model Comparison](model-comparison.md) - Compare models with stochastic evaluation
- [Trace Record & Replay](tracing.md) - Reduce API costs in stochastic tests
- [Code Gallery](showcase/code-gallery.md) - See stochastic evaluation examples

---

<div align="center">

**Stop guessing. Start measuring.**

[Get Started →](getting-started.md){ .md-button .md-button--primary }

</div>
