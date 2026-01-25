# ADR-008: Calibrated Judge for Multi-Model LLM Evaluation

> **Status:** Accepted  
> **Created:** January 12, 2026  
> **Author:** AgentEval Team  
> **Supersedes:** None

---

## Context

LLM-as-judge evaluations are inherently non-deterministic. A single LLM judge may:
- Give inconsistent scores across runs (variance)
- Have systematic biases toward certain response styles
- Hallucinate or make evaluation errors

Single-judge evaluations limit reliability for enterprise use cases where audit trails and reproducibility matter.

---

## Decision

We implement a **CalibratedJudge** system that wraps multiple LLM judges to provide higher-confidence evaluations through voting, statistical analysis, and graceful degradation.

### Core Design Decisions

#### 1. Factory Pattern for Metric Instantiation

Each judge needs its own metric instance with its own `IChatClient`:

```csharp
// ✅ Factory pattern - each judge gets its own metric with its own client
var result = await judge.EvaluateAsync(context, 
    judgeName => new FaithfulnessMetric(judges[judgeName]));

// ❌ Shared metric - would reuse same client for all judges (wrong)
var result = await judge.EvaluateAsync(metric, context);
```

**Rationale:** Metrics like `FaithfulnessMetric` are stateful - they hold an `IChatClient` reference. To evaluate with multiple judges (GPT-4o, Claude, Gemini), each must have its own metric instance.

#### 2. Voting Strategies

We support four aggregation strategies via `VotingStrategy` enum:

| Strategy | Formula | Use Case |
|----------|---------|----------|
| `Median` | Middle value | Default - robust to outliers |
| `Mean` | Average | When all judges are equally reliable |
| `Unanimous` | Require consensus | High-stakes decisions |
| `Weighted` | Weighted average | When judges have known reliability scores |

**Rationale:** Different use cases need different aggregation. Median is default because it's robust against a single biased judge.

#### 3. Agreement Calculation

Agreement is calculated as the inverse of the coefficient of variation:

```
Agreement = 100 - (StdDev / Mean × 100)
```

This produces a 0-100% score where:
- 100% = All judges gave identical scores
- 0% = Maximum disagreement (StdDev equals Mean)

**Rationale:** Simple, intuitive metric that maps naturally to a percentage.

#### 4. Confidence Intervals

We use t-distribution approximation for small samples:

```csharp
var marginOfError = tValue × (stdDev / √n);
var lower = mean - marginOfError;
var upper = mean + marginOfError;
```

**Rationale:** With 2-5 judges (small n), t-distribution is more appropriate than z-distribution.

#### 5. Graceful Degradation

If one judge fails (timeout, error), evaluation continues with remaining judges:

```csharp
if (judgeScores.Count < options.MinimumJudgesRequired)
    throw new InvalidOperationException("Not enough judges succeeded");
```

**Rationale:** Enterprise systems need resilience. A single judge timeout shouldn't fail the entire evaluation.

#### 6. Parallel Execution with Limits

Judges run in parallel with configurable concurrency:

```csharp
var semaphore = new SemaphoreSlim(options.MaxParallelJudges);
var tasks = judges.Select(async judge => { ... });
var results = await Task.WhenAll(tasks);
```

**Rationale:** Parallel execution reduces latency; semaphore prevents overwhelming rate limits.

---

## Interface Design

### ICalibratedJudge

```csharp
public interface ICalibratedJudge
{
    IReadOnlyList<string> JudgeNames { get; }
    CalibratedJudgeOptions Options { get; }
    
    Task<CalibratedResult> EvaluateAsync(
        EvaluationContext context,
        Func<string, IMetric> metricFactory,
        CancellationToken cancellationToken = default);
    
    Task<CalibratedResult> EvaluateAsync<TMetric>(
        TMetric metric,
        EvaluationContext context,
        CancellationToken cancellationToken = default) where TMetric : IMetric;
}
```

### CalibratedResult

```csharp
public record CalibratedResult
{
    public required double Score { get; init; }
    public required double Agreement { get; init; }
    public required IReadOnlyDictionary<string, double> JudgeScores { get; init; }
    public double? ConfidenceLower { get; init; }
    public double? ConfidenceUpper { get; init; }
    public double StandardDeviation { get; init; }
    public VotingStrategy Strategy { get; init; }
    public bool HasConsensus { get; init; }
}
```

---

## Alternatives Considered

### A. Single Judge with Multiple Runs

Run the same judge N times and aggregate.

**Rejected:** Same biases would be amplified. Doesn't address systematic model biases.

### B. Judge Chain (Sequential)

Run judges sequentially, with later judges seeing earlier scores.

**Rejected:** Creates dependencies and ordering effects. Earlier judges influence later ones.

### C. Ensemble Weighting Based on Past Performance

Automatically learn judge weights from historical accuracy.

**Deferred:** Good idea for v2, but adds complexity. Manual weights via `JudgeWeights` option suffices for now.

---

## Consequences

### Positive

- **Higher reliability**: Multi-judge voting reduces variance
- **Audit trail**: `JudgeScores` dictionary shows exactly how each judge voted
- **Confidence quantification**: CI provides uncertainty bounds
- **Enterprise-ready**: Graceful degradation handles partial failures

### Negative

- **Increased cost**: 3× API calls for 3 judges
- **Increased latency**: Even with parallelism, overall time increases
- **Complexity**: Factory pattern is more complex than simple metric evaluation

### Mitigations

- **Cost**: Use `CalibratedJudge` only for high-stakes evaluations; use single judge for CI
- **Latency**: Parallel execution minimizes overhead
- **Complexity**: Provide both factory pattern and simplified `EvaluateAsync<TMetric>` overload

---

## File Locations

| File | Purpose |
|------|---------|
| `src/AgentEval/Calibration/CalibratedJudge.cs` | Main implementation |
| `src/AgentEval/Calibration/ICalibratedJudge.cs` | Interface |
| `src/AgentEval/Calibration/CalibratedResult.cs` | Result record |
| `src/AgentEval/Calibration/VotingStrategy.cs` | Enum |
| `src/AgentEval/Calibration/CalibratedJudgeOptions.cs` | Options |
| `tests/AgentEval.Tests/Calibration/CalibratedJudgeTests.cs` | Unit tests |

---

## Related ADRs

- [ADR-001: Metric Naming Prefixes](001-metric-naming-prefixes.md) - `llm_` prefix for judge-based metrics
- [ADR-005: Model Comparison & stochastic evaluation](005-model-comparison-stochastic.md) - Related multi-run testing
- [ADR-006: Service-Based Architecture & DI](006-service-based-architecture-di.md) - DI patterns

---

## References

- [Ensemble Methods in Machine Learning](https://en.wikipedia.org/wiki/Ensemble_learning)
