// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.GdprBenchmark.Pillars;

/// <summary>
/// Builds a pillar-level <see cref="CompositeEval"/> from a set of article composites and their weights.
/// </summary>
public static class PillarCompositeBuilder
{
    /// <summary>
    /// Builds a <see cref="CompositeEval"/> representing a single GDPR pillar.
    /// </summary>
    /// <param name="pillarKey">Short key identifying the pillar (e.g. <c>Pillar1-Foundations</c>).</param>
    /// <param name="pillarName">Human-readable pillar name.</param>
    /// <param name="articles">Article composites and their weights within the pillar. Weights must sum to 1.0.</param>
    /// <returns>A configured pillar <see cref="CompositeEval"/>.</returns>
    public static CompositeEval Build(
        string pillarKey,
        string pillarName,
        IReadOnlyList<(CompositeEval article, double weight)> articles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pillarKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(pillarName);
        ArgumentNullException.ThrowIfNull(articles);
        if (articles.Count == 0)
            throw new ArgumentException("Pillar must have at least one article.", nameof(articles));

        var components = articles
            .Select(a => new EvalComponent(a.article, a.weight, Required: true))
            .ToList();

        return new CompositeEval(
            key: pillarKey,
            name: pillarName,
            category: $"compliance.gdpr.{pillarKey}",
            version: "1.0.0",
            components: components,
            aggregation: WeightedSumAggregation.Instance,
            threshold: null);    // pillar-level threshold inherited from overall composite
    }
}
