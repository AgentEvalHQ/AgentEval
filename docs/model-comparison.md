# Model Comparison Guide

> **Which model is best for your use case?** Let data answer that question.

---

## The Challenge: So Many Models, So Little Time

You have options:
- GPT-4o vs GPT-4o-mini
- Claude 3.5 Sonnet vs Haiku
- Gemini 1.5 Pro vs Flash
- Open source alternatives

**How do you choose?** 

- Gut feeling? ❌
- Marketing claims? ❌
- Trial and error? ❌
- **Data-driven comparison? ✅**

---

## Model Comparison in 60 Seconds

```csharp
var comparer = new ModelComparer(harness, EvaluationOptions);

var comparison = await comparer.CompareModelsAsync(
    new[] { gpt4oFactory, gpt4oMiniFactory, claudeFactory },
    testCases,
    metrics,
    new ComparisonOptions(RunsPerModel: 10)
);

comparison.PrintComparisonTable();

// Get actionable recommendations
var rec = comparison.Recommendation;
Console.WriteLine($"🏆 Best Overall: {rec.BestOverall}");
Console.WriteLine($"💰 Best Value: {rec.BestValue}");
Console.WriteLine($"⭐ Best Quality: {rec.BestQuality}");
```

Output:
```
┌──────────────────────────────────────────────────────────────────────────────┐
│                        Model Comparison Results                              │
├──────────────┬─────────────┬────────────┬──────────┬────────────┬───────────┤
│ Model        │ Success Rate│ Mean Score │ Latency  │ Cost/1K    │ Recommend │
├──────────────┼─────────────┼────────────┼──────────┼────────────┼───────────┤
│ gpt-4o       │ 94%         │ 91.2       │ 2.1s     │ $0.015     │ ⭐ Quality│
│ gpt-4o-mini  │ 89%         │ 85.4       │ 0.8s     │ $0.00015   │ 💰 Value  │
│ claude-3.5   │ 92%         │ 89.7       │ 1.8s     │ $0.012     │ 🏆 Overall│
└──────────────┴─────────────┴────────────┴──────────┴────────────┴───────────┘
```

**That's it.** No spreadsheets. No manual comparisons. Actionable recommendations.

---

## Setting Up Model Comparison

### Step 1: Create Agent Factories

Each model needs a factory that creates agents consistently:

```csharp
public class GPT4oAgentFactory : IAgentFactory
{
    public string ModelId => "gpt-4o";
    public string ModelName => "GPT-4o";
    
    public IEvaluableAgent CreateAgent()
    {
        var chatClient = new AzureOpenAIChatClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!),
            "gpt-4o"
        );
        
        return new MAFAgentAdapter(new AIAgent(chatClient, tools));
    }
}

public class GPT4oMiniAgentFactory : IAgentFactory
{
    public string ModelId => "gpt-4o-mini";
    public string ModelName => "GPT-4o Mini";
    
    public IEvaluableAgent CreateAgent()
    {
        var chatClient = new AzureOpenAIChatClient(
            new Uri(Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT")!),
            new AzureKeyCredential(Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!),
            "gpt-4o-mini"
        );
        
        return new MAFAgentAdapter(new AIAgent(chatClient, tools));
    }
}

public class ClaudeAgentFactory : IAgentFactory
{
    public string ModelId => "claude-3.5-sonnet";
    public string ModelName => "Claude 3.5 Sonnet";
    
    public IEvaluableAgent CreateAgent()
    {
        // Your Claude client setup
        return new MAFAgentAdapter(new AIAgent(claudeClient, tools));
    }
}
```

### Step 2: Define Test Cases

```csharp
var testCases = new[]
{
    new TestCase
    {
        Name = "Simple Weather Query",
        Input = "What's the weather in Seattle?",
        ExpectedOutput = "Contains temperature"
    },
    new TestCase
    {
        Name = "Complex Booking Flow",
        Input = "Book a flight from NYC to Paris for next Monday",
        ExpectedOutput = "Flight booked"
    },
    new TestCase
    {
        Name = "Multi-step Research",
        Input = "Compare iPhone 15 and Samsung S24 specs",
        ExpectedOutput = "Comparison with specs"
    }
};
```

### Step 3: Select Metrics

```csharp
var metrics = new IMetric[]
{
    new RelevanceMetric(judgeChatClient),
    new FaithfulnessMetric(judgeChatClient),
    new ToolSuccessMetric()
};
```

### Step 4: Run Comparison

```csharp
var comparer = new ModelComparer(harness, EvaluationOptions);

var comparison = await comparer.CompareModelsAsync(
    factories: new[] { gpt4oFactory, gpt4oMiniFactory, claudeFactory },
    testCases: testCases,
    metrics: metrics,
    options: new ComparisonOptions(
        RunsPerModel: 10,           // Stochastic runs per model
        IncludePerformance: true,   // Track latency, cost
        IncludeToolUsage: true      // Track tool calls
    )
);
```

---

## Understanding the Results

### ComparisonResult Structure

```csharp
public class ComparisonResult
{
    // Results per model
    public IReadOnlyDictionary<string, ModelResult> ModelResults { get; }
    
    // Aggregated recommendations
    public ModelRecommendation Recommendation { get; }
    
    // Per-test-case breakdowns
    public IReadOnlyList<TestCaseComparison> TestCaseResults { get; }
}

public class ModelResult
{
    public string ModelId { get; }
    public string ModelName { get; }
    public double SuccessRate { get; }
    public double MeanScore { get; }
    public double StandardDeviation { get; }
    public TimeSpan MeanLatency { get; }
    public decimal MeanCostPerRequest { get; }
    public StochasticStatistics Statistics { get; }
}

public class ModelRecommendation
{
    public string BestOverall { get; }      // Balanced choice
    public string BestQuality { get; }      // Highest scores
    public string BestValue { get; }        // Best quality/cost ratio
    public string BestSpeed { get; }        // Lowest latency
    public string MostConsistent { get; }   // Lowest variance
}
```

### Accessing Detailed Results

```csharp
// Iterate through model results
foreach (var (modelId, result) in comparison.ModelResults)
{
    Console.WriteLine($"\n=== {result.ModelName} ===");
    Console.WriteLine($"Success Rate: {result.SuccessRate:P0}");
    Console.WriteLine($"Mean Score: {result.MeanScore:F1}");
    Console.WriteLine($"Std Dev: {result.StandardDeviation:F2}");
    Console.WriteLine($"Mean Latency: {result.MeanLatency.TotalSeconds:F2}s");
    Console.WriteLine($"Cost/Request: ${result.MeanCostPerRequest:F4}");
}

// Get recommendations
var rec = comparison.Recommendation;
Console.WriteLine($"\n🏆 Best Overall: {rec.BestOverall}");
Console.WriteLine($"⭐ Best Quality: {rec.BestQuality}");
Console.WriteLine($"💰 Best Value: {rec.BestValue}");
Console.WriteLine($"⚡ Best Speed: {rec.BestSpeed}");
Console.WriteLine($"📊 Most Consistent: {rec.MostConsistent}");
```

---

## Comparison Options

```csharp
var options = new ComparisonOptions(
    RunsPerModel: 10,              // Stochastic runs per model
    IncludePerformance: true,      // Track latency, tokens, cost
    IncludeToolUsage: true,        // Track tool call success
    ParallelModels: false,         // Run models sequentially
    WarmupRuns: 1,                 // Warm-up runs (not counted)
    SuccessThreshold: 70.0         // Score >= this is "success"
);
```

### Parallel Execution

```csharp
// Run models in parallel (faster, but higher API load)
var options = new ComparisonOptions(
    RunsPerModel: 10,
    ParallelModels: true
);

// Caution: May hit rate limits
```

---

## Visual Outputs

### Console Table

```csharp
comparison.PrintComparisonTable();
```

### Markdown Report

```csharp
var markdown = comparison.ToMarkdown();
File.WriteAllText("model-comparison.md", markdown);
```

### JSON Export

```csharp
var json = comparison.ToJson();
File.WriteAllText("model-comparison.json", json);
```

---

## Advanced Patterns

### Weighted Comparison

Weight factors by importance to your use case:

```csharp
var weights = new ComparisonWeights
{
    Quality = 0.4,      // 40% weight on score
    Speed = 0.3,        // 30% weight on latency
    Cost = 0.2,         // 20% weight on cost
    Consistency = 0.1   // 10% weight on low variance
};

var comparison = await comparer.CompareModelsAsync(
    factories, testCases, metrics,
    new ComparisonOptions(RunsPerModel: 10, Weights: weights)
);

// BestOverall now reflects your priorities
Console.WriteLine($"Best for your weights: {comparison.Recommendation.BestOverall}");
```

### Per-Test-Case Analysis

```csharp
foreach (var tcResult in comparison.TestCaseResults)
{
    Console.WriteLine($"\n=== {tcResult.TestCase.Name} ===");
    
    foreach (var (modelId, score) in tcResult.ModelScores)
    {
        Console.WriteLine($"  {modelId}: {score:F1}");
    }
    
    Console.WriteLine($"  Best: {tcResult.BestModelForThisTest}");
}
```

### Finding the Right Model per Use Case

```csharp
// Different test categories
var simpleQueries = testCases.Where(t => t.Tags.Contains("simple"));
var complexFlows = testCases.Where(t => t.Tags.Contains("complex"));

var simpleComparison = await comparer.CompareModelsAsync(
    factories, simpleQueries.ToArray(), metrics, options);

var complexComparison = await comparer.CompareModelsAsync(
    factories, complexFlows.ToArray(), metrics, options);

Console.WriteLine($"Best for simple queries: {simpleComparison.Recommendation.BestValue}");
Console.WriteLine($"Best for complex flows: {complexComparison.Recommendation.BestQuality}");
```

---

## Cost-Quality Tradeoffs

### Pareto Frontier Analysis

```csharp
// Find models on the Pareto frontier (best quality for their cost tier)
var paretoModels = comparison.ModelResults
    .Values
    .OrderBy(m => m.MeanCostPerRequest)
    .Where((m, i) => 
        i == 0 || // Cheapest model is always on frontier
        m.MeanScore > comparison.ModelResults.Values
            .Where(other => other.MeanCostPerRequest < m.MeanCostPerRequest)
            .Max(other => other.MeanScore)
    )
    .ToList();

Console.WriteLine("Pareto-optimal models (best quality for cost):");
foreach (var model in paretoModels)
{
    Console.WriteLine($"  {model.ModelName}: Score={model.MeanScore:F1}, Cost=${model.MeanCostPerRequest:F4}");
}
```

### Budget-Constrained Selection

```csharp
decimal maxCostPerRequest = 0.01m;

var withinBudget = comparison.ModelResults.Values
    .Where(m => m.MeanCostPerRequest <= maxCostPerRequest)
    .OrderByDescending(m => m.MeanScore)
    .FirstOrDefault();

Console.WriteLine($"Best model within ${maxCostPerRequest}/request: {withinBudget?.ModelName}");
```

### Quality-Constrained Selection

```csharp
double minScore = 85.0;

var meetsQuality = comparison.ModelResults.Values
    .Where(m => m.MeanScore >= minScore)
    .OrderBy(m => m.MeanCostPerRequest)
    .FirstOrDefault();

Console.WriteLine($"Cheapest model with score >= {minScore}: {meetsQuality?.ModelName}");
```

---

## CI/CD Integration

### Regression Detection

```csharp
// Load previous comparison results
var previousComparison = ComparisonResult.Load("baseline-comparison.json");

var currentComparison = await comparer.CompareModelsAsync(
    factories, testCases, metrics, options);

// Detect regressions
foreach (var (modelId, current) in currentComparison.ModelResults)
{
    var previous = previousComparison.ModelResults[modelId];
    
    var scoreDrop = previous.MeanScore - current.MeanScore;
    if (scoreDrop > 5.0)
    {
        Console.WriteLine($"⚠️ {modelId} regressed: {previous.MeanScore:F1} → {current.MeanScore:F1}");
    }
}
```

### GitHub Action for Model Comparison

```yaml
jobs:
  model-comparison:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Run Model Comparison
        env:
          AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
          AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
        run: |
          dotnet run --project tools/ModelComparison \
            --output comparison-results.json \
            --markdown comparison-results.md
      
      - name: Upload Comparison Results
        uses: actions/upload-artifact@v4
        with:
          name: model-comparison
          path: |
            comparison-results.json
            comparison-results.md
      
      - name: Comment on PR
        if: github.event_name == 'pull_request'
        uses: actions/github-script@v7
        with:
          script: |
            const fs = require('fs');
            const markdown = fs.readFileSync('comparison-results.md', 'utf8');
            github.rest.issues.createComment({
              issue_number: context.issue.number,
              owner: context.repo.owner,
              repo: context.repo.repo,
              body: markdown
            });
```

---

## Model Comparison with Trace Replay

Reduce costs by recording once, comparing from traces:

```csharp
// RECORD: Capture traces from each model once
foreach (var factory in factories)
{
    var agent = factory.CreateAgent();
    var traces = new List<AgentTrace>();
    
    foreach (var testCase in testCases)
    {
        var recorder = new TraceRecordingAgent(agent);
        await recorder.ExecuteAsync(testCase.Input);
        traces.Add(recorder.GetTrace());
    }
    
    TraceSerializer.SaveMany(traces, $"traces/{factory.ModelId}.json");
}

// COMPARE: Replay and evaluate without API calls
var replayResults = new Dictionary<string, List<TestResult>>();

foreach (var factory in factories)
{
    var traces = TraceSerializer.LoadMany($"traces/{factory.ModelId}.json");
    var results = new List<TestResult>();
    
    foreach (var trace in traces)
    {
        var replayer = new TraceReplayingAgent(trace);
        var response = await replayer.ReplayNextAsync();
        var result = await EvaluateResponse(response);
        results.Add(result);
    }
    
    replayResults[factory.ModelId] = results;
}
```

---

## Common Comparison Scenarios

### Scenario 1: Upgrade Evaluation

*Should we upgrade from GPT-4 to GPT-4o?*

```csharp
var factories = new[] { gpt4Factory, gpt4oFactory };

var comparison = await comparer.CompareModelsAsync(
    factories, testCases, metrics, 
    new ComparisonOptions(RunsPerModel: 20));

var gpt4 = comparison.ModelResults["gpt-4"];
var gpt4o = comparison.ModelResults["gpt-4o"];

Console.WriteLine($"GPT-4 → GPT-4o:");
Console.WriteLine($"  Score: {gpt4.MeanScore:F1} → {gpt4o.MeanScore:F1}");
Console.WriteLine($"  Latency: {gpt4.MeanLatency.TotalSeconds:F2}s → {gpt4o.MeanLatency.TotalSeconds:F2}s");
Console.WriteLine($"  Cost: ${gpt4.MeanCostPerRequest:F4} → ${gpt4o.MeanCostPerRequest:F4}");
Console.WriteLine($"  Recommendation: {(gpt4o.MeanScore > gpt4.MeanScore ? "✅ Upgrade" : "❌ Stay")}");
```

### Scenario 2: Cost Reduction

*Can we use a cheaper model without losing quality?*

```csharp
var factories = new[] { gpt4oFactory, gpt4oMiniFactory };

var comparison = await comparer.CompareModelsAsync(
    factories, testCases, metrics, options);

var expensive = comparison.ModelResults["gpt-4o"];
var cheap = comparison.ModelResults["gpt-4o-mini"];

var qualityDrop = expensive.MeanScore - cheap.MeanScore;
var costSavings = 1 - (cheap.MeanCostPerRequest / expensive.MeanCostPerRequest);

Console.WriteLine($"Quality drop: {qualityDrop:F1} points");
Console.WriteLine($"Cost savings: {costSavings:P0}");

if (qualityDrop < 5.0 && costSavings > 0.5)
{
    Console.WriteLine("✅ Switch to cheaper model - minimal quality impact");
}
```

### Scenario 3: Multi-Provider Evaluation

*Which provider is best: OpenAI, Anthropic, or Google?*

```csharp
var factories = new[]
{
    new OpenAIAgentFactory("gpt-4o"),
    new AnthropicAgentFactory("claude-3.5-sonnet"),
    new GoogleAgentFactory("gemini-1.5-pro")
};

var comparison = await comparer.CompareModelsAsync(
    factories, testCases, metrics, options);

comparison.PrintComparisonTable();
Console.WriteLine($"\nBest overall: {comparison.Recommendation.BestOverall}");
```

---

## Best Practices

### 1. Use Representative Test Cases

```csharp
// ✅ Good: Mix of difficulty levels
var testCases = new[]
{
    new TestCase { Name = "Simple", Input = "What's 2+2?" },
    new TestCase { Name = "Medium", Input = "Summarize this article..." },
    new TestCase { Name = "Complex", Input = "Multi-step reasoning..." }
};

// ❌ Bad: Only easy cases
var testCases = new[]
{
    new TestCase { Name = "Easy1", Input = "Hello" },
    new TestCase { Name = "Easy2", Input = "Hi there" }
};
```

### 2. Run Enough Iterations

```csharp
// ✅ Good: 10+ runs for reliable statistics
new ComparisonOptions(RunsPerModel: 10)

// ❌ Bad: 1-2 runs (lucky/unlucky)
new ComparisonOptions(RunsPerModel: 2)
```

### 3. Control for External Factors

```csharp
// Run all models close in time to avoid API variance
var options = new ComparisonOptions(
    RunsPerModel: 10,
    ParallelModels: false  // Sequential to control timing
);
```

### 4. Consider Your Actual Workload

```csharp
// Weight metrics based on what matters to you
var weights = new ComparisonWeights
{
    Quality = 0.5,      // If accuracy is critical
    Speed = 0.3,        // If latency matters
    Cost = 0.2          // If budget is flexible
};
```

---

## Summary

| Question | AgentEval Answer |
|----------|------------------|
| Which model is best overall? | `comparison.Recommendation.BestOverall` |
| Which has best quality? | `comparison.Recommendation.BestQuality` |
| Which is most cost-effective? | `comparison.Recommendation.BestValue` |
| Which is fastest? | `comparison.Recommendation.BestSpeed` |
| Which is most consistent? | `comparison.Recommendation.MostConsistent` |
| Should we upgrade models? | Compare scores before/after |
| Can we use a cheaper model? | Calculate quality drop vs cost savings |

**Stop guessing which model to use. Let data tell you.**

---

## Next Steps

- [stochastic evaluation](stochastic-testing.md) - The foundation for model comparison
- [Trace Record & Replay](tracing.md) - Reduce comparison costs
- [Code Gallery](showcase/code-gallery.md) - More examples

---

<div align="center">

**Make data-driven model decisions.**

[Get Started →](getting-started.md){ .md-button .md-button--primary }

</div>
