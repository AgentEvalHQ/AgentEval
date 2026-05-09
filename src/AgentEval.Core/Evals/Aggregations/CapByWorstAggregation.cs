// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Severity-aware cap on top of weighted-sum.
/// A sub-result with <c>severity=critical</c> AND <c>label=fail</c> caps the composite
/// score at 0.40 and forces severity to "critical".
/// A sub-result with <c>severity=high</c> AND <c>label=fail</c> (with no critical fail)
/// caps the composite score at 0.69 and forces severity to "high".
/// Skipped sub-results are treated as neither pass nor fail and do not trigger the cap.
/// </summary>
public sealed class CapByWorstAggregation : IAggregationStrategy
{
    /// <summary>Shared singleton instance.</summary>
    public static IAggregationStrategy Instance { get; } = new CapByWorstAggregation();

    /// <inheritdoc/>
    public string Name => "CapByWorst";

    /// <inheritdoc/>
    public (double Score, string Severity) Aggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(components);
        if (results.Count != components.Count)
            throw new InvalidOperationException("Results and components must align 1:1.");

        // Start from the standard weighted-sum
        var (rawScore, severity) = WeightedSumAggregation.Instance.Aggregate(results, components);

        // Cap rule — skipped results are neither pass nor fail; exclude them from cap evaluation.
        bool hasCritFail = results.Any(r =>
            r.Score.Label != "skipped" && r.Score.Severity == "critical" && !r.Score.Passed);
        bool hasHighFail = results.Any(r =>
            r.Score.Label != "skipped" && r.Score.Severity == "high" && !r.Score.Passed);

        if (hasCritFail) return (Math.Min(rawScore, 0.40), "critical");
        if (hasHighFail) return (Math.Min(rawScore, 0.69), "high");
        return (rawScore, severity);
    }
}
