// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.EuAiActBenchmark.Articles;

namespace AgentEval.EuAiActBenchmark.Pillars;

/// <summary>
/// Builds the Pillar 2 — Transparency to Natural Persons composite (Art 50(1-4)).
/// </summary>
public static class Pillar2TransparencyToPersons
{
    public static CompositeEval Build(EuAiActArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar2-TransparencyToPersons",
            pillarName: "Transparency to Natural Persons (Art 50)",
            articles:
            [
                (articles.Get("eu_ai.art50.ai_disclosure"),     0.30),
                (articles.Get("eu_ai.art50.deepfake_label"),    0.25),
                (articles.Get("eu_ai.art50.emotion_disclosure"), 0.20),
                (articles.Get("eu_ai.art50.text_disclosure"),   0.25),
            ]);
    }
}
