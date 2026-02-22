# LLM-as-a-Judge Evaluation

AgentEval provides comprehensive LLM-as-a-Judge capabilities across three subsystems: **individual LLM-backed metrics**, **calibrated multi-model consensus**, and **harness-level criteria evaluation**.

## Overview

LLM-as-a-Judge uses language models to evaluate AI agent outputs — replacing or supplementing heuristic scoring with nuanced, context-aware assessment. AgentEval implements this pattern at three levels:

| Level | Component | Use Case |
|-------|-----------|----------|
| **Metric** | `FaithfulnessMetric`, `RelevanceMetric`, etc. | Score individual quality dimensions |
| **Calibration** | `CalibratedJudge` | Multi-model consensus with statistical confidence |
| **Harness** | `ChatClientEvaluator` via `IEvaluator` | Criteria-based pass/fail within test harness |

## 1. Individual LLM-Backed Metrics

All metrics prefixed with `llm_` use an `IChatClient` as a judge. They send structured prompts to the LLM and parse JSON responses containing a score and explanation.

### Available Metrics

| Metric | Name | Category |
|--------|------|----------|
| `FaithfulnessMetric` | `llm_faithfulness` | RAG |
| `RelevanceMetric` | `llm_relevance` | RAG |
| `ContextPrecisionMetric` | `llm_context_precision` | RAG |
| `ContextRecallMetric` | `llm_context_recall` | RAG |
| `AnswerCorrectnessMetric` | `llm_answer_correctness` | RAG |
| `GroundednessMetric` | `llm_groundedness` | Safety |
| `CoherenceMetric` | `llm_coherence` | Safety |
| `FluencyMetric` | `llm_fluency` | Safety |
| `TaskCompletionMetric` | `llm_task_completion` | Agentic |
| `MisinformationMetric` | `llm_misinformation` | Responsible AI |
| `BiasMetric` | `llm_bias` | Responsible AI |

### Usage

```csharp
using AgentEval.Metrics.RAG;
using Microsoft.Extensions.AI;

// Create an LLM client
IChatClient chatClient = /* your Azure OpenAI / OpenAI client */;

// Use it as a judge
var faithfulness = new FaithfulnessMetric(chatClient);
var context = new EvaluationContext
{
    Input = "What is the capital of France?",
    Output = "The capital of France is Paris.",
    Context = "France is a country in Western Europe. Paris is its capital.",
    GroundTruth = "Paris"
};

var result = await faithfulness.EvaluateAsync(context);
Console.WriteLine($"Faithfulness: {result.Score}/100 — {result.Explanation}");
```

### Cost Considerations

| Prefix | Cost | Examples |
|--------|------|---------|
| `llm_` | API call per evaluation | Faithfulness, Relevance, Coherence |
| `code_` | Free (computed) | Tool success rate, tool efficiency |
| `embed_` | Embedding API call | Answer similarity |

Use `code_` metrics when possible for CI/CD pipelines. Reserve `llm_` metrics for quality gates and periodic audits.

## 2. Calibrated Multi-Model Consensus

When a single LLM judge isn't reliable enough, `CalibratedJudge` runs the same evaluation across multiple models and aggregates results with statistical analysis.

### Why Use Multiple Judges?

- **Reduce variance**: LLMs give different scores across runs
- **Cross-model consensus**: Different models catch different issues
- **Statistical confidence**: Agreement scores and confidence intervals quantify reliability
- **Graceful degradation**: If one judge fails, others continue

### Quick Start

```csharp
using AgentEval.Calibration;

// Create a calibrated judge with 3 models
var judge = CalibratedJudge.Create(
    ("GPT-4o", gpt4oClient),
    ("Claude", claudeClient),
    ("Gemini", geminiClient));

// Factory pattern — each judge gets its own metric with its own client
var result = await judge.EvaluateAsync(context, judgeName =>
    new FaithfulnessMetric(clients[judgeName]));

Console.WriteLine($"Score: {result.Score:F1}");
Console.WriteLine($"Agreement: {result.Agreement:F0}%");
Console.WriteLine($"95% CI: [{result.ConfidenceLower:F1}, {result.ConfidenceUpper:F1}]");
Console.WriteLine(result.JudgeBreakdown);
```

### Voting Strategies

| Strategy | Behavior | Best For |
|----------|----------|----------|
| **Median** (default) | Middle value; robust to outliers | General use |
| **Mean** | Average of all scores | Balanced weighting |
| **Unanimous** | Requires all judges within tolerance; throws if not | High-stakes decisions |
| **Weighted** | Each judge has a configurable weight | When you trust some models more |

```csharp
// Weighted voting — trust GPT-4o twice as much
var options = new CalibratedJudgeOptions
{
    Strategy = VotingStrategy.Weighted,
    JudgeWeights = new Dictionary<string, double>
    {
        ["GPT-4o"] = 2.0,
        ["Claude"] = 1.0,
        ["Gemini"] = 1.0
    }
};
var judge = CalibratedJudge.Create(options, judges);
```

### Configuration

| Option | Default | Description |
|--------|---------|-------------|
| `Strategy` | `Median` | Score aggregation method |
| `ConsensusTolerance` | `10.0` | Max spread for Unanimous strategy |
| `Timeout` | `120s` | Per-judge timeout |
| `CalculateConfidenceInterval` | `true` | Enable/disable CI calculation |
| `ConfidenceLevel` | `0.95` | CI confidence (0.90, 0.95, 0.99) |
| `MaxParallelJudges` | `3` | Parallelism limit |
| `ContinueOnJudgeFailure` | `true` | Continue or throw on judge failure |
| `MinimumJudgesRequired` | `1` | Minimum successful judges |

### Understanding Results

```csharp
CalibratedResult result = await judge.EvaluateAsync(...);

result.Score              // Final aggregated score (0-100)
result.Agreement          // Inter-judge agreement (0-100%)
result.HasConsensus       // All judges within ConsensusTolerance?
result.StandardDeviation  // Score spread across judges
result.ConfidenceLower    // Lower bound of confidence interval
result.ConfidenceUpper    // Upper bound of confidence interval
result.JudgeScores        // Dictionary<string, double> per-judge scores
result.JudgeCount         // Number of successful judges
result.Summary            // Formatted one-line summary
result.JudgeBreakdown     // Multi-line judge score listing
```

## 3. Harness-Level Criteria Evaluation

The `MAFEvaluationHarness` can use an `IChatClient` as a criteria-based judge via `ChatClientEvaluator`. This enables pass/fail evaluation against custom criteria defined per test case.

### IEvaluator Interface

```csharp
public interface IEvaluator
{
    Task<EvaluationResult> EvaluateAsync(
        string input, string output, IEnumerable<string> criteria,
        CancellationToken cancellationToken = default);
}
```

`EvaluationResult` contains:

| Property | Type | Description |
|----------|------|-------------|
| `OverallScore` | `int` | Score 0-100 |
| `Summary` | `string` | Evaluator's summary |
| `Improvements` | `IReadOnlyList<string>` | Suggested improvements |
| `CriteriaResults` | `IReadOnlyList<CriterionResult>` | Per-criterion `Met` + `Explanation` |

### ChatClientEvaluator (Default)

`ChatClientEvaluator` sends a structured system prompt requesting JSON evaluation. It handles:
- Criteria formatting as a numbered list
- JSON parsing with `LlmJsonParser.ExtractJson()`
- Graceful failure (returns `EvaluationDefaults.DefaultFailureScore` — currently 50 — on parse error)

```csharp
// Create directly
var evaluator = new ChatClientEvaluator(chatClient);

// Or let the harness create it for you
var harness = new MAFEvaluationHarness(chatClient);
```

The expected JSON response schema:

```json
{
    "criteriaResults": [
        {"criterion": "...", "met": true, "explanation": "..."}
    ],
    "overallScore": 75,
    "summary": "Brief summary",
    "improvements": ["suggestion 1", "suggestion 2"]
}
```

### How Pass/Fail Works

In `MAFEvaluationHarness.RunEvaluationAsync()`:

1. The harness calls `_evaluator.EvaluateAsync(input, output, criteria)`
2. `result.Passed = evaluation.OverallScore >= testCase.PassingScore` (default: 70)
3. Each `CriterionResult.Met` is stored on the `TestResult` for inspection
4. `EvaluateResponse = true` must be set in `EvaluationOptions`

```csharp
var testCase = new TestCase
{
    Input = "Book a flight to Paris",
    PassingScore = 70,
    EvaluationCriteria = new List<string>
    {
        "Response should confirm the booking",
        "Response should mention the destination city",
        "Response should provide a reference number"
    }
};

var options = new EvaluationOptions { EvaluateResponse = true };
var result = await harness.RunEvaluationAsync(agent, testCase, options);

foreach (var criterion in result.CriteriaResults)
{
    Console.WriteLine($"  {(criterion.Met ? "✅" : "❌")} {criterion.Criterion}");
    Console.WriteLine($"     {criterion.Explanation}");
}
```

### CalibratedEvaluator (Multi-Model)

For higher reliability, `CalibratedEvaluator` implements `IEvaluator` using multiple LLM judges with consensus aggregation. It is a **drop-in replacement** for `ChatClientEvaluator`:

```csharp
using AgentEval.Calibration;
using AgentEval.Models;

// Multi-model evaluator with 2 judges
var evaluator = new CalibratedEvaluator(
    new[] { ("GPT-4o", gpt4oClient), ("Claude", claudeClient) },
    new CalibratedJudgeOptions { Strategy = VotingStrategy.Median });

// Drop-in — same harness, same API
var harness = new MAFEvaluationHarness(evaluator);

var testCase = new TestCase
{
    Input = "Book a flight to Paris",
    PassingScore = 70,
    EvaluationCriteria = new List<string>
    {
        "Response should confirm the booking",
        "Response should mention the destination city"
    }
};

var options = new EvaluationOptions { EvaluateResponse = true };
var result = await harness.RunEvaluationAsync(agent, testCase, options);
```

**How aggregation works:**

| Aspect | Method |
|--------|--------|
| `OverallScore` | Aggregated via configured voting strategy (Median, Mean, Weighted, Unanimous) |
| `CriterionResult.Met` | Majority vote (>50% of judges say Met) |
| `Summary` | Enriched with agreement percentage and individual judge scores |
| `Improvements` | Union of all judges' suggestions (deduplicated) |

This combines the reliability of `CalibratedJudge` (multi-model consensus, graceful degradation) with the criteria-based evaluation interface that `MAFEvaluationHarness` expects.

## 4. Red Team LLM Judge

For security evaluation, `LLMJudgeEvaluator` assesses whether attack probes successfully bypassed agent guardrails. It implements `IProbeEvaluator` and is used as a fallback when deterministic evaluators return inconclusive.

### How It Works

1. Receives an `AttackProbe` (the attack) and the agent's `response`
2. Sends both to an LLM with a security-focused system prompt
3. Parses the LLM verdict: `Blocked`, `Bypassed`, `Partial`, or `Inconclusive`
4. Returns an `EvaluationResult` with verdict, confidence, and explanation

### Usage

```csharp
using AgentEval.RedTeam.Evaluators;

var evaluator = new LLMJudgeEvaluator(chatClient);
var verdict = await evaluator.EvaluateAsync(probe, agentResponse);
// Returns: Blocked, Bypassed, Partial, or Inconclusive
```

### Configuration with LLMJudgeOptions

| Option | Default | Description |
|--------|---------|-------------|
| `Temperature` | `0.0` | Lower = more deterministic verdicts |
| `MaxTokens` | `256` | Sufficient for structured verdict JSON |
| `CustomSystemPrompt` | `null` | Override the default security evaluator prompt |
| `CustomUserPromptTemplate` | `null` | Use `{probe}` and `{response}` placeholders |

```csharp
var options = new LLMJudgeOptions
{
    Temperature = 0.0f,
    MaxTokens = 512,
    CustomSystemPrompt = "You are a security evaluator. Assess if the response reveals sensitive information."
};
var evaluator = new LLMJudgeEvaluator(chatClient, options);
```

## Testing Without LLM Calls

Use `FakeChatClient` to unit test judge metrics without API calls:

```csharp
using AgentEval.Testing;

var fakeClient = new FakeChatClient("""{"score": 85, "explanation": "Good"}""");
var metric = new FaithfulnessMetric(fakeClient);
var result = await metric.EvaluateAsync(context);

Assert.Equal(85, result.Score);
```

For integration testing, use [Trace Record & Replay](tracing.md) to capture real evaluations and replay them deterministically.

## Samples

- **[Sample 05](../samples/AgentEval.Samples/Sample05_ComprehensiveRAG.cs)**: RAG quality metrics with LLM judges
- **[Sample 18](../samples/AgentEval.Samples/Sample18_JudgeCalibration.cs)**: Multi-model calibrated judge with all 4 voting strategies
- **[Sample 24](../samples/AgentEval.Samples/Sample24_CalibratedEvaluator.cs)**: CalibratedEvaluator — multi-model harness evaluation with criteria

## Related Documentation

- [Metrics Reference](metrics-reference.md) — Complete metric catalog
- [RAG Metrics](rag-metrics.md) — RAG-specific LLM metrics
- [Evaluation Guide](evaluation-guide.md) — "Evaluation Always Real" principle
- [Architecture](architecture.md) — Calibration layer design
- [ADR-008](adr/008-calibrated-judge-multi-model.md) — CalibratedJudge design decisions
