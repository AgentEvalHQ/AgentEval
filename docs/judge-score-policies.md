# Judge score selection

`MAFEvaluationHarness` defaults to `JudgeScorePolicy.JudgeOverall`. Select `CriteriaMetFraction` to score the share of applicable declared criteria met, while retaining the judge's original overall score.

```csharp
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;

var testCase = new TestCase
{
    Name = "Swedish RAG",
    Input = "När lanseras Aurora?",
    JudgeCriteria =
    [
        new("support", "Alla faktapåståenden stöds av det hämtade underlaget."),
        new("relevance", "Svaret besvarar frågan."),
        new("citations", "Svaret innehåller korrekta källhänvisningar.")
    ],
    RequiresContext = true,
    PassingScore = 80
};

var options = new EvaluationOptions { ScorePolicy = JudgeScorePolicy.CriteriaMetFraction };
var harness = new MAFEvaluationHarness(judge);
var runner = new StochasticRunner(harness, evaluationOptions: options);
var run = await runner.RunStochasticTestAsync(agent, testCase,
    new StochasticOptions(Runs: 12, MaxParallelism: 3));
```

Here `judge` is your configured `IEvaluator` and `agent` is your `IEvaluableAgent`. The agent must attach its actual `RetrievedContext`; see [context capture](judge-context.md). Parallel runs require a thread-safe agent or an `IAgentFactory` that creates isolated agents. Swedish criterion text works with `ChatClientEvaluator`; this example does not localize the built-in system instructions.

Stable IDs are case-sensitive and independent of the criterion's wording. `ChatClientEvaluator` sends these IDs and requests them in the response. A custom evaluator must implement the `EvaluationRequest` overload and echo each ID in `CriterionResult.Id`. The legacy default interface implementation refuses to discard identities or evidence. Do not supply both `JudgeCriteria` and the older `EvaluationCriteria` string list.

The existing string list also supports fraction scoring. Verdicts must identify the declared text; AgentEval only removes its own rendered list ordinals and normalizes whitespace/case for that compatibility join. Duplicate or ambiguous declarations are rejected as unmeasured. Use stable IDs for translated or independently edited criteria.

## Measurement rules

The selected score is `100 × met / applicable`. Every expected criterion must have exactly one well-formed verdict. Missing, duplicate or unknown IDs, malformed booleans, contradictory JSON verdict sets, judge failure and an empty applicable denominator are unmeasured. An omitted criterion is never inferred to be inapplicable. Explicit `applicable: false` excludes a criterion from the denominator; the policy author should define when that is permitted.

No configured judge, no declared criteria, or `EvaluateResponse = false` cannot produce a criterion score from the mere presence of an answer. Failed measurements remain visible through `JudgeFailed` and `ScoreSelection.FailureCode`, and are excluded from score statistics. They still affect the stochastic verdict through the existing unmeasured-run bounds.

`TestResult.ScoreSelection` records the policy/version, raw overall score, exact selected score and criterion counts. `MeasuredScore` returns the exact selected score, or null when unmeasured. The older integer `Score` is a rounded display value: two of three criteria gives `Score = 67`, but `MeasuredScore = 66.666…` and fails a threshold of 67. Use `MeasuredScore` in downstream calculations.

Stochastic mean, median, percentiles, standard deviation and confidence intervals use the exact selected values. `ExactMinScore` and `ExactMaxScore` preserve fractional extremes; the older integer min/max fields remain for compatibility. Summary/report conversion and native AEF scores also retain precision. The AEF `agenteval.scoreSelection` extension records selection evidence without criterion text or retrieval passages.

Different selection policies cannot be pooled by `StochasticRunner`, `ModelComparer` or an AEF trial rollup. Nondefault selection changes a known judge instrument digest. An opaque custom judge still has an unknown prompt identity; selection evidence does not invent one.

This option selects harness scores. `GroundednessEval` is a separate composite instrument with its own [dimension weights and failure policy](groundedness-policies.md); this option does not change its aggregation.

Thanks to [@adner](https://github.com/adner) (Andreas Adner) for requesting criterion-based statistics and identifying the context gap.
