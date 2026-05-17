// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.GdprBenchmark.Articles;

namespace AgentEval.GdprBenchmark.Reporting;

/// <summary>
/// Walks the GDPR composite result tree and produces a <see cref="GdprSummary"/>
/// with per-pillar and per-article roll-ups.
/// </summary>
/// <remarks>
/// The expected tree shape is: L0 (overall) &gt; SubResults = 5 pillar EvalResults
/// &gt; SubResults = N article EvalResults &gt; SubResults = scenario EvalResults.
/// </remarks>
public sealed class SummaryBuilder
{
    private readonly ArticlesRegistry _articles;

    /// <summary>Initialises a new <see cref="SummaryBuilder"/>.</summary>
    /// <param name="articles">Registry used to look up article metadata.</param>
    public SummaryBuilder(ArticlesRegistry articles)
    {
        _articles = articles ?? throw new ArgumentNullException(nameof(articles));
    }

    /// <summary>
    /// Builds a <see cref="GdprSummary"/> from the composite result tree rooted at
    /// <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Handles both the Standard preset tree (L0→5 pillars→articles→scenarios) and the
    /// Smoke/flat preset tree (L0→articles→scenarios). The distinction is made by
    /// checking whether L1 keys are pillar keys (start with "Pillar") or article keys
    /// (start with "gdpr.art").
    /// </remarks>
    /// <param name="root">The top-level composite <see cref="EvalResult"/>.</param>
    /// <returns>A populated <see cref="GdprSummary"/>.</returns>
    public GdprSummary Build(EvalResult root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var perPillar = new Dictionary<string, GdprPillarSummary>();
        var perArticle = new Dictionary<string, GdprArticleSummary>();

        if (root.Details.SubResults is not { Count: > 0 } l1Children)
        {
            return new GdprSummary(
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

                        perArticle[articleKey] = new GdprArticleSummary(
                            Score: article.Score.Value,
                            Status: status,
                            ScenarioCount: scenarios.Count,
                            ScenariosFailed: failedCount,
                            Severity: article.Score.Severity);
                    }
                }

                perPillar[pillarKey] = new GdprPillarSummary(
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

                perArticle[articleKey] = new GdprArticleSummary(
                    Score: article.Score.Value,
                    Status: status,
                    ScenarioCount: scenarios.Count,
                    ScenariosFailed: failedCount,
                    Severity: article.Score.Severity);
            }
            // No pillar layer — perPillar remains empty.
        }

        return new GdprSummary(
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
