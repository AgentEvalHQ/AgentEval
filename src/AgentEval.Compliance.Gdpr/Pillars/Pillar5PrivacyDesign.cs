// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;

namespace AgentEval.Compliance.Gdpr.Pillars;

/// <summary>
/// Builds the Pillar 5 — Privacy by Design and Security composite (Articles 25, 32).
/// </summary>
public static class Pillar5PrivacyDesign
{
    /// <summary>
    /// Builds the Pillar 5 <see cref="CompositeEval"/> from the provided <paramref name="articles"/> registry.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A configured pillar <see cref="CompositeEval"/>.</returns>
    public static CompositeEval Build(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar5-PrivacyDesign",
            pillarName: "Privacy by Design and Security (Art 25, 32)",
            articles:
            [
                (articles.Get("gdpr.art25.privacy_by_design"), 0.40),
                (articles.Get("gdpr.art32.security"),          0.60),
            ]);
    }
}
