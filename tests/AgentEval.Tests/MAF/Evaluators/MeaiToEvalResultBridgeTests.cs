// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;
using AgentEval.MAF.Evaluators;
using Xunit;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// Unit coverage for the deterministic conversion logic in <see cref="MeaiToEvalResultBridge"/>:
/// AgentEval-score recovery from the reason marker, the 1–5 → 0–100 fallback, the query/metric tree
/// shape, and key disambiguation for same-named metrics.
/// </summary>
public class MeaiToEvalResultBridgeTests
{
    private static AgentEvaluationResults Wrap(EvaluationResult meai) =>
        new("AgentEval", new[] { meai }, inputItems: Array.Empty<EvalItem>());

    private static EvaluationResult ResultWith(params (string Key, EvaluationMetric Metric)[] metrics)
    {
        var r = new EvaluationResult();
        foreach (var (key, metric) in metrics)
            r.Metrics[key] = metric;
        return r;
    }

    private static EvalResult FirstLeaf(EvalResult tree) =>
        tree.Details.SubResults![0]      // query node
            .Details.SubResults![0];     // first metric leaf

    // ── B10q (review round 4 M7): a metric MEAI could not score is no verdict, never PASS 100 ──────────────────────

    public static TheoryData<string, EvaluationMetric, string> MetricsWithoutAVerdict()
    {
        var withDiagnostic = new NumericMetric("relevance", 4.0, "scored");
        withDiagnostic.AddDiagnostics(EvaluationDiagnostic.Error("Failed to parse numeric score for 'relevance'."));
        return new()
        {
            { "no value", new NumericMetric("groundedness", null, "no score"), "error" },
            { "no value, inconclusive", new NumericMetric("groundedness", null)
                { Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Inconclusive) }, "error" },
            { "error diagnostic", withDiagnostic, "error" },
            { "off scale, inconclusive", new NumericMetric("coherence", 6.0)
                { Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Inconclusive) }, "error" },
            { "boolean with no value", new BooleanMetric("equivalent", null), "error" },
            { "a string with no interpretation", new StringMetric("note", "informational"), "skipped" },
        };
    }

    [Theory]
    [MemberData(nameof(MetricsWithoutAVerdict))]
    public void AMetricWithoutAVerdict_IsNotAPass(string because, EvaluationMetric metric, string label)
    {
        // MEAI's quality evaluators leave the value empty and record an error diagnostic when they cannot parse the
        // judge's reply or lack their context; every such metric fell to the last branch and read PASS 100.
        var tree = MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(ResultWith((metric.Name, metric))));

        var leaf = FirstLeaf(tree);
        Assert.True(label == leaf.Score.Label, because);
        Assert.False(leaf.Score.Passed);
        Assert.Equal(0.0, leaf.Score.Value);
        Assert.False(tree.Details.SubResults![0].Score.Passed, because);   // nor does its query pass on it
    }

    [Theory]
    [InlineData(true, "pass", 1.0)]
    [InlineData(false, "fail", 0.0)]   // read 100 with no interpretation
    public void ABooleanMetric_IsItsOwnVerdict(bool value, string label, double score)
    {
        var tree = MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(ResultWith(("equivalent", new BooleanMetric("equivalent", value)))));

        Assert.Equal(label, FirstLeaf(tree).Score.Label);
        Assert.Equal(score, FirstLeaf(tree).Score.Value);
    }

    [Theory]
    [InlineData("error", "error")]     // a metric with no verdict beside a pass: no verdict for the query (was FAIL)
    [InlineData("fail", "fail")]       // a measured failure decides
    [InlineData("skipped", "warn")]    // a metric that did not run withholds the pass
    public void AQuery_ReadsItsMetricsByMeasurement(string second, string queryLabel)
    {
        EvaluationMetric other = second switch
        {
            "error" => new NumericMetric("groundedness", null),
            "fail" => new NumericMetric("groundedness", 1.0) { Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true) },
            _ => new StringMetric("note", "informational"),
        };
        var meai = ResultWith(("relevance", new NumericMetric("relevance", 5.0)), (other.Name, other));

        var query = MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai)).Details.SubResults![0];

        Assert.Equal(queryLabel, query.Score.Label);
        Assert.False(query.Score.Passed);
        if (second == "error")
            Assert.Equal(1.0, query.Score.Value, 6);   // the mean of the measured metric, not (1.0 + 0) / 2
    }

    [Fact]
    public void AMetricWithACustomNonPassingLabel_DoesNotPassItsQuery()
    {
        // Review round 6 M-2 (B10aa): MeasuredRollup read only "fail"/"warn" as non-passing, so a measured "needs-review"
        // (not passed; ReportStatus FAIL) passed the query, the run and the unified report.
        var meai = ResultWith(("custom", new NumericMetric("custom", 3.0, "AgentEval score: 60/100 (needs-review, severity medium)")));

        var tree = MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai));

        Assert.Equal("needs-review", FirstLeaf(tree).Score.Label);
        Assert.False(FirstLeaf(tree).Score.Passed);
        Assert.Equal("fail", tree.Details.SubResults![0].Score.Label);
        Assert.False(tree.Score.Passed);
    }

    [Fact]
    public void ARunWhoseOnlyQueryWithheldItsPass_Withholds_NotSkipped()
    {
        // Review round 5 L-1 (B10u): the root checked "nothing measured" before the withheld state and read skipped 0.000
        // though a metric passed (the B9d defect).
        var meai = ResultWith(("coherence", new NumericMetric("coherence", 4.5)), ("note", new StringMetric("note", "informational")));

        var tree = MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai));

        Assert.Equal("warn", tree.Details.SubResults![0].Score.Label);
        Assert.Equal("warn", tree.Score.Label);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.NotMeasured, tree.Score.Measurement);
    }

    [Fact]
    public void Build_RecoversAgentEvalScore_FromReasonMarker()
    {
        // Value (5.0) deliberately disagrees with the marker (85) to prove the marker wins.
        var meai = ResultWith(("llm_relevance",
            new NumericMetric("llm_relevance", 5.0, "AgentEval score: 85/100 (pass, severity none)")));

        var tree = MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai));

        Assert.Equal(0.85, FirstLeaf(tree).Score.Value, 3);
    }

    [Fact]
    public void Build_FallsBackTo1To5LinearMap_WhenNoMarker()
    {
        // No marker → (value - 1) / 4 * 100; 3.0 → 50% → 0.50.
        var meai = ResultWith(("code_tool_success",
            new NumericMetric("code_tool_success", 3.0, "plain reason, no marker")));

        var tree = MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai));

        Assert.Equal(0.50, FirstLeaf(tree).Score.Value, 3);
    }

    [Fact]
    public void Build_ShapesTree_OneQueryNode_OneLeafPerMetric()
    {
        var meai = ResultWith(
            ("a", new NumericMetric("a", 5.0, "AgentEval score: 100/100 (pass, severity none)")),
            ("b", new NumericMetric("b", 1.0, "AgentEval score: 0/100 (fail, severity high)")));

        var tree = MeaiToEvalResultBridge.Build("MyEval", new[] { "what is 2+2?" }, Wrap(meai));

        Assert.Equal("MyEval", tree.Metric.Name);
        Assert.Single(tree.Details.SubResults!);                       // one query node
        Assert.Equal(2, tree.Details.SubResults![0].Details.SubResults!.Count);  // two metric leaves
    }

    [Fact]
    public void Build_DisambiguatesSameNamedMetrics_ViaDictionaryKey()
    {
        // Two metrics share the display name "relevance" but have distinct dictionary keys
        // (as AgentEvalCompositeEvaluator.AddMetric produces "name" + "name #2"). The leaves must
        // get distinct EvalResult keys, not collide.
        var meai = ResultWith(
            ("relevance",    new NumericMetric("relevance", 5.0, "AgentEval score: 90/100 (pass, severity none)")),
            ("relevance #2", new NumericMetric("relevance", 3.0, "AgentEval score: 50/100 (pass, severity none)")));

        var tree = MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai));

        var leaves = tree.Details.SubResults![0].Details.SubResults!;
        Assert.Equal(2, leaves.Count);
        Assert.Equal(2, leaves.Select(l => l.Metric.Key).Distinct().Count());  // keys are unique
    }

    [Fact]
    public void Build_LowScoreMarker_WithoutInterpretation_IsNotPassed()
    {
        // Regression: a low marker score with no MEAI Interpretation must derive pass/fail from the
        // score, never render as a green "pass".
        var meai = ResultWith(("llm_x",
            new NumericMetric("llm_x", 5.0, "AgentEval score: 10/100 (fail, severity high)")));

        var leaf = FirstLeaf(MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai)));

        Assert.Equal(0.10, leaf.Score.Value, 3);
        Assert.False(leaf.Score.Passed);
        Assert.Equal("fail", leaf.Score.Label);
    }

    [Fact]
    public void Build_RecoversLabelAndSeverity_FromMarker()
    {
        // The composite embeds the original verdict in the marker — recover it so a "critical" leaf
        // isn't flattened to the binary "high".
        var meai = ResultWith(("x",
            new NumericMetric("x", 3.0, "AgentEval score: 40/100 (fail, severity critical)")));

        var leaf = FirstLeaf(MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai)));

        Assert.Equal(0.40, leaf.Score.Value, 3);
        Assert.Equal("fail", leaf.Score.Label);
        Assert.Equal("critical", leaf.Score.Severity);
        Assert.False(leaf.Score.Passed);
    }

    // ── Mixed-scale normalisation (Foundry: 0–1 vs 1–5) ───────────────────────────────────────

    [Fact]
    public void Build_NormalizesZeroToOneScale_WhenValueIsBelow1AndNotFailed()
    {
        // Foundry task_adherence uses 0–1; 0.5 (mid-range) should map to 50%, not a negative clamped value.
        var meai = ResultWith(("task_adherence",
            new NumericMetric("task_adherence", 0.5)
            { Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Good, failed: false) }));

        var leaf = FirstLeaf(MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai)));

        Assert.Equal(0.50, leaf.Score.Value, 3);
        Assert.True(leaf.Score.Passed);
    }

    [Fact]
    public void Build_NormalizesAmbiguousV1_AsZero_WhenInterpretationIsFailed()
    {
        // v==1.0 is ambiguous: best on 0–1 scale OR worst on 1–5 scale.
        // Interpretation.Failed=true disambiguates to 1–5 worst → (1-1)/4*100 = 0%.
        var meai = ResultWith(("relevance",
            new NumericMetric("relevance", 1.0)
            { Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true) }));

        var leaf = FirstLeaf(MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai)));

        Assert.Equal(0.0, leaf.Score.Value, 3);
        Assert.False(leaf.Score.Passed);
    }

    [Fact]
    public void Build_NormalizesAmbiguousV1_As100Percent_WhenInterpretationIsNotFailed()
    {
        // v==1.0, Interpretation.Failed=false → 0–1 scale, best → 1.0*100 = 100%.
        var meai = ResultWith(("task_adherence",
            new NumericMetric("task_adherence", 1.0)
            { Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Good, failed: false) }));

        var leaf = FirstLeaf(MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai)));

        Assert.Equal(1.0, leaf.Score.Value, 3);
        Assert.True(leaf.Score.Passed);
    }

    [Fact]
    public void Build_NormalizesVBelow1_As0To1Proportion_EvenWhenFailed()
    {
        // Regression: v < 1.0 with Failed=true must use 0-1 proportion, NOT the 1-5 formula.
        // task_adherence=0.5 (partial failure on 0-1 scale) → 50%, not clamped-to-0%.
        var meai = ResultWith(("task_adherence",
            new NumericMetric("task_adherence", 0.5)
            { Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true) }));

        var leaf = FirstLeaf(MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai)));

        Assert.Equal(0.50, leaf.Score.Value, 3);
    }

    [Theory]
    [InlineData(1.0 - 5e-10, false, 1.0)]   // slightly below 1.0, not failed → near-1.0, treat as 0-1 best → 100%
    [InlineData(1.0 + 5e-10, false, 1.0)]   // slightly above 1.0, not failed → near-1.0, treat as 0-1 best → 100%
    [InlineData(1.0 - 5e-10, true,  0.0)]   // slightly below 1.0, failed → near-1.0, treat as 1-5 worst → 0%
    [InlineData(1.0 + 5e-10, true,  0.0)]   // slightly above 1.0, failed → near-1.0, treat as 1-5 worst → 0%
    public void Build_NearOne_EpsilonBoundary_DisambiguatesViaInterpretation(
        double rawValue, bool failed, double expectedScore)
    {
        // Regression: exact equality (v==1.0) is fragile under floating-point deserialisation.
        // Values within epsilon of 1.0 must use Interpretation.Failed for disambiguation.
        var rating = failed ? EvaluationRating.Unacceptable : EvaluationRating.Good;
        var meai = ResultWith(("task_adherence",
            new NumericMetric("task_adherence", rawValue)
            { Interpretation = new EvaluationMetricInterpretation(rating, failed: failed) }));

        var leaf = FirstLeaf(MeaiToEvalResultBridge.Build("Eval", new[] { "q1" }, Wrap(meai)));

        Assert.Equal(expectedScore, leaf.Score.Value, 6);
    }
}