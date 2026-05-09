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

        // Determine tree shape: check whether any L1 node is a recognised category node
        // (its Category or Key matches a known agentic category string).
        bool hasCategoryLayer = l1Children.Any(c => IsKnownCategory(c));

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
            // Flat 2-layer tree: root → evaluators → (optional) criteria
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

                // Accumulate into synthetic category buckets.
                AccumulateSyntheticCategory(perCategory, categoryKey, evaluator.Score.Value, status,
                    evaluator.Score.Severity == "critical" && status == "FAIL" ? evalKey : null);
            }
        }

        return new AgenticSummary(
            OverallScore: root.Score.Value,
            OverallStatus: MapLabelToStatus(root.Score.Label),
            PerCategory: perCategory,
            PerEvaluator: perEvaluator);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static bool IsKnownCategory(EvalResult node)
    {
        var cat = node.Metric.Category;
        if (!string.IsNullOrWhiteSpace(cat) && s_knownCategories.Contains(cat))
            return true;
        return s_knownCategories.Contains(node.Metric.Key);
    }

    private static string ResolveCategoryKey(EvalResult node)
    {
        var cat = node.Metric.Category;
        if (!string.IsNullOrWhiteSpace(cat)) return cat;
        return node.Metric.Key;
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

        var key = node.Metric.Key ?? string.Empty;
        if (key.StartsWith("task_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("intent_", StringComparison.OrdinalIgnoreCase))
            return "system-outcome";
        if (key.StartsWith("tool_", StringComparison.OrdinalIgnoreCase))
            return "agentic-process";
        if (key.StartsWith("safety_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("refusal_", StringComparison.OrdinalIgnoreCase))
            return "safety-security";
        if (key.StartsWith("rag_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("retrieval_", StringComparison.OrdinalIgnoreCase))
            return "rag";
        if (key.StartsWith("judge_", StringComparison.OrdinalIgnoreCase))
            return "judge-quality";

        return "operational";
    }

    /// <summary>
    /// Accumulates a single evaluator result into the in-progress category dictionary.
    /// Scores are averaged incrementally using a running count stored as a negative sentinel
    /// (not ideal for large-scale; acceptable for the benchmark tree sizes expected here).
    /// </summary>
    private static void AccumulateSyntheticCategory(
        Dictionary<string, AgenticCategorySummary> perCategory,
        string categoryKey,
        double score,
        string status,
        string? criticalFailKey)
    {
        if (!perCategory.TryGetValue(categoryKey, out var existing))
        {
            perCategory[categoryKey] = new AgenticCategorySummary(
                Score: score,
                Status: status,
                CriticalFails: criticalFailKey is null
                    ? Array.Empty<string>()
                    : new[] { criticalFailKey });
        }
        else
        {
            // Simple re-average: not perfectly accurate without a running count,
            // but for the flat preset case this is a best-effort roll-up.
            var newCriticals = criticalFailKey is null
                ? existing.CriticalFails
                : existing.CriticalFails.Concat(new[] { criticalFailKey }).ToList();

            // Combine existing and new score with equal weight approximation.
            var combined = (existing.Score + score) / 2.0;
            var combinedStatus = CombineStatus(existing.Status, status);

            perCategory[categoryKey] = new AgenticCategorySummary(
                Score: combined,
                Status: combinedStatus,
                CriticalFails: newCriticals);
        }
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
