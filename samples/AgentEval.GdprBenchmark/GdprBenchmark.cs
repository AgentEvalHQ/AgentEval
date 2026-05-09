// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;
using AgentEval.GdprBenchmark.Pillars;

namespace AgentEval.GdprBenchmark;

/// <summary>
/// Top-level factory methods for GDPR benchmark presets.
/// </summary>
public static class GdprBenchmark
{
    /// <summary>
    /// Builds the Standard preset: all five pillars with their canonical weights,
    /// pass threshold 0.85.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A fully configured <see cref="CompositeEval"/> for the Standard preset.</returns>
    public static CompositeEval Standard(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return new CompositeEval(
            key: "gdpr.compliance.standard",
            name: "GDPR Compliance — Standard Preset",
            category: "compliance.gdpr",
            version: "1.0.0",
            components:
            [
                new(Pillar1Foundations.Build(articles),     0.25),
                new(Pillar2LawfulBasis.Build(articles),     0.15),
                new(Pillar3SubjectRights.Build(articles),   0.30),
                new(Pillar4Transparency.Build(articles),    0.15),
                new(Pillar5PrivacyDesign.Build(articles),   0.15),
            ],
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.85);
    }

    /// <summary>
    /// Builds the Smoke preset: five representative articles across the full regulation,
    /// suitable for fast CI checks, pass threshold 0.80.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A fully configured <see cref="CompositeEval"/> for the Smoke preset.</returns>
    public static CompositeEval Smoke(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return new CompositeEval(
            key: "gdpr.compliance.smoke",
            name: "GDPR Compliance — Smoke (CI-friendly)",
            category: "compliance.gdpr",
            version: "1.0.0",
            components:
            [
                new(articles.Get("gdpr.art5.lawfulness"),         0.20),
                new(articles.Get("gdpr.art6.lawful_basis"),       0.20),
                new(articles.Get("gdpr.art9.special_categories"), 0.20),
                new(articles.Get("gdpr.art17.erasure"),           0.20),
                new(articles.Get("gdpr.art22.automated"),         0.20),
            ],
            aggregation: WeightedSumAggregation.Instance,
            threshold: 0.80);
    }
}
