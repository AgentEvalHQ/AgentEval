# Model Comparison Guide

> **Which model is best for your use case?** Run the same test cases on each model, several times each, and compare what came back.

---

## The Challenge: So Many Models, So Little Time

You have options:
- GPT-4o vs GPT-4o mini
- A Claude or Gemini model vs an OpenAI one
- An open-weights model you host yourself

Marketing claims and a single run of each tell you little about how a model behaves on *your* tasks. `ModelComparer` runs the same test cases on every model, repeats each run so that one lucky or unlucky reply does not decide the result, and ranks the models on quality, speed, cost and reliability.

---

## Model Comparison in 60 Seconds

```csharp
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;

var harness = new MAFEvaluationHarness(judgeClient);   // judgeClient: the IChatClient that grades replies
var stochasticRunner = new StochasticRunner(harness);
var comparer = new ModelComparer(stochasticRunner);

// CreateAgent(deployment) is your code: it returns a fresh IEvaluableAgent for that model
var factories = new IAgentFactory[]
{
    new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
    new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini"))
};

var testCase = new TestCase
{
    Name = "Refund policy",
    Input = "Can I return an opened item after 20 days?",
    EvaluationCriteria = ["States the 30-day return window", "Mentions that a receipt is required"]
};

var result = await comparer.CompareModelsAsync(factories, testCase,
    new ModelComparisonOptions(RunsPerModel: 10));

Console.WriteLine(result.Summary);
```

Illustrative output (two models, nothing priced the runs):
```
🏆 Model Comparison Results for: Refund policy
   Test: "Can I return an opened item after 20 days?"

Rankings:
   🥇 GPT-4o - Score: 80.0 (Quality: 100.0, Speed: 0.0, Cost: 100.0, Reliability: 100.0)
   🥈 GPT-4o Mini - Score: 60.0 (Quality: 0.0, Speed: 100.0, Cost: 100.0, Reliability: 100.0)

Recommendation: Use GPT-4o
```

Two things to notice. With two models every dimension score is 0 or 100, because the scores rank the models against each other rather than grade them ([How the Scores Are Computed](#how-the-scores-are-computed)). And both models score 100 on cost because no run was priced ([Cost is priced from one model name](#what-the-ranking-does-not-tell-you)).

---

## Setting Up Model Comparison

### Step 1: An Agent Factory per Model

`IAgentFactory` has a `ModelId`, a `ModelName` and a `CreateAgent()` method. `CreateAgent()` is called once per run, so return a new agent each time. `DelegateAgentFactory` wraps a delegate, which covers most cases:

```csharp
// One way to write CreateAgent: any IChatClient becomes an evaluable agent.
// CreateChatClient(deployment) is yours: Azure OpenAI, OpenAI, Ollama, or any other IChatClient.
IEvaluableAgent CreateAgent(string deployment) =>
    CreateChatClient(deployment).AsEvaluableAgent(
        name: "SupportAgent",
        systemPrompt: "You are a customer support assistant for an online store.");
```

For a Microsoft Agent Framework `AIAgent`, return `new MAFAgentAdapter(aiAgent)` instead (see [Sample D3](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/PerformanceAndStatistics/03_ModelComparison.cs)). Implement `IAgentFactory` yourself when a factory needs its own state. `ModelComparer` does not read `IAgentFactory.Configuration`.

### Step 2: Define Test Cases

```csharp
var testCases = new[]
{
    new TestCase
    {
        Name = "Order status",
        Input = "Where is order 1042?",
        ExpectedOutputContains = "1042"
    },
    new TestCase
    {
        Name = "Refund policy",
        Input = "Can I return an opened item after 20 days?",
        EvaluationCriteria = ["States the 30-day return window", "Mentions that a receipt is required"]
    }
};
```

How `MAFEvaluationHarness` scores a run decides what "quality" means in the comparison:

- With a judge (`new MAFEvaluationHarness(judgeClient)`) and `EvaluationCriteria`, the judge grades the reply against the criteria, 0 to 100.
- Otherwise a run scores 100 when the reply is non-empty and contains `ExpectedOutputContains` (when set), and 0 when it does not. A test case with neither criteria nor an expected substring scores 100 for any non-empty reply, so it compares speed and reliability, not quality.

### Step 3: Run the Comparison

```csharp
var stochasticRunner = new StochasticRunner(harness);
var comparer = new ModelComparer(stochasticRunner);

IReadOnlyList<ModelComparisonResult> results = await comparer.CompareModelsAsync(
    factories: factories,
    testCases: testCases,
    options: new ModelComparisonOptions(RunsPerModel: 10));
```

The overload that takes a list runs the single-test-case comparison once per test case, in order, and returns one `ModelComparisonResult` per test case. Each result ranks the models on that test case only; `results.ToMarkdown()` adds up wins and averages the scores across them.

A comparison makes models × test cases × `RunsPerModel` agent calls, plus the judge's calls when a judge grades the replies.

---

## Understanding the Results

### Result Types

Abridged from `AgentEval.Comparison`:

```csharp
public record ModelComparisonResult(
    TestCase TestCase,
    IReadOnlyList<ModelResult> ModelResults,   // one per factory, in factory order
    IReadOnlyList<ModelRanking> Ranking,       // highest composite score first
    ModelRanking Winner,                       // Ranking[0]
    ModelComparisonOptions Options);           // plus a Summary string

public record ModelResult(
    string ModelId,
    string ModelName,
    StochasticResult StochasticResult,         // every run, and their statistics
    TimeSpan AverageLatency,
    decimal? AverageCost,                      // null when no run carried a cost estimate
    decimal? TotalCost);                       // plus PassRate, MeanScore, StandardDeviation

public record ModelRanking(
    string ModelId,
    string ModelName,
    double CompositeScore,
    double QualityScore,
    double SpeedScore,
    double CostScore,
    double ReliabilityScore,
    int Rank);                                 // 1 = best
```

### Accessing Detailed Results

```csharp
foreach (var model in result.ModelResults)
{
    var cost = model.AverageCost is { } c ? $"${c:F4}" : "not measured";
    Console.WriteLine($"{model.ModelName}: pass rate {model.PassRate:P0}, mean score {model.MeanScore:F1} " +
                      $"(SD {model.StandardDeviation:F1}), latency {model.AverageLatency.TotalSeconds:F2}s, cost/run {cost}");
}

foreach (var ranking in result.Ranking)
{
    Console.WriteLine($"#{ranking.Rank} {ranking.ModelName}: composite {ranking.CompositeScore:F1} " +
                      $"(quality {ranking.QualityScore:F1}, speed {ranking.SpeedScore:F1}, " +
                      $"cost {ranking.CostScore:F1}, reliability {ranking.ReliabilityScore:F1})");
}

Console.WriteLine($"Winner: {result.Winner.ModelName}");
```

---

## How the Scores Are Computed

1. Each model runs `RunsPerModel` times through the stochastic runner. `ModelResult.StochasticResult` keeps every run (`IndividualResults`) and their statistics.
2. Each model gets four raw numbers: mean score (quality), average latency (speed), average estimated cost per run (cost) and pass rate (reliability).
3. Each raw number is rescaled to 0–100 across the models **in this comparison**: the best model gets 100 and the worst 0, with lower latency and lower cost counting as better. With one model, or when every model has the same value, every model gets 100.
4. `CompositeScore` = Quality × `ScoringWeights.Quality` + Speed × `Speed` + Cost × `Cost` + Reliability × `Reliability`, with default weights 0.4 / 0.2 / 0.2 / 0.2. `Ranking` sorts by composite score, highest first; on a tie the factory listed first ranks first. `Winner` is `Ranking[0]`.

What follows from this:

- **The scores are relative.** A speed score of 0 means "slowest of these models", not "slow". Adding or removing a model changes every model's scores.
- **Small differences use the whole scale.** Two models with mean scores of 91.0 and 90.5 get quality scores of 100 and 0. Check `ModelResults` and the confidence interval in `StochasticResult.Statistics.ConfidenceInterval` before treating a rank as a real difference.

### What the Ranking Does Not Tell You

- **Cost is priced from one model name.** The harness estimates a run's cost from its token counts and the price of `EvaluationOptions.ModelName` in `ModelPricing`. `StochasticRunner` passes the same `EvaluationOptions` for every model, so every model is priced at that one model's rate, and the cost dimension then compares token usage, not prices. With no `ModelName` (as in the examples above), or a name missing from the price table, no run is priced: `AverageCost` is `null` for every model and every model gets the same cost score, 100. To compare prices, price each model's tokens at its own rate ([Cost-Quality Tradeoffs](#cost-quality-tradeoffs)), or set the cost weight to 0.
- **Missing data scores as best.** A run records latency and cost only when the agent call returns. A model whose every run throws has an average latency of zero and no cost, which the ranking treats as fastest and cheapest. Its quality and reliability scores will be 0, but check `PassRate` and each run's `TestResult.Error` in `StochasticResult.IndividualResults` before trusting its rank.
- **Weights are not checked.** `ModelComparer` does not call `ScoringWeights.Validate()`. Weights that do not sum to 1 change the scale of `CompositeScore`, which is then no longer 0–100. Call `Validate()` on your weights yourself.
- **Tokens may be estimated.** When the provider returns no token usage, the harness estimates tokens from the text (`PerformanceMetrics.TokensAreEstimated`), and a cost built on them is an estimate of an estimate.

---

## Comparison Options

```csharp
var options = new ModelComparisonOptions(
    RunsPerModel: 10,                                // runs per model, per test case
    ScoringWeights: ScoringWeights.QualityFocused,   // null means ScoringWeights.Default
    EnableCostAnalysis: true,                        // false: AverageCost and TotalCost are null
    EnableStatistics: true,                          // false: see below
    ConfidenceLevel: 0.95,                           // for the confidence interval on the mean score
    MaxParallelism: 1,                               // runs of one model at the same time
    DelayBetweenRuns: TimeSpan.FromMilliseconds(500) // pause between runs, for rate limits
);
```

Presets: `ModelComparisonOptions.Quick` (3 runs per model), `ModelComparisonOptions.Default` (5) and `ModelComparisonOptions.Thorough` (10).

- **At least 3 runs.** `ModelComparisonOptions.Validate()` accepts 1 or 2, but the stochastic runner rejects fewer than 3 runs with an `ArgumentOutOfRangeException`, so `RunsPerModel: 2` fails as soon as the comparison starts.
- **Parallelism.** Models run one after another. `MaxParallelism` runs that many runs of the same model at the same time, which shortens the comparison and raises the load on the API.
- **`EnableStatistics: false`.** The runner skips the statistics calculator: `StandardDeviation` and the percentiles are reported as 0 and `ConfidenceInterval` as `null`. A 0 there means "not computed", not "perfectly consistent". The ranking is unaffected, because reliability uses the pass rate.
- **Pass threshold.** `ModelComparer` runs each model with the default stochastic success threshold of 0.8, which sets `StochasticResult.Passed`. It does not affect the ranking.

---

## Scoring Weights

| Profile | Quality | Speed | Cost | Reliability |
|---------|---------|-------|------|-------------|
| `ScoringWeights.Default` | 0.40 | 0.20 | 0.20 | 0.20 |
| `ScoringWeights.QualityFocused` | 0.60 | 0.15 | 0.10 | 0.15 |
| `ScoringWeights.SpeedFocused` | 0.25 | 0.50 | 0.10 | 0.15 |
| `ScoringWeights.CostFocused` | 0.25 | 0.10 | 0.50 | 0.15 |
| `ScoringWeights.ReliabilityFocused` | 0.30 | 0.15 | 0.15 | 0.40 |

Your own weights:

```csharp
// Cost weight 0: the runs are not priced per model, so cost would not discriminate anyway
var weights = new ScoringWeights(Quality: 0.5, Speed: 0.3, Cost: 0.0, Reliability: 0.2);
weights.Validate();   // throws unless every weight is non-negative and they sum to 1.0

var result = await comparer.CompareModelsAsync(factories, testCase,
    new ModelComparisonOptions(RunsPerModel: 10, ScoringWeights: weights));

Console.WriteLine($"Best for your weights: {result.Winner.ModelName}");
```

---

## Visual Outputs

### Text and Markdown

```csharp
Console.WriteLine(result.Summary);                   // ranking with every dimension score, and the winner

string report = result.ToMarkdown();                 // header, winner, rankings, raw metrics, statistics, weights
string brief = result.ToMarkdown(MarkdownExportOptions.Minimal);
string rankings = result.ToRankingsTable();          // composite and the four dimension scores
string metrics = result.ToDetailedMetricsTable();    // pass rate, mean score, latency, average and total cost
string stats = result.ToStatisticsTable();           // mean, median, SD, min, max, P25/P75/P95, confidence intervals
string comment = result.ToGitHubComment();           // winner plus a collapsible rankings table
await result.SaveToMarkdownAsync("model-comparison.md");

// Several test cases: wins per model, average scores, and a rankings table per test case
string suiteReport = results.ToMarkdown();
string suiteComment = results.ToGitHubComment();
await results.SaveToMarkdownAsync("model-comparison-suite.md");
```

The detailed metrics table prints `N/A` for a cost that was not measured. Markdown reports end with a UTC timestamp unless `MarkdownExportOptions.IncludeTimestamp` is `false`.

### Console Table

`PrintComparisonTable()` (in `AgentEval.Output`) prints the runs of each model side by side: pass rate, mean score, duration and its spread, time to first token, tokens, cost, tool success rate, and the mean of every metric the runs recorded.

```csharp
using AgentEval.Output;

result.ModelResults
    .Select(m => (ModelName: m.ModelName, Result: m.StochasticResult))
    .ToList()
    .PrintComparisonTable();
```

### JSON

There is no JSON exporter for `ModelComparisonResult`.

---

## Advanced Patterns

### Weighted Comparison

See [Scoring Weights](#scoring-weights). `Winner` reflects the weights you pass; the raw numbers in `ModelResults` do not change with them.

### Per-Test-Case Analysis

```csharp
foreach (var r in results)
{
    Console.WriteLine($"{r.TestCase.Name}: winner {r.Winner.ModelName}");

    foreach (var model in r.ModelResults)
    {
        Console.WriteLine($"  {model.ModelName}: mean score {model.MeanScore:F1}, pass rate {model.PassRate:P0}");
    }
}
```

### Finding the Right Model per Use Case

```csharp
// Two groups of your own test cases
var simpleComparison = await comparer.CompareModelsAsync(factories, simpleQueries, options);
var complexComparison = await comparer.CompareModelsAsync(factories, complexFlows, options);

Console.WriteLine("Simple queries:\n" + simpleComparison.ToMarkdown(MarkdownExportOptions.Minimal));
Console.WriteLine("Complex flows:\n" + complexComparison.ToMarkdown(MarkdownExportOptions.Minimal));
```

---

## Cost-Quality Tradeoffs

The built-in cost dimension prices every model at one rate ([see above](#what-the-ranking-does-not-tell-you)). To weigh quality against price, price each model's own token usage at its own rate with `ModelPricing.EstimateCost`:

```csharp
using AgentEval.Models;

// ModelId must match a ModelPricing entry (for example "gpt-4o" or "gpt-4o-mini");
// otherwise, or when the runs recorded no tokens, the cost stays null: not measured.
var priced = result.ModelResults
    .Select(m => (
        Model: m,
        CostPerRun: m.StochasticResult.PromptTokenStats is { } prompt
                    && m.StochasticResult.CompletionTokenStats is { } completion
            ? ModelPricing.EstimateCost(m.ModelId, (int)prompt.Mean, (int)completion.Mean)
            : null))
    .ToList();

foreach (var (model, costPerRun) in priced)
{
    Console.WriteLine($"{model.ModelName}: mean score {model.MeanScore:F1}, " +
                      $"cost/run {(costPerRun is { } cost ? $"${cost:F4}" : "not measured")}");
}
```

### Budget-Constrained Selection

```csharp
decimal maxCostPerRun = 0.01m;

var withinBudget = priced
    .Where(p => p.CostPerRun is { } cost && cost <= maxCostPerRun)
    .OrderByDescending(p => p.Model.MeanScore)
    .FirstOrDefault();

Console.WriteLine(withinBudget.Model is null
    ? $"No priced model costs ${maxCostPerRun} or less per run"
    : $"Best model within ${maxCostPerRun}/run: {withinBudget.Model.ModelName}");
```

### Quality-Constrained Selection

```csharp
double minScore = 85.0;

var meetsQuality = priced
    .Where(p => p.Model.MeanScore >= minScore && p.CostPerRun is not null)
    .OrderBy(p => p.CostPerRun)
    .FirstOrDefault();

Console.WriteLine(meetsQuality.Model is null
    ? $"No priced model has a mean score of {minScore} or more"
    : $"Cheapest model with a mean score of {minScore} or more: {meetsQuality.Model.ModelName}");
```

---

## CI/CD Integration

### Fail the Build When a Model Drops Below Your Bar

```csharp
var results = await comparer.CompareModelsAsync(factories, testCases, options);

await results.SaveToMarkdownAsync("comparison-results.md");
await File.WriteAllTextAsync("comparison-comment.md", results.ToGitHubComment());

// The model you ship must keep passing every test case
const string shippedModel = "gpt-4o-mini";
var regressions = results
    .SelectMany(r => r.ModelResults
        .Where(m => m.ModelId == shippedModel && m.PassRate < 0.8)
        .Select(m => $"{r.TestCase.Name}: pass rate {m.PassRate:P0}"))
    .ToList();

if (regressions.Count > 0)
{
    Console.Error.WriteLine("Below the 80% pass-rate bar:\n  " + string.Join("\n  ", regressions));
    Environment.ExitCode = 1;
}
```

Model comparison results have no saved-baseline format. To track a trend across builds, keep the numbers you gate on (pass rate, mean score) in a file of your own.

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

      # tests/ModelComparison stands for your own console project running the code above
      - name: Run Model Comparison
        env:
          AZURE_OPENAI_ENDPOINT: ${{ secrets.AZURE_OPENAI_ENDPOINT }}
          AZURE_OPENAI_API_KEY: ${{ secrets.AZURE_OPENAI_API_KEY }}
        run: dotnet run --project tests/ModelComparison

      - name: Upload Comparison Results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: model-comparison
          path: |
            comparison-results.md
            comparison-comment.md

      - name: Comment on PR
        if: always() && github.event_name == 'pull_request'
        uses: actions/github-script@v7
        with:
          script: |
            const fs = require('fs');
            github.rest.issues.createComment({
              issue_number: context.issue.number,
              owner: context.repo.owner,
              repo: context.repo.repo,
              body: fs.readFileSync('comparison-comment.md', 'utf8')
            });
```

---

## Model Comparison with Trace Replay

Record each model's replies once, then re-grade them without calling the models again:

```csharp
using AgentEval.Tracing;

// RECORD: one live run per model and test case
foreach (var factory in factories)
{
    for (var i = 0; i < testCases.Length; i++)
    {
        await using var recorder = new TraceRecordingAgent(factory.CreateAgent(), $"{factory.ModelId}-{i}");
        await recorder.InvokeAsync(testCases[i].Input);
        await recorder.SaveAsync($"traces/{factory.ModelId}/{i}.trace.json");
    }
}

// RE-GRADE: replay the recorded replies through the harness; the models are not called
foreach (var factory in factories)
{
    for (var i = 0; i < testCases.Length; i++)
    {
        var replayer = await TraceReplayingAgent.FromFileAsync($"traces/{factory.ModelId}/{i}.trace.json");
        var replayed = await harness.RunEvaluationAsync(replayer, testCases[i]);
        Console.WriteLine($"{factory.ModelName} / {testCases[i].Name}: score {replayed.Score}, passed {replayed.Passed}");
    }
}
```

A replay returns the recorded reply at once and the same reply every time, so it re-checks grading and assertions, not speed or run-to-run variation. A judge, when the harness has one, is still called. Compare speed and reliability with live runs.

---

## Common Comparison Scenarios

### Scenario 1: Upgrade Evaluation

*Should we move from GPT-4 to GPT-4o?*

```csharp
var results = await comparer.CompareModelsAsync(
    new IAgentFactory[] { gpt4Factory, gpt4oFactory }, testCases,
    new ModelComparisonOptions(RunsPerModel: 20));

foreach (var r in results)
{
    var before = r.ModelResults.Single(m => m.ModelId == "gpt-4");
    var after = r.ModelResults.Single(m => m.ModelId == "gpt-4o");

    Console.WriteLine($"{r.TestCase.Name}:");
    Console.WriteLine($"  Mean score: {before.MeanScore:F1} -> {after.MeanScore:F1}");
    Console.WriteLine($"  Pass rate:  {before.PassRate:P0} -> {after.PassRate:P0}");
    Console.WriteLine($"  Latency:    {before.AverageLatency.TotalSeconds:F2}s -> {after.AverageLatency.TotalSeconds:F2}s");
}
```

### Scenario 2: Cost Reduction

*Can we use a cheaper model without losing quality?*

```csharp
var result = await comparer.CompareModelsAsync(
    new IAgentFactory[] { gpt4oFactory, gpt4oMiniFactory }, testCase,
    new ModelComparisonOptions(RunsPerModel: 10));

var expensive = result.ModelResults.Single(m => m.ModelId == "gpt-4o");
var cheap = result.ModelResults.Single(m => m.ModelId == "gpt-4o-mini");

Console.WriteLine($"Mean score drop: {expensive.MeanScore - cheap.MeanScore:F1} points");
Console.WriteLine($"Pass rate:       {expensive.PassRate:P0} -> {cheap.PassRate:P0}");
// For the price side, price each model's tokens at its own rate (see Cost-Quality Tradeoffs)
```

### Scenario 3: Multi-Provider Evaluation

*Which provider fits best?*

```csharp
// openAiClient, anthropicClient and geminiClient are IChatClient instances for each provider
var factories = new IAgentFactory[]
{
    new DelegateAgentFactory("gpt-4o", "GPT-4o", () => openAiClient.AsEvaluableAgent(name: "Agent")),
    new DelegateAgentFactory("claude-3-5-sonnet", "Claude 3.5 Sonnet", () => anthropicClient.AsEvaluableAgent(name: "Agent")),
    new DelegateAgentFactory("gemini-1.5-pro", "Gemini 1.5 Pro", () => geminiClient.AsEvaluableAgent(name: "Agent"))
};

var results = await comparer.CompareModelsAsync(factories, testCases, options);
Console.WriteLine(results.ToMarkdown());
```

---

## Best Practices

### 1. Use Representative Test Cases

```csharp
// ✅ Good: a mix of difficulty levels, each with criteria a judge can grade
var testCases = new[]
{
    new TestCase { Name = "Simple", Input = "What's 2+2?", ExpectedOutputContains = "4" },
    new TestCase { Name = "Medium", Input = "Summarize this article: ...", EvaluationCriteria = ["Covers the three main points"] },
    new TestCase { Name = "Complex", Input = "Plan a 3-city trip within a $2,000 budget", EvaluationCriteria = ["Stays within budget", "Covers all three cities"] }
};

// ❌ Bad: only easy cases, and nothing for quality to be judged on (any non-empty reply scores 100)
var easyCases = new[]
{
    new TestCase { Name = "Easy1", Input = "Hello" },
    new TestCase { Name = "Easy2", Input = "Hi there" }
};
```

### 2. Run Enough Iterations

```csharp
// ✅ Good: 10+ runs, so the statistics and confidence intervals mean something
var enough = new ModelComparisonOptions(RunsPerModel: 10);

// ❌ Fails once the comparison starts: the stochastic runner needs at least 3 runs
//    and throws ArgumentOutOfRangeException
var tooFew = new ModelComparisonOptions(RunsPerModel: 2);
```

### 3. Control for External Factors

```csharp
// Models already run one after another; space the runs out to stay under rate limits
var options = new ModelComparisonOptions(
    RunsPerModel: 10,
    MaxParallelism: 1,
    DelayBetweenRuns: TimeSpan.FromSeconds(1));
```

Run the models you compare close together in time, so that API load affects them alike.

### 4. Weight What You Measured

```csharp
// Weight what matters to you, and leave out what the comparison did not measure
var weights = new ScoringWeights(Quality: 0.6, Speed: 0.2, Cost: 0.0, Reliability: 0.2);
weights.Validate();
```

---

## Summary

| Question | Where to look |
|----------|---------------|
| Which model ranked first? | `result.Winner` |
| How did each model score on each dimension? | `result.Ranking` |
| What were the raw numbers? | `result.ModelResults`: pass rate, mean score, SD, latency, cost |
| Is a difference real or noise? | `model.StochasticResult.Statistics.ConfidenceInterval`, and enough runs |
| Which model won across a suite? | `results.ToMarkdown()`: wins and average scores |
| What does each model cost? | Its own tokens at its own rate: `ModelPricing.EstimateCost` |

---

## Next Steps

- [Stochastic evaluation](stochastic-evaluation.md) - The foundation for model comparison
- [Trace Record & Replay](tracing.md) - Record replies once, re-grade them for free
- [Code Gallery](showcase/code-gallery.md) - More examples
- [Sample D3](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/PerformanceAndStatistics/03_ModelComparison.cs) - Runnable model comparison example
- [Sample D4](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/PerformanceAndStatistics/04_CombinedStochasticComparison.cs) - Combined stochastic + model comparison

---

<div align="center">

**Make data-driven model decisions.**

[Get Started →](getting-started.md){ .md-button .md-button--primary }

</div>
