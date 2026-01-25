# Code Gallery

> **The code you've been dreaming of.** Real examples of AgentEval in action.

---

## Model Comparison with Recommendations

Compare models across your test suite and get actionable recommendations:

```csharp
var comparer = new ModelComparer(harness);

var result = await comparer.CompareModelsAsync(
    factories: new IAgentFactory[]
    {
        new AzureModelFactory("gpt-4o", "GPT-4o"),
        new AzureModelFactory("gpt-4o-mini", "GPT-4o Mini"),  
        new AzureModelFactory("gpt-35-turbo", "GPT-3.5 Turbo")
    },
    testCases: agenticTestSuite,
    metrics: new[] { new ToolSuccessMetric(), new RelevanceMetric(evaluator) },
    options: new ComparisonOptions(RunsPerModel: 5));

// Get markdown table
Console.WriteLine(result.ToMarkdown());
```

**Output:**
```markdown
## Model Comparison Results

| Rank | Model         | Tool Accuracy | Relevance | Mean Latency | Cost/1K Req |
|------|---------------|---------------|-----------|--------------|-------------|
| 1    | GPT-4o        | 94.2%         | 91.5      | 1,234ms      | $0.0150     |
| 2    | GPT-4o Mini   | 87.5%         | 84.2      | 456ms        | $0.0003     |
| 3    | GPT-3.5 Turbo | 72.1%         | 68.9      | 312ms        | $0.0005     |

**Recommendation:** GPT-4o - Highest quality (94.2% tool accuracy)
**Best Value:** GPT-4o Mini - 87.5% accuracy at 50x lower cost
```

---

## stochastic evaluation with Statistics

LLMs are non-deterministic. Run tests multiple times and analyze statistics:

```csharp
var result = await stochasticRunner.RunStochasticTestAsync(
    agent, testCase,
    new StochasticOptions
    {
        Runs = 20,                    // Run 20 times
        SuccessRateThreshold = 0.85   // 85% must pass
    });

// What the statistics mean:
// - Mean: Average score across all runs (higher = better quality)
// - StandardDeviation: How much scores vary (lower = more consistent)
// - SuccessRate: % of runs that passed (score >= threshold)

Console.WriteLine($"Mean Score: {result.Statistics.Mean:F1}");          // e.g., 87.3
Console.WriteLine($"Std Dev: {result.Statistics.StandardDeviation:F1}"); // e.g., 5.2
Console.WriteLine($"Success Rate: {result.Statistics.PassRate:P0}");     // e.g., 90%

// Assert with statistical confidence
result.Statistics.Mean.Should().BeGreaterThan(80);
result.Statistics.StandardDeviation.Should().BeLessThan(15);  // Consistent behavior
Assert.True(result.PassedThreshold, $"Success rate {result.SuccessRate:P0} below 85%");
```

---

## Combined: Stochastic + Model Comparison

The most powerful pattern - compare models with statistical rigor:

```csharp
// Based on Sample16_CombinedStochasticComparison
var factories = new IAgentFactory[]
{
    new AzureModelFactory("gpt-4o", "GPT-4o"),
    new AzureModelFactory("gpt-4o-mini", "GPT-4o Mini")
};

var stochasticOptions = new StochasticOptions(
    Runs: 5,                         // 5 runs per model
    SuccessRateThreshold: 0.8,       // 80% must pass
    EnableStatisticalAnalysis: true
);

var modelResults = new List<(string ModelName, StochasticResult Result)>();

foreach (var factory in factories)
{
    var result = await stochasticRunner.RunStochasticTestAsync(
        factory, testCase, stochasticOptions);
    modelResults.Add((factory.ModelName, result));
}

// Print comparison table
modelResults.PrintComparisonTable();
```

**Output:**
```
┌──────────────────────────────────────────────────────────────────────────────┐
│                     Model Comparison (5 runs each)                           │
├──────────────┬─────────────┬────────────┬──────────┬────────────┬───────────┤
│ Model        │ Pass Rate   │ Mean Score │ Std Dev  │ Latency    │ Winner    │
├──────────────┼─────────────┼────────────┼──────────┼────────────┼───────────┤
│ GPT-4o       │ 100%        │ 92.4       │ 3.2      │ 1,456ms    │ 🏆 Quality│
│ GPT-4o Mini  │ 80%         │ 84.1       │ 8.7      │ 523ms      │ ⚡ Speed  │
└──────────────┴─────────────┴────────────┴──────────┴────────────┴───────────┘
```

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

var faithfulness = await new FaithfulnessMetric(evaluator).EvaluateAsync(context);
var relevance = await new RelevanceMetric(evaluator).EvaluateAsync(context);

Console.WriteLine($"Faithfulness: {faithfulness.Score}/100");  // Is it grounded?
Console.WriteLine($"Relevance: {relevance.Score}/100");        // Does it answer the question?

// Detect hallucinations
if (faithfulness.Score < 70)
{
    throw new HallucinationDetectedException(
        $"Response not grounded in context. Faithfulness: {faithfulness.Score}");
}
```

---

## Trace Recording for Debugging

Record agent executions for debugging and reproduction:

```csharp
// RECORD: Capture live execution for debugging
var recorder = new TraceRecordingAgent(realAgent);
var response = await recorder.ExecuteAsync("Book flight to Paris");
var trace = recorder.GetTrace();

// Save for debugging/reproduction
await TraceSerializer.SaveAsync(trace, "debug-traces/booking-issue-123.json");

// The trace contains:
// - Full tool call sequence with arguments
// - Timing information per step
// - Model responses
// - Error details if any failed

// Use for: Debugging, reproduction, step-by-step analysis
// NOT for: Running as automated tests (replaying doesn't prove anything)
```

---

## Snapshot Testing

Detect regressions with semantic similarity:

```csharp
var comparer = new SnapshotComparer(embeddingClient);

// Save baseline
await comparer.SaveBaselineAsync("booking-flow", result);

// Later: Compare against baseline
var comparison = await comparer.CompareAsync("booking-flow", newResult);

if (comparison.SimilarityScore < 0.85)
{
    Console.WriteLine($"⚠️ Regression detected!");
    Console.WriteLine($"Similarity: {comparison.SimilarityScore:P0}");
    Console.WriteLine($"Diff: {comparison.SemanticDiff}");
}
```

---

## Multi-Turn Conversations

Test complete conversation flows:

```csharp
var conversation = new ConversationRunner(harness);

await conversation.AddUserTurnAsync("I need to book a flight");
var turn1 = await conversation.GetLastResponseAsync();
turn1.Should().Contain("Where would you like to go?");

await conversation.AddUserTurnAsync("Paris, next Monday");
var turn2 = await conversation.GetLastResponseAsync();
turn2.ToolUsage!.Should().HaveCalledTool("SearchFlights");

await conversation.AddUserTurnAsync("Book the first option");
var turn3 = await conversation.GetLastResponseAsync();
turn3.ToolUsage!.Should()
    .HaveCalledTool("BookFlight")
    .AfterTool("SearchFlights");
```

---

## See Also

- [stochastic evaluation Guide](../stochastic-testing.md) - Full statistical testing documentation
- [Model Comparison Guide](../model-comparison.md) - Comparing models in depth
- [Assertions Reference](../assertions.md) - Complete assertion API
- [Sample 16](https://github.com/joslat/AgentEval/blob/main/samples/AgentEval.Samples/Sample16_CombinedStochasticComparison.cs) - Full working example

