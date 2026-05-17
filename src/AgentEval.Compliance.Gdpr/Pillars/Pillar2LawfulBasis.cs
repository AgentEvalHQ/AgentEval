// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;

namespace AgentEval.Compliance.Gdpr.Pillars;

/// <summary>
/// Builds the Pillar 2 — Lawful Basis composite (Articles 6, 7).
/// </summary>
public static class Pillar2LawfulBasis
{
    /// <summary>
    /// Builds the Pillar 2 <see cref="CompositeEval"/> from the provided <paramref name="articles"/> registry.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A configured pillar <see cref="CompositeEval"/>.</returns>
    public static CompositeEval Build(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar2-LawfulBasis",
            pillarName: "Lawful Basis for Processing (Art 6, 7)",
            articles:
            [
                (articles.Get("gdpr.art6.lawful_basis"), 0.55),
                (articles.Get("gdpr.art7.consent"),      0.45),
            ]);
    }
}
