// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles;

namespace AgentEval.Compliance.EuAiAct.Pillars;

/// <summary>Builds the Pillar 3 — Human Oversight composite (Art 14).</summary>
public static class Pillar3HumanOversight
{
    public static CompositeEval Build(EuAiActArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar3-HumanOversight",
            pillarName: "Human Oversight (Art 14)",
            articles:
            [
                (articles.Get("eu_ai.art14.human_oversight"), 1.00),
            ]);
    }
}
