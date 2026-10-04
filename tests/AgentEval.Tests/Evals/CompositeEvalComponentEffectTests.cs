// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals;

/// <summary>
/// <see cref="EvalComponent.OnFailure"/> (#203 review, B6b): what a component's own measured failure does to the
/// composite's verdict. The owner's rule: keep working and say the answer is not optimal because of the failed
/// dimension (Warn), unless the failure means the answer cannot be trusted (Fail). Before, every component was
/// averaged: fluency 0.30 with the rest perfect read RAG 0.965 = PASS.
/// </summary>
public class CompositeEvalComponentEffectTests
{
    private sealed class Fixed(string key, string label, double value) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(
            label == "skipped"
                ? EvalResult.Skipped(this, "did not run")
                : new EvalResult(
                    new(key, key, "test", "1.0.0"),
                    new EvalScore(value, null, label, label == "pass", null, label == "pass" ? "none" : "medium", null),
                    new(null, null, null, null, null),
                    new("atomic-code", null, null, null, null, 0, false),
                    DateTimeOffset.UtcNow));
    }

    private static readonly EvalInput Input = new("q", "r");

    // Five dimensions; the one under test is weighted 0.1, so the average alone always passes the 0.7 threshold.
    private static Task<EvalResult> Run(string label, ComponentFailureEffect effect, bool required = true, double value = 0.3)
    {
        var components = new List<EvalComponent>
        {
            new(new Fixed("a", "pass", 1.0), 0.225), new(new Fixed("b", "pass", 1.0), 0.225),
            new(new Fixed("c", "pass", 1.0), 0.225), new(new Fixed("d", "pass", 1.0), 0.225),
            new(new Fixed("dimension", label, value), 0.1, required) { OnFailure = effect },
        };
        return new CompositeEval("p", "P", "test", "1.0.0", components, WeightedSumAggregation.Instance, threshold: 0.7)
            .EvaluateAsync(Input);
    }

    [Fact]
    public async Task Averaged_IsTheOldBehaviour_TheAverageAloneDecides()
    {
        var result = await Run("fail", ComponentFailureEffect.Averaged);

        Assert.Equal("pass", result.Score.Label);   // 0.93: the failure is averaged out, as before
    }

    [Fact]
    public async Task Fail_AnAccuracyDimensionFailing_FailsTheComposite_AndSaysWhich()
    {
        var result = await Run("fail", ComponentFailureEffect.Fail);

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.Measured, result.Score.CensusBucket());
        Assert.Contains("Failed: dimension", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warn_AQualityDimensionFailing_WarnsAndNamesIt_ItDoesNotFail()
    {
        var result = await Run("fail", ComponentFailureEffect.Warn);

        Assert.Equal("warn", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Contains("Not optimal: dimension", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ComponentFailureEffect.Fail)]
    [InlineData(ComponentFailureEffect.Warn)]
    public async Task ADimensionThatPassed_ChangesNothing(ComponentFailureEffect effect)
    {
        var result = await Run("pass", effect, value: 1.0);

        Assert.Equal("pass", result.Score.Label);
        Assert.Null(result.Details.Summary);
    }

    [Fact]
    public async Task AnAccuracyDimensionThatOnlyWarned_PassesAWarnUp_NotAFail()
    {
        var result = await Run("warn", ComponentFailureEffect.Fail, value: 0.8);

        Assert.Equal("warn", result.Score.Label);
    }

    [Fact]
    public async Task ASkippedDimension_IsNotAFailure_TheCoverageRulesDecide()
    {
        // Optional and skipped: neither the effect nor the required-component rule applies.
        var result = await Run("skipped", ComponentFailureEffect.Fail, required: false);

        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task AMeasuredAccuracyFailure_IsAVerdict_EvenWhenARequiredComponentDidNotRun()
    {
        var components = new List<EvalComponent>
        {
            new(new Fixed("a", "pass", 1.0), 0.45), new(new Fixed("unrun", "skipped", 0.0), 0.45),
            new(new Fixed("dimension", "fail", 0.3), 0.1) { OnFailure = ComponentFailureEffect.Fail },
        };
        var result = await new CompositeEval("p", "P", "test", "1.0.0", components, WeightedSumAggregation.Instance, threshold: 0.6)
            .EvaluateAsync(Input);

        Assert.Equal("fail", result.Score.Label);
        Assert.Equal(MeasurementState.Measured, result.Score.CensusBucket());   // not "withheld": it failed
    }

    [Fact]
    public async Task TheEffect_NeverLiftsAVerdict()
    {
        // The average already fails (the dimension weighs 0.9): a Warn effect does not soften that to warn.
        var components = new List<EvalComponent>
        {
            new(new Fixed("a", "pass", 1.0), 0.1),
            new(new Fixed("dimension", "fail", 0.1), 0.9) { OnFailure = ComponentFailureEffect.Warn },
        };
        var result = await new CompositeEval("p", "P", "test", "1.0.0", components, WeightedSumAggregation.Instance, threshold: 0.7)
            .EvaluateAsync(Input);

        Assert.Equal("fail", result.Score.Label);
    }

    // ── B6c-3 (mid-branch review): a verdict reads the parts that did not pass, and a pass reports no severity ───────

    private sealed class WithSeverity(string key, string label, double value, string severity) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) => Task.FromResult(new EvalResult(
            new(key, key, "test", "1.0.0"),
            new EvalScore(value, null, label, label == "pass", null, severity, null),
            new(null, null, null, null, null),
            new("atomic-code", null, null, null, null, 0, false),
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task APassingPartCarryingAnAbsorbedSeverity_DoesNotFailTheParent()
    {
        // An article that passed its own threshold while one of its scenarios failed reported that scenario's "high":
        // the parent's severity cap then failed a preset in which every article passed.
        var parent = new CompositeEval("p", "P", "test", "1.0.0",
            [new EvalComponent(new WithSeverity("article", "pass", 0.86, "high"), 0.5), new EvalComponent(new Fixed("other", "pass", 1.0), 0.5)],
            WeightedSumAggregation.Instance, threshold: 0.8) { SeverityCapsThreshold = true };

        var result = await parent.EvaluateAsync(Input);

        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task APassingComposite_ReportsNoSeverity_AFailingOne_AtLeastMedium()
    {
        // Inside: one part fails at high but the composite passes; the other case fails on score with no failing part.
        var absorbing = new CompositeEval("absorbing", "A", "test", "1.0.0",
            [new EvalComponent(new WithSeverity("weak", "fail", 0.5, "high"), 0.1), new EvalComponent(new Fixed("strong", "pass", 1.0), 0.9)],
            WeightedSumAggregation.Instance, threshold: 0.8);
        var underBar = new CompositeEval("under", "U", "test", "1.0.0",
            [new EvalComponent(new Fixed("ok", "pass", 0.75), 1.0)],
            WeightedSumAggregation.Instance, threshold: 0.8);

        var passing = await absorbing.EvaluateAsync(Input);
        var failing = await underBar.EvaluateAsync(Input);

        Assert.Equal(("pass", "none"), (passing.Score.Label, passing.Score.Severity));
        Assert.Equal(("fail", "medium"), (failing.Score.Label, failing.Score.Severity));
    }

    [Fact]
    public async Task AMeasuredAccuracyFailure_IsTheVerdict_EvenWhenARequiredPartErrored()
    {
        // B6c-10 (mid-branch review): the errored part made the label "error" (exit 11) while the summary said "verdict is
        // fail" and "no pass/fail verdict is reported". More measurement cannot turn a measured accuracy failure into a pass.
        var components = new List<EvalComponent>
        {
            new(new Fixed("judge", "error", 0.0), 0.5),
            new(new WithSeverity("accuracy", "fail", 0.3, "high"), 0.5) { OnFailure = ComponentFailureEffect.Fail },
        };
        var result = await new CompositeEval("p", "P", "test", "1.0.0", components, WeightedSumAggregation.Instance, threshold: 0.7)
            .EvaluateAsync(Input);

        Assert.Equal("fail", result.Score.Label);
        Assert.Contains("Failed: accuracy", result.Details.Summary!, StringComparison.Ordinal);
        Assert.DoesNotContain("no pass/fail verdict is reported", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEffect_SurvivesACopy()
    {
        var component = new EvalComponent(new Fixed("x", "pass", 1.0), 0.5) { OnFailure = ComponentFailureEffect.Fail };

        Assert.Equal(ComponentFailureEffect.Fail, (component with { Weight = 0.25 }).OnFailure);
    }
}
