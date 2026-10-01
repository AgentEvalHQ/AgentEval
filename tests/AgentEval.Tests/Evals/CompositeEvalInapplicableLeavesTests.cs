// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// ADR-030 Slice 1.2, extended past the letter of its acceptance criterion.
/// <para>
/// The criterion reads "one predicate, five call sites" and names the five aggregation strategies.
/// There is a SIXTH site that decides pass/fail — <c>CompositeEval</c>'s own <c>measuredCount</c>,
/// the Slice 0.1 fix for defect D-a — and it carried its own label-only copy of the rule. Slice 1.1
/// introduces a fourth neutral label, <c>inapplicable</c>, that the copy does not know; a composite
/// every leaf of which is inapplicable therefore counted three "measurements", skipped the
/// nothing-measured branch, took the <c>Threshold == null</c> path, read an empty severity rollup as
/// <c>none</c> and reported <b>pass</b>.
/// </para>
/// <para>
/// That is defect D-a exactly — <i>a green verdict from an instrument that measured nothing</i> —
/// re-opened by the slice meant to make undecidability expressible, and it would have shipped inside
/// the change that fixed it for the other three labels.
/// </para>
/// </summary>
public class CompositeEvalInapplicableLeavesTests
{
    private sealed class FixedEval(string key, EvalScore score) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version), score, new(null, null, null, null, null),
                new("atomic-code", null, null, null, null, 0, false), DateTimeOffset.UtcNow));
    }

    private static IEval Inapplicable(string key) => new FixedEval(key, EvalScore.NotApplicable());

    private static IEval Measured(string key, double value, bool passed) =>
        new FixedEval(key, new(value, null, passed ? "pass" : "fail", passed, null, "none", null));

    [Fact]
    public async Task AllLeavesInapplicable_DoesNotReportPass()
    {
        var composite = new CompositeEval(
            "c", "Composite", "test", "1.0.0",
            new[]
            {
                new EvalComponent(Inapplicable("a"), 1.0),
                new EvalComponent(Inapplicable("b"), 1.0),
                new EvalComponent(Inapplicable("c"), 1.0),
            },
            WeightedSumAggregation.Instance);

        var result = await composite.EvaluateAsync(new EvalInput("q"));

        Assert.False(result.Score.Passed);
        Assert.NotEqual("pass", result.Score.Label);
        Assert.NotNull(result.Details.Summary);
    }

    [Fact]
    public async Task AllLeavesInapplicable_SaysWhyInTheResultItself()
    {
        var composite = new CompositeEval(
            "c", "Composite", "test", "1.0.0",
            new[] { new EvalComponent(Inapplicable("a"), 1.0) },
            WeightedSumAggregation.Instance);

        var result = await composite.EvaluateAsync(new EvalInput("q"));

        // A reader of the artifact must see "nothing measured", never a bare 0.0.
        Assert.Contains("inapplicable", result.Details.Summary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MixedLeaves_StillReportAVerdictOverTheMeasuredOnes()
    {
        // The fix must not turn "some leaves were inapplicable" into "nothing was measured".
        var composite = new CompositeEval(
            "c", "Composite", "test", "1.0.0",
            new[]
            {
                new EvalComponent(Measured("a", 0.9, passed: true), 1.0),
                new EvalComponent(Inapplicable("b"), 1.0),
            },
            WeightedSumAggregation.Instance);

        var result = await composite.EvaluateAsync(new EvalInput("q"));

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        Assert.Equal(0.9, result.Score.Value, precision: 10);
    }

    [Fact]
    public async Task ACompositeOfInapplicableLeaves_IsAVoidCensus_NotAZero()
    {
        var subs = new[] { EvalScore.NotApplicable(), EvalScore.NotApplicable() };
        var census = subs.Census();

        Assert.True(census.Void);
        Assert.Contains("VOID", census.RenderMean(0.0), StringComparison.Ordinal);

        var composite = new CompositeEval(
            "c", "Composite", "test", "1.0.0",
            new[] { new EvalComponent(Inapplicable("a"), 1.0), new EvalComponent(Inapplicable("b"), 1.0) },
            MinAggregation.Instance);

        var result = await composite.EvaluateAsync(new EvalInput("q"));

        Assert.False(result.Score.Passed);
    }
}

/// <summary>
/// Coverage disclosure for a PARTLY measured composite. The unmeasured leaves stay out of the score
/// (deliberate, pinned elsewhere); what was missing is any statement that they did. Before this, nine
/// skipped leaves and one at 1.0 reported pass, Score = 1.0, and no note at all.
/// </summary>
public class CompositeEvalCoverageDisclosureTests
{
    private sealed class FixedEval(string key, EvalScore score) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";
        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
            => Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version), score, new(null, null, null, null, null),
                new("atomic-code", null, null, null, null, 0, false), DateTimeOffset.UtcNow));
    }

    private static EvalComponent Leaf(string key, EvalScore score, bool required = true) =>
        new(new FixedEval(key, score), 1.0, required);

    private static EvalScore Pass(double v) => new(v, null, "pass", true, null, "none", null);
    private static EvalScore Skipped() => new(0, null, "skipped", false, null, "none", null);
    private static EvalScore Errored() => new(0, null, "error", false, null, "none", null);

    [Fact]
    public async Task NineOfTenUnmeasured_KeepsTheVerdict_ButSaysHowLittleWasMeasured()
    {
        var components = new List<EvalComponent> { Leaf("measured", Pass(1.0)) };
        components.AddRange(Enumerable.Range(0, 9).Select(i => Leaf($"s{i}", Skipped())));
        var composite = new CompositeEval("c", "C", "test", "1.0.0", components, WeightedSumAggregation.Instance);

        var result = await composite.EvaluateAsync(new EvalInput("q"));

        // The verdict and score are unchanged — the disclosure is the fix, not a new rule.
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(1.0, result.Score.Value);
        Assert.Contains("Measured 1 of 10", result.Details.Summary!, StringComparison.Ordinal);
        Assert.Contains("9 left out of the score", result.Details.Summary!, StringComparison.Ordinal);
        Assert.Equal(result.Details.Summary, Assert.Single(result.Details.Recommendations!));
    }

    [Fact]
    public async Task TheBreakdownAddsUpToTheUnmeasuredCount()
    {
        var composite = new CompositeEval("c", "C", "test", "1.0.0",
            [Leaf("m", Pass(0.9)), Leaf("s", Skipped()), Leaf("i", EvalScore.NotApplicable()),
             Leaf("e", Errored(), required: false)],
            WeightedSumAggregation.Instance);

        var result = await composite.EvaluateAsync(new EvalInput("q"));

        Assert.Contains("3 left out of the score (1 skipped or not measured, 1 inapplicable, 1 errored)",
            result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullyMeasuredComposite_CarriesNoCoverageNote()
    {
        var composite = new CompositeEval("c", "C", "test", "1.0.0",
            [Leaf("a", Pass(0.9)), Leaf("b", Pass(0.8))], WeightedSumAggregation.Instance);

        var result = await composite.EvaluateAsync(new EvalInput("q"));

        Assert.Null(result.Details.Summary);
        Assert.Null(result.Details.Recommendations);
    }
}
