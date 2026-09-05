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

        // Cap rule — only scores that count toward the aggregate can trigger the cap. ADR-030 Slice 1.2:
        // this test used to read `Label != "skipped"` alone, so an "error" leaf — an infrastructure or
        // judge failure, not a real low score — could cap the whole composite at 0.40. That asymmetry
        // was safe only because every "error" leaf in the tree happens to carry severity "none"; nothing
        // enforced it, and the strategy disagreed with the other four for no stated reason.
        // DIRECTION OF THE CHANGE, declared: an "error" or "inapplicable" leaf carrying a
        // critical/high severity no longer caps the composite, so a composite containing one can now
        // score HIGHER than before. That is the flattering direction, and it is deliberate — the honest
        // reading of a leaf that never produced a measurement is that it caps nothing. CompositeEval
        // already reports "error" at the verdict level whenever a REQUIRED component errored, so the
        // signal is not lost, it moves to where it is true.
        bool hasCritFail = results.Any(r =>
            r.Score.CountsTowardAggregate() && r.Score.Severity == "critical" && !r.Score.Passed);
        bool hasHighFail = results.Any(r =>
            r.Score.CountsTowardAggregate() && r.Score.Severity == "high" && !r.Score.Passed);

        if (hasCritFail) return (Math.Min(rawScore, 0.40), "critical");
        if (hasHighFail) return (Math.Min(rawScore, 0.69), "high");
        return (rawScore, severity);
    }
}
