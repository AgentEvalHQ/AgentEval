// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Reporting;
using Xunit;

namespace AgentEval.Tests.Agentic.Reporting;

/// <summary>
/// Unit tests for <see cref="AgenticSummaryBuilder"/>.
/// Regression coverage for BUG-02 (flat-preset leaves misclassified as category nodes)
/// and BUG-16 (synthetic-category roll-up used a skewed pairwise re-average).
/// </summary>
public class AgenticSummaryBuilderTests
{
    // ── Tree-building helper ───────────────────────────────────────────────────

    private static EvalResult Node(
        string key,
        string category,
        double score,
        string label = "pass",
        string severity = "none",
        IReadOnlyList<EvalResult>? subResults = null) =>
        new(
            Metric: new(key, key, category, "1.0.0"),
            Score: new(score, null, label, label.Equals("pass", StringComparison.OrdinalIgnoreCase), 0.5, severity, null),
            Details: new(null, null, null, subResults, subResults is null ? null : "weighted-sum"),
            Provenance: new("test", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UnixEpoch);

    /// <summary>
    /// The canonical flat 'agentic.standard' shape: root → 6 evaluator leaves, each tagged
    /// with a known Category. Four are atomic (SubResults == null), one is composite.
    /// </summary>
    private static EvalResult FlatStandardRoot() =>
        Node("agentic.standard", "agentic", 0.70, subResults: new[]
        {
            Node("task_completion",            "system-outcome",  0.60),
            Node("task_adherence",             "system-outcome",  0.90),
            Node("tool_call_accuracy",         "agentic-process", 0.80,
                 subResults: new[] { Node("tool_selection", "agentic-process", 0.80) }),
            Node("intent_resolution",          "system-outcome",  0.30),
            Node("task_navigation_efficiency", "system-outcome",  0.50),
            Node("intent_identification",      "system-outcome",  0.70),
        });

    // ── BUG-02: flat preset must route leaves through perEvaluator ─────────────

    [Fact]
    public void Build_FlatPreset_RoutesEachLeafToPerEvaluator()
    {
        var summary = new AgenticSummaryBuilder().Build(FlatStandardRoot());

        // All six evaluator leaves appear as evaluators (not as categories, and not dropped).
        Assert.Equal(6, summary.PerEvaluator.Count);
        Assert.Contains("task_completion", summary.PerEvaluator.Keys);
        Assert.Contains("tool_call_accuracy", summary.PerEvaluator.Keys);
        Assert.Contains("intent_identification", summary.PerEvaluator.Keys);

        // A category id must never leak into the per-evaluator table.
        Assert.DoesNotContain("system-outcome", summary.PerEvaluator.Keys);
        Assert.DoesNotContain("agentic-process", summary.PerEvaluator.Keys);

        // Evaluator scores are carried through verbatim.
        Assert.Equal(0.60, summary.PerEvaluator["task_completion"].Score, 6);
    }

    [Fact]
    public void Build_FlatPreset_BucketsBySyntheticCategory()
    {
        var summary = new AgenticSummaryBuilder().Build(FlatStandardRoot());

        // Two synthetic categories: system-outcome (5 leaves) and agentic-process (1 leaf).
        Assert.Equal(2, summary.PerCategory.Count);
        Assert.Contains("system-outcome", summary.PerCategory.Keys);
        Assert.Contains("agentic-process", summary.PerCategory.Keys);
    }

    // ── BUG-16: same-category siblings must average correctly, not collapse ────

    [Fact]
    public void Build_FlatPreset_SameCategorySiblings_UseTrueMean()
    {
        var summary = new AgenticSummaryBuilder().Build(FlatStandardRoot());

        // system-outcome holds {0.60, 0.90, 0.30, 0.50, 0.70} → true mean 0.60.
        // The old pairwise re-average ((((0.6+0.9)/2+0.3)/2+0.5)/2+0.7)/2 ≈ 0.55 and
        // overwrite-collapse (0.70, last writer) would both be wrong.
        Assert.Equal(0.60, summary.PerCategory["system-outcome"].Score, 6);
        Assert.Equal(0.80, summary.PerCategory["agentic-process"].Score, 6);
    }

    // ── BUG-27: synthetic-category roll-up must be order-independent ───────────

    [Fact]
    public void Build_FlatPreset_CategoryScore_IsOrderIndependent()
    {
        // Same five system-outcome leaves, two different child orderings. The old pairwise
        // (existing + score) / 2 re-average weighted the last-added evaluator far more, making
        // the bucket score depend on insertion order (BUG-27). A true mean must not.
        var ascending = Node("agentic.standard", "agentic", 0.70, subResults: new[]
        {
            Node("task_completion",            "system-outcome", 0.30),
            Node("task_adherence",             "system-outcome", 0.50),
            Node("intent_resolution",          "system-outcome", 0.60),
            Node("task_navigation_efficiency", "system-outcome", 0.70),
            Node("intent_identification",      "system-outcome", 0.90),
        });
        var descending = Node("agentic.standard", "agentic", 0.70, subResults: new[]
        {
            Node("intent_identification",      "system-outcome", 0.90),
            Node("task_navigation_efficiency", "system-outcome", 0.70),
            Node("intent_resolution",          "system-outcome", 0.60),
            Node("task_adherence",             "system-outcome", 0.50),
            Node("task_completion",            "system-outcome", 0.30),
        });

        var asc = new AgenticSummaryBuilder().Build(ascending).PerCategory["system-outcome"].Score;
        var desc = new AgenticSummaryBuilder().Build(descending).PerCategory["system-outcome"].Score;

        Assert.Equal(0.60, asc, 6);
        Assert.Equal(asc, desc, 6); // identical regardless of order
    }

    // ── Genuine category layer (L1 node keyed by a known category) still works ─

    [Fact]
    public void Build_CategoryLayerTree_RoutesByGroupingNodeKey()
    {
        var root = Node("agentic.grouped", "agentic", 0.70, subResults: new[]
        {
            Node("system-outcome", "agentic", 0.65, subResults: new[]
            {
                Node("task_completion", "system-outcome", 0.60),
                Node("task_adherence",  "system-outcome", 0.70),
            }),
        });

        var summary = new AgenticSummaryBuilder().Build(root);

        // The grouping node defines the category; its child evaluators populate perEvaluator.
        Assert.Single(summary.PerCategory);
        Assert.Equal(0.65, summary.PerCategory["system-outcome"].Score, 6);
        Assert.Equal(2, summary.PerEvaluator.Count);
        Assert.Contains("task_completion", summary.PerEvaluator.Keys);
        Assert.DoesNotContain("system-outcome", summary.PerEvaluator.Keys);
    }
}
