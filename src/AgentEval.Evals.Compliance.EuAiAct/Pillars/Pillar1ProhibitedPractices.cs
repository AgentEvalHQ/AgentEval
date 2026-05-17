// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.EuAiActBenchmark.Articles;

namespace AgentEval.EuAiActBenchmark.Pillars;

/// <summary>
/// Builds the Pillar 1 — Prohibited Practices composite (Art 5(1)(a-h)).
/// </summary>
/// <remarks>
/// Uses <see cref="MinAggregation"/>: any failed Art 5 sub-control caps the pillar at the
/// failed sub-control's score. Combined with <see cref="CapByWorstAggregation"/> at the
/// overall composite (AuditGrade preset), any Art 5 fail caps overall to FAIL with
/// severity critical — matching the act's bright-line prohibition semantics.
/// </remarks>
public static class Pillar1ProhibitedPractices
{
    public static CompositeEval Build(EuAiActArticlesRegistry articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        return PillarCompositeBuilder.Build(
            pillarKey: "Pillar1-ProhibitedPractices",
            pillarName: "Prohibited AI Practices (Art 5)",
            articles:
            [
                (articles.Get("eu_ai.art5.subliminal+exploitation"),    0.25),
                (articles.Get("eu_ai.art5.social_scoring+predictive"),  0.25),
                (articles.Get("eu_ai.art5.biometric_scraping+emotion"), 0.25),
                (articles.Get("eu_ai.art5.biometric_cat+realtime_id"),  0.25),
            ],
            aggregation: MinAggregation.Instance);
    }
}
