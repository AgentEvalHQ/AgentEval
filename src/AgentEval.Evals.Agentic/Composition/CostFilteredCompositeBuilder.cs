// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Cost; // T3.1: EvaluatorCostMap relocated here.

namespace AgentEval.Evals.Agentic.Composition;

/// <summary>
/// Filters the components of a benchmark <see cref="CompositeEval"/> by per-evaluator cost
/// tier (per <see cref="EvaluatorCostMap"/>) and renormalizes the remaining weights to
/// sum to 1.0. Used by the <c>--budget-tier</c> CLI flag to skip expensive evaluators
/// when running on a tight budget.
/// </summary>
public static class CostFilteredCompositeBuilder
{
    /// <summary>
    /// Returns a new <see cref="CompositeEval"/> derived from <paramref name="composite"/>
    /// with components whose cost tier exceeds <paramref name="budget"/> removed and the
    /// remaining weights renormalized to sum to 1.0.
    /// </summary>
    /// <param name="composite">The source composite (typically the output of an <c>AgenticBenchmark</c> factory).</param>
    /// <param name="budget">
    /// Budget tier — evaluators with a cost tier &gt; this value are filtered out.
    /// Use <see cref="EvaluatorCostTier.High"/> or higher to keep all evaluators (no-op).
    /// </param>
    /// <returns>A new <see cref="CompositeEval"/> with filtered components and renormalized weights.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no components remain after filtering.</exception>
    public static CompositeEval FilterByBudget(CompositeEval composite, EvaluatorCostTier budget)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var filtered = composite.Components
            .Where(c => EvaluatorCostMap.IsWithinBudget(EvaluatorCostMap.GetTier(c.Eval.Key), budget))
            .ToList();

        if (filtered.Count == 0)
        {
            throw new InvalidOperationException(
                $"No evaluators remain in preset '{composite.Key}' after filtering by budget tier '{budget}'. " +
                $"Try a higher budget tier (e.g. 'high' or 'all').");
        }

        if (filtered.Count == composite.Components.Count)
        {
            // No-op — return original to preserve identity (and avoid extra GC churn).
            return composite;
        }

        var totalWeight = filtered.Sum(c => c.Weight);
        IReadOnlyList<EvalComponent> renormalized = totalWeight > 0
            ? filtered.Select(c => new EvalComponent(c.Eval, c.Weight / totalWeight, c.Required)).ToList()
            : filtered;

        return new CompositeEval(
            key: composite.Key,
            name: composite.Name,
            category: composite.Category,
            version: composite.Version,
            components: renormalized,
            aggregation: composite.Aggregation,
            threshold: composite.Threshold);
    }
}
