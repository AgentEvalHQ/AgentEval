// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Aggregation strategy for multi-judge consensus: computes the weighted median of judge scores
/// and takes the max severity across all non-skipped judges.
/// Weighted median algorithm: sort by score ascending, walk cumulative weight, return the score
/// at the point where cumulative weight first reaches or exceeds 50% of total weight.
/// </summary>
public sealed class WeightedMedianAggregation : IAggregationStrategy
{
    /// <summary>Shared singleton instance.</summary>
    public static IAggregationStrategy Instance { get; } = new WeightedMedianAggregation();

    /// <inheritdoc/>
    public string Name => "WeightedMedian";

    /// <inheritdoc/>
    public (double Score, string Severity) Aggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(components);
        if (results.Count != components.Count)
            throw new InvalidOperationException("Results and components must align 1:1.");

        // 17: exclude "error" leaves (transient provider failure, severity "none" by construction) the same
        // way WeightedSumAggregation does — neither "skipped" nor "error" is a real quality signal, and
        // including an "error" leaf's placeholder score would incorrectly drag the median down.
        var pairs = Enumerable.Range(0, results.Count)
            .Where(i => results[i].Score.CountsTowardAggregate() && components[i].Weight > 0)
            .Select(i => (Score: results[i].Score.Value, Weight: components[i].Weight))
            .OrderBy(p => p.Score)
            .ToList();

        if (pairs.Count == 0) return (0, "none");

        var totalWeight = pairs.Sum(p => p.Weight);
        var halfWeight = totalWeight / 2.0;
        double cumulative = 0;
        double median = pairs[0].Score;
        foreach (var (score, weight) in pairs)
        {
            cumulative += weight;
            if (cumulative >= halfWeight)
            {
                median = score;
                break;
            }
        }

        var severity = SeverityRollup.Max(
            results.Where(r => r.Score.CountsTowardAggregate()).Select(r => r.Score.Severity));

        return (median, severity);
    }
}
