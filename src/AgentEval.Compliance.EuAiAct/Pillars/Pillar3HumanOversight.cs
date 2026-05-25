// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles;

namespace AgentEval.Compliance.EuAiAct.Pillars;

/// <summary>Builds the Pillar 3 — Human Oversight composite (Art 9 + Art 14).</summary>
/// <remarks>
/// Pillar 3 groups the EU AI Act articles that govern OPERATIONAL DISCIPLINE around
/// the agent-human interaction surface for high-risk systems:
/// <list type="bullet">
///   <item><b>Art 14 (Human Oversight)</b> — the agent must enable effective human
///         oversight, intervention, and override over decisions in high-risk contexts.</item>
///   <item><b>Art 9 (Risk-Management System)</b> — the iterative lifecycle of risk
///         identification, evaluation, mitigation, and post-market re-evaluation.
///         Sits alongside Art 14 because both are about live operational discipline
///         (as distinct from the upstream dataset / classification posture in Pillar 4).</item>
/// </list>
/// <para>Weight rationale: both are high-severity dialog-awareness probes and both
/// describe ongoing operational obligations rather than one-off attestations, so a
/// 50/50 split is the defensible default. The pillar-level weight (0.15) inside
/// <c>EuAiActBenchmark.Standard()</c> is unchanged by this rebalancing.</para>
/// </remarks>
public static class Pillar3HumanOversight
{
    public static CompositeEval Build(EuAiActArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar3-HumanOversight",
            pillarName: "Human Oversight (Art 9, Art 14)",
            articles:
            [
                (articles.Get("eu_ai.art9.risk_management"),  0.50),
                (articles.Get("eu_ai.art14.human_oversight"), 0.50),
            ]);
    }
}
