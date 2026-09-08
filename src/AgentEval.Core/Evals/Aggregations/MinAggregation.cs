// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// "Any sub-fail fails the composite" aggregation strategy.
/// Score = minimum of non-skipped, non-error sub-scores; severity = maximum of non-skipped, non-error severities.
/// If all results are skipped or errored, returns (0, "none").
/// </summary>
public sealed class MinAggregation : IAggregationStrategy
{
    /// <summary>Shared singleton instance.</summary>
    public static IAggregationStrategy Instance { get; } = new MinAggregation();

    /// <inheritdoc/>
    public string Name => "Min";

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
    /// <c>grep -rn '\.Eval\b|\.Required\b' src/AgentEval.Core/Evals/Aggregations/ | grep -v '///'</c> returns 0 — so a
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

        // 17: exclude "error" leaves too (transient provider failure, severity "none" by construction), not
        // just "skipped" — a "min" strategy is maximally exposed to this: one error leaf's placeholder score
        // would otherwise floor the ENTIRE composite regardless of every other sub-result's real quality.
        var nonSkipped = results.Where(r => r.Score.CountsTowardAggregate()).ToList();
        if (nonSkipped.Count == 0) return (0, "none");

        var min = nonSkipped.Min(r => r.Score.Value);
        var severity = SeverityRollup.Max(nonSkipped.Select(r => r.Score.Severity));
        return (min, severity);
    }
}
