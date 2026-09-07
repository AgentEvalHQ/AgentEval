// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Aggregation strategy that computes a weighted average score across sub-eval results.</summary>
public sealed class WeightedSumAggregation : IAggregationStrategy
{
    /// <summary>Shared singleton instance.</summary>
    public static IAggregationStrategy Instance { get; } = new WeightedSumAggregation();

    /// <inheritdoc/>
    public string Name => "WeightedSum";

    /// <inheritdoc/>
    /// <remarks>Forwards to the static of the same name, so the interface and the direct call
    /// cannot diverge.</remarks>
    (double Score, string Severity) IAggregationStrategy.AggregateWeights(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<double> weights) => AggregateWeights(results, weights);

    /// <inheritdoc/>
    public (double Score, string Severity) Aggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(components);
        if (results.Count != components.Count)
            throw new InvalidOperationException("Results and components must align 1:1.");

        return AggregateWeights(results, components.Select(c => c.Weight).ToArray());
    }

    /// <summary>
    /// The weights-only entry point. Aggregation reads nothing from an <see cref="EvalComponent"/>
    /// except its <see cref="EvalComponent.Weight"/> — verified across all five strategies:
    /// <c>grep '\.Eval|\.Required' src/AgentEval.Core/Evals/Aggregations/</c> returns 0 — so a
    /// caller that has weights but no evals does not need a throwing <c>IEval</c> stub to carry them.
    /// Four such stubs existed only to satisfy the <see cref="EvalComponent"/> constructor.
    /// </summary>
    public static (double Score, string Severity) AggregateWeights(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(weights);
        if (results.Count != weights.Count)
            throw new InvalidOperationException("Results and weights must align 1:1.");

        double weightSum = 0, weightedScoreSum = 0;
        for (int i = 0; i < results.Count; i++)
        {
            // Exclude neutral infra leaves from weighted scoring:
            //   "skipped" — evaluator intentionally not run (bypass, timeout, circuit-breaker)
            //   "error"   — transient provider failure (network, quota, upstream bug)
            // Neither represents a real quality signal; including them at 0.0 would incorrectly
            // drag the composite below threshold (e.g. sample 13 uses threshold=0.75).
            if (!results[i].Score.CountsTowardAggregate()) continue;
            if (weights[i] <= 0) continue;
            weightSum        += weights[i];
            weightedScoreSum += results[i].Score.Value * weights[i];
        }

        var score = weightSum > 0 ? weightedScoreSum / weightSum : 0;
        var severity = SeverityRollup.Max(results
            .Where(r => r.Score.CountsTowardAggregate())
            .Select(r => r.Score.Severity));
        return (score, severity);
    }
}
