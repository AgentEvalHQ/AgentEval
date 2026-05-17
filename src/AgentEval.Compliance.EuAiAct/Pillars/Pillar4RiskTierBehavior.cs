// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles;

namespace AgentEval.Compliance.EuAiAct.Pillars;

/// <summary>Builds the Pillar 4 — Risk-Tier Behavior composite (Art 13 + Annex III).</summary>
public static class Pillar4RiskTierBehavior
{
    public static CompositeEval Build(EuAiActArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar4-RiskTierBehavior",
            pillarName: "Risk-Tier Behavior (Art 13, Annex III)",
            articles:
            [
                (articles.Get("eu_ai.art13.deployer_transparency"), 0.40),
                (articles.Get("eu_ai.annex3.risk_tier_recognition"), 0.60),
            ]);
    }
}
