// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;

namespace AgentEval.GdprBenchmark.Pillars;

/// <summary>
/// Builds the Pillar 4 — Transparency composite (Articles 13, 14).
/// </summary>
public static class Pillar4Transparency
{
    /// <summary>
    /// Builds the Pillar 4 <see cref="CompositeEval"/> from the provided <paramref name="articles"/> registry.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A configured pillar <see cref="CompositeEval"/>.</returns>
    public static CompositeEval Build(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar4-Transparency",
            pillarName: "Transparency Obligations (Art 13, 14)",
            articles:
            [
                (articles.Get("gdpr.art13.information_collection"), 0.55),
                (articles.Get("gdpr.art14.information_indirect"),   0.45),
            ]);
    }
}
