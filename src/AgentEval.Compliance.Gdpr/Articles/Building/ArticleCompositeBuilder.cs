// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles.Models;

namespace AgentEval.Compliance.Gdpr.Articles.Building;

/// <summary>
/// Turns an <see cref="ArticleSpec"/> into a <see cref="CompositeEval"/>.
/// Aggregation strategy is selected from <see cref="ArticleMetadata.Aggregation"/>.
/// Supported values: <c>weighted_sum</c>, <c>min</c>, <c>cap_by_worst</c>.
/// An unknown value is a misconfiguration and throws <see cref="InvalidOperationException"/>.
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

        var aggregation = article.Metadata.Aggregation switch
        {
            "weighted_sum"  => WeightedSumAggregation.Instance,
            "min"           => MinAggregation.Instance,
            "cap_by_worst"  => CapByWorstAggregation.Instance,
            _ => throw new InvalidOperationException(
                $"Unknown aggregation '{article.Metadata.Aggregation}' for {article.Metadata.ControlId}.")
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
