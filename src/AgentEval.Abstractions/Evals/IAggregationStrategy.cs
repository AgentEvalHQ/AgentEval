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
}
