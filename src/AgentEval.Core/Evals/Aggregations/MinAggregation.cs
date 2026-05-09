// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// "Any sub-fail fails the composite" aggregation strategy.
/// Score = minimum of non-skipped sub-scores; severity = maximum of non-skipped severities.
/// If all results are skipped, returns (0, "none").
/// </summary>
public sealed class MinAggregation : IAggregationStrategy
{
    /// <summary>Shared singleton instance.</summary>
    public static IAggregationStrategy Instance { get; } = new MinAggregation();

    /// <inheritdoc/>
    public string Name => "Min";

    /// <inheritdoc/>
    public (double Score, string Severity) Aggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(components);
        if (results.Count != components.Count)
            throw new InvalidOperationException("Results and components must align 1:1.");

        var nonSkipped = results.Where(r => r.Score.Label != "skipped").ToList();
        if (nonSkipped.Count == 0) return (0, "none");

        var min = nonSkipped.Min(r => r.Score.Value);
        var severity = SeverityRollup.Max(nonSkipped.Select(r => r.Score.Severity));
        return (min, severity);
    }
}
