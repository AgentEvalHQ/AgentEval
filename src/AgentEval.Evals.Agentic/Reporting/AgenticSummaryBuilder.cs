// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Reporting;

/// <summary>
/// Walks an agentic benchmark composite result tree and produces an <see cref="AgenticSummary"/>
/// with per-category and per-evaluator roll-ups.
/// </summary>
/// <remarks>
/// <para>
/// The expected tree shape is: L0 (overall) &gt; SubResults = N category EvalResults
/// &gt; SubResults = M evaluator EvalResults &gt; optional leaf sub-results.
/// Categories are detected from <see cref="EvalMetadata.Category"/> on L1 nodes;
/// if the Category is absent or empty the node's Key is used as the category label.
/// </para>
/// <para>
/// Handles both the full preset tree (L0 → categories → evaluators → criteria) and a flat
/// preset tree (L0 → evaluators → criteria). The distinction is made by checking whether
/// the L1 nodes carry a recognisable agentic category string in their <c>Category</c> or <c>Key</c>.
/// </para>
/// </remarks>
public sealed class AgenticSummaryBuilder
{
    /// <summary>Known agentic category identifiers.</summary>
    private static readonly HashSet<string> s_knownCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "system-outcome",
        "agentic-process",
        "rag",
        "safety-security",
        "operational",
        "judge-quality"
    };

    /// <summary>
    /// Builds an <see cref="AgenticSummary"/> from the composite result tree rooted at
    /// <paramref name="root"/>.
    /// </summary>
    /// <param name="root">The top-level composite <see cref="EvalResult"/> from the runner.</param>
    /// <returns>A populated <see cref="AgenticSummary"/>.</returns>
    public AgenticSummary Build(EvalResult root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var perCategory = new Dictionary<string, AgenticCategorySummary>(StringComparer.Ordinal);
        var perEvaluator = new Dictionary<string, AgenticEvaluatorSummary>(StringComparer.Ordinal);

        if (root.Details.SubResults is not { Count: > 0 } l1Children)
        {
            return new AgenticSummary(
                OverallScore: root.Score.Value,
                OverallStatus: MapLabelToStatus(root.Score.Label),
                PerCategory: perCategory,
                PerEvaluator: perEvaluator);
        }

        // Determine tree shape. A genuine category layer exists only when an L1 node is
        // itself a category *grouping* node — identified by its own Key being a known
        // category id. Flat presets (root → evaluators → criteria) carry the category on
        // each evaluator leaf's Metric.Category; that tag must NOT be mistaken for a
        // grouping layer, or every flat-preset report mislabels evaluators as categories
        // and collapses same-category siblings (BUG-02).
        bool hasCategoryLayer = l1Children.Any(IsCategoryNode);

        if (hasCategoryLayer)
        {
            // Full 3-layer tree: root → categories → evaluators → (optional) criteria
            foreach (var categoryNode in l1Children)
            {
                var categoryKey = ResolveCategoryKey(categoryNode);
                var criticalFails = new List<string>();

                if (categoryNode.Details.SubResults is { Count: > 0 } evaluators)
                {
                    foreach (var evaluator in evaluators)
                    {
                        var evalKey = evaluator.Metric.Key;
                        var status = MapLabelToStatus(evaluator.Score.Label);

                        if (evaluator.Score.Severity == "critical" && status == "FAIL")
                            criticalFails.Add(evalKey);

                        // Confidence: look for it in the evaluator's Details or Provenance.
                        var confidence = ExtractConfidence(evaluator);

                        perEvaluator[evalKey] = new AgenticEvaluatorSummary(
                            Score: evaluator.Score.Value,
                            Status: status,
                            Severity: evaluator.Score.Severity ?? "none",
                            Confidence: confidence);
                    }
                }

                perCategory[categoryKey] = new AgenticCategorySummary(
                    Score: categoryNode.Score.Value,
                    Status: MapLabelToStatus(categoryNode.Score.Label),
                    CriticalFails: criticalFails);
            }
        }
        else
        {
            // Flat 2-layer tree: root → evaluators → (optional) criteria.
            // Roll evaluators up into synthetic category buckets using a true running mean
            // (sum / count). The previous pairwise `(existing + score) / 2` re-average
            // silently over-weighted the last evaluator added to each bucket (BUG-16).
            var catScoreSum = new Dictionary<string, double>(StringComparer.Ordinal);
            var catCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var catStatus = new Dictionary<string, string>(StringComparer.Ordinal);
            var catCriticals = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (var evaluator in l1Children)
            {
                var evalKey = evaluator.Metric.Key;
                var status = MapLabelToStatus(evaluator.Score.Label);
                var categoryKey = ResolveCategoryFromEvaluator(evaluator);
                var confidence = ExtractConfidence(evaluator);

                perEvaluator[evalKey] = new AgenticEvaluatorSummary(
                    Score: evaluator.Score.Value,
                    Status: status,
                    Severity: evaluator.Score.Severity ?? "none",
                    Confidence: confidence);

                catScoreSum[categoryKey] = catScoreSum.GetValueOrDefault(categoryKey) + evaluator.Score.Value;
                catCount[categoryKey] = catCount.GetValueOrDefault(categoryKey) + 1;
                catStatus[categoryKey] = catStatus.TryGetValue(categoryKey, out var prior)
                    ? CombineStatus(prior, status)
                    : status;

                if (evaluator.Score.Severity == "critical" && status == "FAIL")
                {
                    if (!catCriticals.TryGetValue(categoryKey, out var fails))
                        catCriticals[categoryKey] = fails = new List<string>();
                    fails.Add(evalKey);
                }
            }

            foreach (var (categoryKey, count) in catCount)
            {
                perCategory[categoryKey] = new AgenticCategorySummary(
                    Score: catScoreSum[categoryKey] / count,
                    Status: catStatus[categoryKey],
                    CriticalFails: catCriticals.TryGetValue(categoryKey, out var fails)
                        ? fails
                        : (IReadOnlyList<string>)Array.Empty<string>());
            }
        }

        return new AgenticSummary(
            OverallScore: root.Score.Value,
            OverallStatus: MapLabelToStatus(root.Score.Label),
            PerCategory: perCategory,
            PerEvaluator: perEvaluator);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="node"/> is a category <i>grouping</i> node — i.e. its own
    /// <see cref="EvalMetadata.Key"/> is a known agentic category id. Evaluator leaves
    /// (whose Key is an evaluator id and whose Category merely tags their bucket) are
    /// deliberately excluded so flat presets are not misread as a category layer.
    /// </summary>
    private static bool IsCategoryNode(EvalResult node) =>
        s_knownCategories.Contains(node.Metric.Key);

    private static string ResolveCategoryKey(EvalResult node)
    {
        // A grouping node's identity is its (known-category) Key; fall back to Category,
        // then Key, for resilience against non-canonical trees.
        if (s_knownCategories.Contains(node.Metric.Key)) return node.Metric.Key;
        var cat = node.Metric.Category;
        return !string.IsNullOrWhiteSpace(cat) ? cat : node.Metric.Key;
    }

    /// <summary>
    /// Infers a category for a flat-preset evaluator node from its metric key prefix
    /// or category field. Falls back to <c>"operational"</c>.
    /// </summary>
    private static string ResolveCategoryFromEvaluator(EvalResult node)
    {
        var cat = node.Metric.Category;
        if (!string.IsNullOrWhiteSpace(cat) && s_knownCategories.Contains(cat))
            return cat;

        // ARC-11: shared last-resort heuristic (one keyword map across summary + PDF reports).
        return AgenticCategoryResolver.InferCategoryFromKey(node.Metric.Key);
    }

    private static string CombineStatus(string a, string b)
    {
        if (a == "FAIL" || b == "FAIL") return "FAIL";
        if (a == "WARN" || b == "WARN") return "WARN";
        return "PASS";
    }

    /// <summary>
    /// Attempts to extract a confidence value from the evaluator result.
    /// Returns null if no confidence information is available.
    /// </summary>
    private static double? ExtractConfidence(EvalResult evaluator)
    {
        // Confidence may be stored in Provenance.ConfidenceScore or Details.Metrics.
        // EvalResult does not currently have a dedicated Confidence property, so
        // we inspect the Score's Confidence field if present.
        return evaluator.Score.Confidence;
    }

    private static string MapLabelToStatus(string label) => label.ToUpperInvariant() switch
    {
        "PASS" => "PASS",
        "WARN" => "WARN",
        _ => "FAIL"
    };
}
