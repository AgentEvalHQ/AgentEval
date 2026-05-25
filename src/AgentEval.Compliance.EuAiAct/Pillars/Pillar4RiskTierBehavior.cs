// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles;

namespace AgentEval.Compliance.EuAiAct.Pillars;

/// <summary>Builds the Pillar 4 — Risk-Tier Behavior composite (Art 10 + Art 13 + Annex III).</summary>
/// <remarks>
/// Pillar 4 groups the EU AI Act articles that govern how the agent reasons about
/// the RISK TIER of a deployment context and the UPSTREAM data foundations that
/// determine whether a high-risk system can safely operate in that tier:
/// <list type="bullet">
///   <item><b>Annex III (Risk-Tier Recognition)</b> — whether the agent identifies
///         that a deployment falls into one of the listed high-risk categories.</item>
///   <item><b>Art 13 (Deployer Transparency)</b> — honest capability / limitation /
///         intended-purpose disclosure to the deployer.</item>
///   <item><b>Art 10 (Data Governance)</b> — the upstream training/validation/testing
///         data quality, representativeness, and bias-mitigation obligations that
///         feed the agent's behaviour in a given risk tier.</item>
/// </list>
/// <para>Weight rationale: Annex III recognition stays the largest sub-pillar (0.40)
/// because risk-tier mis-classification is the load-bearing failure mode that all
/// other obligations cascade from. Art 10 (data governance, 0.30) is the upstream
/// input that determines whether the high-risk classification can actually be
/// honoured. Art 13 (deployer transparency, 0.30) is reduced from 0.40 to make
/// room without disrupting the relative emphasis. Sum = 1.0; pillar-level weight
/// (0.10) inside <c>EuAiActBenchmark.Standard()</c> is unchanged.</para>
/// </remarks>
public static class Pillar4RiskTierBehavior
{
    public static CompositeEval Build(EuAiActArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar4-RiskTierBehavior",
            pillarName: "Risk-Tier Behavior (Art 10, Art 13, Annex III)",
            articles:
            [
                (articles.Get("eu_ai.art10.data_governance"),       0.30),
                (articles.Get("eu_ai.art13.deployer_transparency"), 0.30),
                (articles.Get("eu_ai.annex3.risk_tier_recognition"), 0.40),
            ]);
    }
}
