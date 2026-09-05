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
    public (double Score, string Severity) Aggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(components);
        if (results.Count != components.Count)
            throw new InvalidOperationException("Results and components must align 1:1.");

        double weightSum = 0, weightedScoreSum = 0;
        for (int i = 0; i < results.Count; i++)
        {
            // Exclude neutral infra leaves from weighted scoring:
            //   "skipped" — evaluator intentionally not run (bypass, timeout, circuit-breaker)
            //   "error"   — transient provider failure (network, quota, upstream bug)
            // Neither represents a real quality signal; including them at 0.0 would incorrectly
            // drag the composite below threshold (e.g. sample 13 uses threshold=0.75).
            if (!results[i].Score.CountsTowardAggregate()) continue;
            if (components[i].Weight <= 0) continue;
            weightSum        += components[i].Weight;
            weightedScoreSum += results[i].Score.Value * components[i].Weight;
        }

        var score = weightSum > 0 ? weightedScoreSum / weightSum : 0;
        var severity = SeverityRollup.Max(results
            .Where(r => r.Score.CountsTowardAggregate())
            .Select(r => r.Score.Severity));
        return (score, severity);
    }
}
