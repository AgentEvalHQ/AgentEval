# Code Gallery

> **The code you've been dreaming of.** Real examples of AgentEval in action.

---

## Model Comparison with Recommendations

Compare models across your evaluation suite and get actionable recommendations:

```csharp
var stochasticRunner = new StochasticRunner(harness);
var comparer = new ModelComparer(stochasticRunner);

// CreateAgent(deployment) is your code: it returns an IEvaluableAgent for that model
var results = await comparer.CompareModelsAsync(
    factories: new IAgentFactory[]
    {
        new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
        new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini")),
        new DelegateAgentFactory("gpt-35-turbo", "GPT-3.5 Turbo", () => CreateAgent("gpt-35-turbo"))
    },
    testCases: agenticTestSuite,
    options: new ModelComparisonOptions(RunsPerModel: 5));

Console.WriteLine(results.ToMarkdown());
```

`ToMarkdown()` on the list of results writes how many test cases each model won, each model's average composite, quality, speed, cost and reliability scores, and a rankings table for every test case. The scores run from 0 to 100 *relative to the models compared*: the best model on a dimension gets 100 and the worst 0. Cost needs care: the harness prices every model at the rate of one model name, `EvaluationOptions.ModelName`, and with none set (as here) it prices nothing, so every model gets the same cost score. See [How the Scores Are Computed](../model-comparison.md#how-the-scores-are-computed).

---

## stochastic evaluation with Statistics

LLMs are non-deterministic. Run evaluations multiple times and analyze statistics:

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions(
        Runs: 20,                     // Run 20 times
        SuccessRateThreshold: 0.85)); // 85% of runs must pass

// What the statistics mean:
// - MeanScore: average score across all runs (higher = better quality)
// - StandardDeviation: how much scores vary (lower = more consistent)
// - PassRate: fraction of runs that passed
var stats = result.Statistics;
Console.WriteLine($"Mean score: {stats.MeanScore:F1}");
Console.WriteLine($"Std dev:    {stats.StandardDeviation:F1}");
Console.WriteLine($"Pass rate:  {stats.PassRate:P0}");
Console.WriteLine(result.Summary);   // e.g. ✅ PASSED: 18/20 runs passed (90.0% >= 85% threshold)

// Assert with statistical confidence
result.Should()
    .HavePassRateAtLeast(0.85)        // reliability
    .HaveMeanScoreAtLeast(80)         // avg quality
    .HaveStandardDeviationAtMost(10); // consistency
```

---

## Combined: Stochastic + Model Comparison

The most powerful pattern - compare models with statistical rigor:

```csharp
// Based on Sample D4 (04_CombinedStochasticComparison.cs)
var factories = new IAgentFactory[]
{
    new DelegateAgentFactory("gpt-4o", "GPT-4o", () => CreateAgent("gpt-4o")),
    new DelegateAgentFactory("gpt-4o-mini", "GPT-4o Mini", () => CreateAgent("gpt-4o-mini"))
};

var modelResults = new List<(string ModelName, StochasticResult Result)>();

foreach (var factory in factories)
{
    var result = await stochasticRunner.RunStochasticTestAsync(
        factory, testCase,
        new StochasticOptions(Runs: 5, SuccessRateThreshold: 0.8));
    modelResults.Add((factory.ModelName, result));
}

// Print comparison table
modelResults.PrintComparisonTable();
```

`PrintComparisonTable()` writes one console row per model: pass rate, mean score, mean duration and its spread, time to first token, tokens, cost, tool success rate, and the mean of every metric the runs recorded. `OutputOptions` turns columns on and off.

---

## Fluent Tool Chain Assertions

Assert on tool usage like you've always imagined:

```csharp
result.ToolUsage!.Should()
    .HaveCalledTool("SearchFlights", because: "must search before booking")
        .WithArgument("destination", "Paris")
        .WithDurationUnder(TimeSpan.FromSeconds(2))
    .And()
    .HaveCalledTool("BookFlight", because: "booking follows search")
        .AfterTool("SearchFlights")
        .WithArgument("flightId", "AF1234")
    .And()
    .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
    .HaveNoErrors();
```

---

## Behavioral Policy Guardrails

Compliance as code - enforce policies programmatically:

```csharp
result.ToolUsage!.Should()
    // PCI-DSS: Never expose card numbers
    .NeverPassArgumentMatching(@"\b\d{16}\b",
        because: "PCI-DSS prohibits raw card numbers in tool arguments")
    
    // Safety: Block dangerous operations
    .NeverCallTool("DeleteAllCustomers",
        because: "mass deletion requires manual approval")
    
    // GDPR: Require confirmation before processing personal data
    .MustConfirmBefore("ProcessPersonalData",
        because: "GDPR requires explicit consent",
        confirmationToolName: "VerifyUserConsent");
```

---

## Performance SLAs as Code

Make performance requirements executable:

```csharp
result.Performance!.Should()
    .HaveTotalDurationUnder(TimeSpan.FromSeconds(5), 
        because: "UX requires sub-5s responses")
    .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500),
        because: "streaming responsiveness matters")
    .HaveEstimatedCostUnder(0.05m, 
        because: "stay within $0.05/request budget")
    .HaveTokenCountUnder(2000);
```

A metric that was not captured cannot fail its check: time to first token is recorded only on streaming runs, and cost only when `EvaluationOptions.ModelName` names a model in the price table. Inside an `AgentEvalScope` such a check is recorded as inconclusive; outside one it is skipped.

---

## RAG Quality Metrics

Detect hallucinations and verify grounding:

```csharp
var context = new EvaluationContext
{
    Input = "What are the return policy terms?",
    Output = agentResponse,
    Context = retrievedDocuments,         // The RAG context
    GroundTruth = "30-day return policy"  // Optional reference
};

// judgeClient is the IChatClient that grades the answer
var faithfulness = await new FaithfulnessMetric(judgeClient).EvaluateAsync(context);
var relevance = await new RelevanceMetric(judgeClient).EvaluateAsync(context);

Console.WriteLine($"Faithfulness: {faithfulness.Score}/100");  // Is it grounded?
Console.WriteLine($"Relevance: {relevance.Score}/100");        // Does it answer the question?

// Detect hallucinations. Passed is also false when the judge's reply could not be parsed,
// so read Explanation before blaming the agent.
if (!faithfulness.Passed)
{
    throw new InvalidOperationException(
        $"Faithfulness {faithfulness.Score:F0}: {faithfulness.Explanation}");
}
```

---

## Trace Recording for Debugging

Record agent executions for debugging and reproduction:

```csharp
using AgentEval.Tracing;

// RECORD: capture a live execution
await using var recorder = new TraceRecordingAgent(realAgent, "booking-issue-123");
var response = await recorder.InvokeAsync("Book flight to Paris");

// Save for debugging/reproduction
await recorder.SaveAsync("debug-traces/booking-issue-123.trace.json");

// The trace contains, per call:
// - The prompt and the response text
// - Timing, and token usage when the provider reports it
// - Tool calls with their arguments and results
// - Error details if the call failed

// REPLAY: the same response again, with no model call
var replayer = await TraceReplayingAgent.FromFileAsync("debug-traces/booking-issue-123.trace.json");
var replayed = await replayer.InvokeAsync("Book flight to Paris");

// A replay re-checks your assertions and grading against a fixed response.
// It does not test the model again.
```

---

## Snapshot Evaluation

Detect regressions against a saved baseline:

```csharp
using System.Text.Json;
using AgentEval.Snapshots;

var store = new SnapshotStore("snapshots");
var comparer = new SnapshotComparer(new SnapshotOptions
{
    UseSemanticComparison = true,   // applies to fields named response, output, content, message, answer, text
    SemanticThreshold = 0.85        // word-overlap similarity of the two texts, not embeddings
});

var current = new
{
    response = result.ActualOutput,
    tools = result.ToolUsage?.Calls.Select(c => c.Name).ToArray()
};

if (!store.Exists("booking-flow"))
{
    // First run: save the baseline
    await store.SaveAsync("booking-flow", current);
}
else
{
    // Later runs: compare against the baseline
    var baseline = await store.LoadAsync<JsonElement>("booking-flow");
    var comparison = comparer.Compare(baseline.GetRawText(), JsonSerializer.Serialize(current));

    if (!comparison.IsMatch)
    {
        Console.WriteLine("⚠️ Regression detected!");
        foreach (var difference in comparison.Differences)
        {
            Console.WriteLine($"{difference.Path}: {difference.Message}");
        }
    }
}
```

---

## Multi-Turn Conversations

Test complete conversation flows:

```csharp
using AgentEval.Testing;

var testCase = ConversationalTestCase.Create("Flight booking")
    .AddUserTurn("I need to book a flight")
    .AddUserTurn("Paris, next Monday")
    .AddUserTurn("Book the first option")
    .ExpectTools("SearchFlights", "BookFlight")
    .WithMaxDuration(TimeSpan.FromMinutes(2))
    .Build();

// The agent keeps its own history across turns,
// for example chatClient.AsEvaluableAgent(includeHistory: true)
var runner = new ConversationRunner(agent);
var result = await runner.RunAsync(testCase);

// The runner checks that every expected tool was called, the duration limit,
// and that every user turn got a reply
foreach (var assertion in result.Assertions)
{
    Console.WriteLine($"{(assertion.Passed ? "pass" : "FAIL")} {assertion.Name} {assertion.Message}");
}

// Tool calls across the whole conversation, in call order
var search = result.ToolsCalled.IndexOf("SearchFlights");
var book = result.ToolsCalled.IndexOf("BookFlight");
Console.WriteLine(search >= 0 && book > search
    ? "Searched before booking"
    : "No search followed by a booking");
```

---

## See Also

- [stochastic evaluation Guide](../stochastic-evaluation.md) - Full statistical evaluation documentation
- [Model Comparison Guide](../model-comparison.md) - Comparing models in depth
- [Assertions Reference](../assertions.md) - Complete assertion API
- [Sample D4](https://github.com/AgentEvalHQ/AgentEval/blob/main/samples/AgentEval.Samples/PerformanceAndStatistics/04_CombinedStochasticComparison.cs) - Full working example
