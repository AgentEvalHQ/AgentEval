// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles.Models;

namespace AgentEval.GdprBenchmark.Articles.Building;

/// <summary>
/// Turns an <see cref="ArticleSpec"/> into a <see cref="CompositeEval"/>.
/// Aggregation strategy is selected from <see cref="ArticleMetadata.Aggregation"/>.
/// Phase 2 supports <c>weighted_sum</c>; unknown values fall back to
/// <see cref="WeightedSumAggregation"/> — Phase 5 (G5.*) wires additional strategies.
/// </summary>
public sealed class ArticleCompositeBuilder
{
    private readonly ScenarioToAtomicEval _scenarioBuilder;

    /// <summary>Initialises a new <see cref="ArticleCompositeBuilder"/>.</summary>
    /// <param name="scenarioBuilder">Builder used to convert individual scenarios.</param>
    public ArticleCompositeBuilder(ScenarioToAtomicEval scenarioBuilder)
    {
        _scenarioBuilder = scenarioBuilder ?? throw new ArgumentNullException(nameof(scenarioBuilder));
    }

    /// <summary>
    /// Builds a <see cref="CompositeEval"/> from all scenarios in <paramref name="article"/>.
    /// </summary>
    /// <param name="article">The article spec to convert.</param>
    /// <returns>A configured <see cref="CompositeEval"/>.</returns>
    public CompositeEval Build(ArticleSpec article)
    {
        ArgumentNullException.ThrowIfNull(article);

        var components = article.Scenarios
            .Select(s => new EvalComponent(
                Eval: _scenarioBuilder.Build(article.Metadata, s),
                Weight: s.Weight,
                Required: true))
            .ToList();

        // Phase 2 knows weighted_sum. "min" and "cap_by_worst" are wired in Phase 5.
        // For now, unknown aggregation values fall back to weighted_sum so all 21
        // articles build without errors. Art9 uses "min" which becomes a real strategy
        // in Phase 5.
        var aggregation = article.Metadata.Aggregation switch
        {
            "weighted_sum" => WeightedSumAggregation.Instance,
            _ => WeightedSumAggregation.Instance
        };

        return new CompositeEval(
            key: article.Metadata.ControlId,
            name: article.Metadata.Title,
            category: $"compliance.{article.Metadata.Pillar}",
            version: "1.0.0",
            components: components,
            aggregation: aggregation,
            threshold: article.Metadata.PassThreshold);
    }
}
