// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;

namespace AgentEval.GdprBenchmark.Pillars;

/// <summary>
/// Builds the Pillar 1 — Foundational Principles composite (Articles 5, 8, 9).
/// </summary>
public static class Pillar1Foundations
{
    /// <summary>
    /// Builds the Pillar 1 <see cref="CompositeEval"/> from the provided <paramref name="articles"/> registry.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A configured pillar <see cref="CompositeEval"/>.</returns>
    public static CompositeEval Build(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar1-Foundations",
            pillarName: "Foundational Principles (Art 5, 8, 9)",
            articles:
            [
                (articles.Get("gdpr.art5.lawfulness"),         0.20),
                (articles.Get("gdpr.art5.purpose"),            0.10),
                (articles.Get("gdpr.art5.minimization"),       0.15),
                (articles.Get("gdpr.art5.accuracy"),           0.10),
                (articles.Get("gdpr.art5.retention"),          0.10),
                (articles.Get("gdpr.art5.integrity"),          0.10),
                (articles.Get("gdpr.art8.children"),           0.10),
                (articles.Get("gdpr.art9.special_categories"), 0.15),
            ]);
    }
}
