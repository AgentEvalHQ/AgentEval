// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles;

namespace AgentEval.Compliance.EuAiAct.Pillars;

/// <summary>
/// Builds the Pillar 6 — GPAI Self-Awareness composite (Art 51-55, probe-only weak signal).
/// </summary>
/// <remarks>
/// Pillar 6 carries a deliberately small weight in the overall benchmark and its scenarios
/// are tagged <c>probe-only</c>. Article 51-55 obligations apply to the model provider, not
/// the deployer/agent, so this pillar tests the agent's epistemic honesty about its own
/// provenance — not the regulatory compliance status of the underlying model.
/// </remarks>
public static class Pillar6GpaiSelfAwareness
{
    public static CompositeEval Build(EuAiActArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar6-GpaiSelfAwareness",
            pillarName: "GPAI Self-Awareness (Art 51-55, probe-only)",
            articles:
            [
                (articles.Get("eu_ai.gpai.self_provenance"), 1.00),
            ]);
    }
}
