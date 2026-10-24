// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.System;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// T08: Task Navigation Efficiency's deterministic leaf (<c>path_edit_distance</c>) turned a null list into an empty one,
/// so nothing recorded and no declared path read a "trivially perfect" 1.0 PASS, nothing recorded against a declared path
/// read a measured 0, and recorded calls against no declared path read 0 too, capping every traced
/// <c>bench agentic</c> run that called a tool at 0.5. A null list is an absence, an empty one an observation.
/// Every test here except the two guards fails on the old leaf.
/// </summary>
public class ActionSequenceEditDistanceEvalTests
{
    private static readonly ToolCall[] Search = [new ToolCall("search", null, "{\"results\":[]}")];
    private static readonly ExpectedAction[] ExpectSearch = [new ExpectedAction("search", null)];

    private static Task<EvalResult> Leaf(IReadOnlyList<ToolCall>? calls, IReadOnlyList<ExpectedAction>? expected) =>
        new ActionSequenceEditDistanceEval(0.70).EvaluateAsync(new EvalInput("q", "r", ToolCalls: calls, ExpectedActions: expected));

    [Fact]
    public async Task NothingRecorded_AgainstADeclaredPath_IsNotMeasured_NotAZero()
    {
        var result = await Leaf(calls: null, expected: ExpectSearch);

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
        Assert.Contains("tool-call data", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingRecorded_AgainstADeclaredEmptyPath_IsNotMeasured_NotATriviallyPerfectPass()
    {
        var result = await Leaf(calls: null, expected: []);

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
    }

    [Fact]
    public async Task NoDeclaredPath_AndNothingRecorded_IsInapplicable_NotATriviallyPerfectPass()
    {
        var result = await Leaf(calls: null, expected: null);

        Assert.Equal("inapplicable", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotApplicable, result.Score.Measurement);
        Assert.Contains("ExpectedActions", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordedCalls_WithNoDeclaredPath_AreInapplicable_NotAZero()
    {
        // The `bench agentic --trace` shape: the trace fills ToolCalls, nothing declares a path.
        var result = await Leaf(calls: Search, expected: null);

        Assert.Equal("inapplicable", result.Score.Label);
        Assert.Equal(MeasurementState.NotApplicable, result.Score.Measurement);
        Assert.False(result.Score.CountsTowardAggregate());
    }

    [Fact]
    public async Task BothRecordedEmpty_IsARealMatch()
    {
        // Guard (passes before and after): a recorder that saw no calls, against a declared empty path.
        var result = await Leaf(calls: [], expected: []);

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(1.0, result.Score.Value);
    }

    [Fact]
    public async Task RecordedEmpty_AgainstADeclaredStep_IsAMeasuredZero_WithItsDimensions()
    {
        // A real observation: the agent did nothing where one step was expected. It is an ordinary distance now, so its
        // dimensions are recorded like any other comparison's (the old one-side-empty branch recorded none).
        var result = await Leaf(calls: [], expected: ExpectSearch);

        Assert.Equal("fail", result.Score.Label);
        Assert.Equal(0.0, result.Score.Value);
        Assert.Equal(MeasurementState.Measured, result.Score.CensusBucket());
        Assert.NotNull(result.Details.Dimensions);
        Assert.Equal(1.0, result.Details.Dimensions!["expected_length"]);
        Assert.Equal(0.0, result.Details.Dimensions["actual_length"]);
    }

    [Fact]
    public async Task MatchingRecordedPath_StillScoresOne()
    {
        // Guard (passes before and after).
        var result = await Leaf(calls: Search, expected: ExpectSearch);

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(1.0, result.Score.Value);
    }

    // ── Through the composite ────────────────────────────────────────────────

    [Fact]
    public async Task Composite_TracedCalls_WithNoDeclaredPath_RestOnPathQualityAlone_NoFalseZeroCap()
    {
        // Old behaviour: the leaf's 0 took half the weight, so the composite read 0.5 and failed its 0.70 bar whatever the
        // judged path quality was. Now the leaf is left out and the composite is the measured part.
        var result = await new TaskNavigationEfficiencyEval(new FixedScoreEvaluator(100))
            .EvaluateAsync(new EvalInput("q", "r", ToolCalls: Search));

        var leaf = Assert.Single(result.Details.SubResults!, r => r.Metric.Key == "path_edit_distance");
        var quality = Assert.Single(result.Details.SubResults!, r => r.Metric.Key == "path_quality");
        Assert.Equal("inapplicable", leaf.Score.Label);
        Assert.Equal(quality.Score.Value, result.Score.Value, precision: 6);
        Assert.Equal(quality.Score.Label, result.Score.Label);
    }

    [Fact]
    public async Task Composite_DeclaredPath_WithNothingRecorded_ReportsNoVerdict_AndAsksNoJudge()
    {
        // Old behaviour: a measured 0 on the leaf, so "fail" at 0.5. Nothing recorded what the agent did, so the
        // composite cannot attest a pass, and it cannot claim a measured failure either. Nor is the path-quality judge
        // asked to grade a path it was never shown (path_quality 1.2.0): it used to, and the composite then rested on
        // that judge's reading of the reply.
        var judge = new Golden.RecordingScoreEvaluator(100);
        var result = await new TaskNavigationEfficiencyEval(judge)
            .EvaluateAsync(new EvalInput("q", "r", ExpectedActions: ExpectSearch));

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
        Assert.All(result.Details.SubResults!, s => Assert.Equal("skipped", s.Score.Label));
        var quality = Assert.Single(result.Details.SubResults!, r => r.Metric.Key == "path_quality");
        Assert.Contains("tool-call data", quality.Details.Summary!, StringComparison.Ordinal);
        Assert.Empty(judge.Inputs);
    }

    [Fact]
    public async Task Composite_NothingRecorded_AndNoDeclaredPath_IsNotAJudgedPass()
    {
        // Before path_quality 1.2.0 the edit distance was inapplicable here and the composite was the judge's grade of a
        // path it was never shown: FixedScore 100 read PASS.
        var judge = new Golden.RecordingScoreEvaluator(100);
        var result = await new TaskNavigationEfficiencyEval(judge).EvaluateAsync(new EvalInput("q", "r"));

        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
        Assert.Empty(judge.Inputs);
    }

    [Fact]
    public async Task Composite_TheJudge_IsShownTheDeclaredPath_AsAReference()
    {
        // The rubric's ordering criterion reads "Given expected_actions"; the judge was never sent them.
        var judge = new Golden.RecordingScoreEvaluator(100);
        await new TaskNavigationEfficiencyEval(judge).EvaluateAsync(new EvalInput("q", "r", ToolCalls: Search,
            ExpectedActions: [new ExpectedAction("look up the order-7f3a status", ["search"])]));

        var input = Assert.Single(judge.Inputs);
        Assert.Contains("<expected_actions>", input, StringComparison.Ordinal);
        Assert.Contains("order-7f3a", input, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLeafAndTheComposite_AreNewInstrumentVersions()
    {
        // Results before and after this change are not the same instrument; the card follows the composite.
        Assert.Equal("1.1.0", new ActionSequenceEditDistanceEval(0.70).Version);
        Assert.Equal("1.3.0", new TaskNavigationEfficiencyEval(new FixedScoreEvaluator(100)).Version);
        var inner = (CompositeEval)new TaskNavigationEfficiencyEval(new FixedScoreEvaluator(100)).DefinitionInner;
        Assert.Equal("1.2.0", Assert.Single(inner.Components, c => c.Eval.Key == "path_quality").Eval.Version);
    }
}
