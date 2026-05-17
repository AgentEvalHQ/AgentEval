// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Compliance.EuAiAct.Articles;

namespace AgentEval.Compliance.EuAiAct.Reporting;

/// <summary>
/// Walks the EU AI Act composite result tree and produces an <see cref="EuAiActSummary"/>
/// with per-pillar and per-article roll-ups.
/// </summary>
/// <remarks>
/// The expected tree shape is: L0 (overall) &gt; SubResults = 6 pillar EvalResults
/// &gt; SubResults = N article EvalResults &gt; SubResults = scenario EvalResults.
/// </remarks>
public sealed class SummaryBuilder
{
    private readonly EuAiActArticlesRegistry _articles;

    /// <summary>Initialises a new <see cref="SummaryBuilder"/>.</summary>
    /// <param name="articles">Registry used to look up article metadata.</param>
    public SummaryBuilder(EuAiActArticlesRegistry articles)
    {
        _articles = articles ?? throw new ArgumentNullException(nameof(articles));
    }

    /// <summary>
    /// Builds an <see cref="EuAiActSummary"/> from the composite result tree rooted at
    /// <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Handles both the Standard preset tree (L0→6 pillars→articles→scenarios) and the
    /// Smoke/flat preset tree (L0→articles→scenarios). The distinction is made by
    /// checking whether L1 keys are pillar keys (start with "Pillar") or article keys
    /// (start with "eu_ai.").
    /// </remarks>
    /// <param name="root">The top-level composite <see cref="EvalResult"/>.</param>
    /// <returns>A populated <see cref="EuAiActSummary"/>.</returns>
    public EuAiActSummary Build(EvalResult root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var perPillar = new Dictionary<string, EuAiActPillarSummary>();
        var perArticle = new Dictionary<string, EuAiActArticleSummary>();

        if (root.Details.SubResults is not { Count: > 0 } l1Children)
        {
            return new EuAiActSummary(
                OverallScore: root.Score.Value,
                OverallStatus: MapLabelToStatus(root.Score.Label),
                PerPillar: perPillar,
                PerArticle: perArticle);
        }

        // Determine the tree shape based on whether L1 nodes are pillars or articles.
        bool hasPillarLayer = l1Children.Any(c =>
            c.Metric.Key.StartsWith("Pillar", StringComparison.OrdinalIgnoreCase));

        if (hasPillarLayer)
        {
            // Standard 3-layer tree: root → pillars → articles → scenarios
            foreach (var pillar in l1Children)
            {
                var pillarKey = pillar.Metric.Key;
                var criticalFails = new List<string>();

                if (pillar.Details.SubResults is { Count: > 0 } articles)
                {
                    foreach (var article in articles)
                    {
                        var articleKey = article.Metric.Key;
                        var scenarios = article.Details.SubResults ?? Array.Empty<EvalResult>();
                        var failedCount = scenarios.Count(s => !s.Score.Passed);

                        var status = MapLabelToStatus(article.Score.Label);
                        if (article.Score.Severity == "critical" && status == "FAIL")
                            criticalFails.Add(articleKey);

                        perArticle[articleKey] = new EuAiActArticleSummary(
                            Score: article.Score.Value,
                            Status: status,
                            ScenarioCount: scenarios.Count,
                            ScenariosFailed: failedCount,
                            Severity: article.Score.Severity);
                    }
                }

                perPillar[pillarKey] = new EuAiActPillarSummary(
                    Score: pillar.Score.Value,
                    Status: MapLabelToStatus(pillar.Score.Label),
                    CriticalFails: criticalFails);
            }
        }
        else
        {
            // Flat 2-layer tree: root → articles → scenarios (e.g. Smoke preset)
            foreach (var article in l1Children)
            {
                var articleKey = article.Metric.Key;
                var scenarios = article.Details.SubResults ?? Array.Empty<EvalResult>();
                var failedCount = scenarios.Count(s => !s.Score.Passed);

                var status = MapLabelToStatus(article.Score.Label);

                perArticle[articleKey] = new EuAiActArticleSummary(
                    Score: article.Score.Value,
                    Status: status,
                    ScenarioCount: scenarios.Count,
                    ScenariosFailed: failedCount,
                    Severity: article.Score.Severity);
            }
            // No pillar layer — perPillar remains empty.
        }

        return new EuAiActSummary(
            OverallScore: root.Score.Value,
            OverallStatus: MapLabelToStatus(root.Score.Label),
            PerPillar: perPillar,
            PerArticle: perArticle);
    }

    private static string MapLabelToStatus(string label) => label.ToUpperInvariant() switch
    {
        "PASS" => "PASS",
        "WARN" => "WARN",
        _ => "FAIL"
    };
}
