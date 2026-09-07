// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Strategy that combines a set of sub-eval results into a single score and severity.</summary>
public interface IAggregationStrategy
{
    /// <summary>Name of this strategy, e.g. "WeightedSum".</summary>
    string Name { get; }

    /// <summary>Aggregates <paramref name="results"/> weighted by their corresponding <paramref name="components"/>.</summary>
    (double Score, string Severity) Aggregate(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<EvalComponent> components);

    /// <summary>
    /// The same aggregation, over bare WEIGHTS — for a caller that has weights and no evals.
    /// </summary>
    /// <param name="results">The results to aggregate.</param>
    /// <param name="weights">One weight per result, aligned 1:1.</param>
    /// <returns>The aggregate score and its severity rollup.</returns>
    /// <remarks>
    /// <para>
    /// 🔴 <b>Reachability, not convenience.</b> Aggregation reads nothing from an
    /// <see cref="EvalComponent"/> except its <see cref="EvalComponent.Weight"/> — verified across
    /// all five shipped strategies (<c>grep '\.Eval|\.Required' src/AgentEval.Core/Evals/Aggregations/</c>
    /// returns 0). Before this member existed the weights-only path was a <c>public static</c> on
    /// each concrete strategy and therefore <b>unreachable through the interface</b>: a caller
    /// holding an <see cref="IAggregationStrategy"/> had to manufacture an
    /// <see cref="EvalComponent"/> per weight, and an <see cref="EvalComponent"/> demands an
    /// <see cref="IEval"/>. Four throwing <c>IEval</c> stubs existed for exactly that reason and were
    /// deleted; this is the member that keeps them deleted.
    /// </para>
    /// <para>
    /// Implementations must agree with <see cref="Aggregate"/> when the weights are the components'
    /// own — the shipped five satisfy that by having <see cref="Aggregate"/> forward here.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="results"/> and <paramref name="weights"/> differ in length.</exception>
    (double Score, string Severity) AggregateWeights(
        IReadOnlyList<EvalResult> results,
        IReadOnlyList<double> weights);
}
