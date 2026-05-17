// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;

namespace AgentEval.GdprBenchmark.Pillars;

/// <summary>
/// Builds the Pillar 3 — Data Subject Rights composite (Articles 15-18, 20-22).
/// </summary>
public static class Pillar3SubjectRights
{
    /// <summary>
    /// Builds the Pillar 3 <see cref="CompositeEval"/> from the provided <paramref name="articles"/> registry.
    /// </summary>
    /// <param name="articles">Registry containing all 21 GDPR article composites.</param>
    /// <returns>A configured pillar <see cref="CompositeEval"/>.</returns>
    public static CompositeEval Build(ArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar3-SubjectRights",
            pillarName: "Data Subject Rights (Art 15-18, 20-22)",
            articles:
            [
                (articles.Get("gdpr.art15.access"),         0.10),
                (articles.Get("gdpr.art16.rectification"),  0.05),
                (articles.Get("gdpr.art17.erasure"),        0.20),
                (articles.Get("gdpr.art18.restriction"),    0.10),
                (articles.Get("gdpr.art20.portability"),    0.10),
                (articles.Get("gdpr.art21.object"),         0.15),
                (articles.Get("gdpr.art22.automated"),      0.30),
            ]);
    }
}
