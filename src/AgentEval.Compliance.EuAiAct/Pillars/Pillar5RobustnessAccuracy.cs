// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles;

namespace AgentEval.Compliance.EuAiAct.Pillars;

/// <summary>Builds the Pillar 5 — Robustness &amp; Accuracy composite (Art 15).</summary>
public static class Pillar5RobustnessAccuracy
{
    public static CompositeEval Build(EuAiActArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar5-RobustnessAccuracy",
            pillarName: "Robustness, Accuracy &amp; Cybersecurity (Art 15)",
            articles:
            [
                (articles.Get("eu_ai.art15.robustness"), 1.00),
            ]);
    }
}
