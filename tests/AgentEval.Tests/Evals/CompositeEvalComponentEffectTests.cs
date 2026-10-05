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

    [Theory]
    [InlineData(ComponentFailureEffect.Averaged, "pass")]   // 0.8 clears 0.5: the nested failure is averaged out
    [InlineData(ComponentFailureEffect.Warn, "warn")]
    [InlineData(ComponentFailureEffect.Fail, "fail")]
    public async Task AThresholdParent_WithARequiredNestedFailure_DoesWhatItsEffectSays(ComponentFailureEffect effect, string parentLabel)
    {
        // Round 2 L-3: a required NESTED composite that failed — measured, scoring 0.6 against its own 0.8 bar — under a
        // parent whose threshold (0.5) the average still clears. The parent's verdict is the component's effect, not
        // an accident of the average; Averaged (the default for your own composites) is the documented old behaviour.
        var child = new CompositeEval("child", "Child", "test", "1.0.0",
            [new EvalComponent(new Fixed("x", "fail", 0.6), 1.0)], WeightedSumAggregation.Instance, threshold: 0.8);
        var parent = new CompositeEval("parent", "Parent", "test", "1.0.0",
            [new EvalComponent(child, 0.5) { OnFailure = effect }, new EvalComponent(new Fixed("sibling", "pass", 1.0), 0.5)],
            WeightedSumAggregation.Instance, threshold: 0.5);

        var childResult = await child.EvaluateAsync(Input);
        var result = await parent.EvaluateAsync(Input);

        Assert.Equal("fail", childResult.Score.Label);
        Assert.Equal(AgentEval.Evals.Meta.MeasurementState.Measured, childResult.Score.Measurement);   // a measured failure, not a withheld pass
        Assert.Equal(parentLabel, result.Score.Label);
        if (effect != ComponentFailureEffect.Averaged)
            Assert.Contains("child", result.Details.Summary);
    }

    [Theory]
    [InlineData(ComponentFailureEffect.Averaged, "pass")]   // documented: averaged like any score (review round 3 M5)
    [InlineData(ComponentFailureEffect.Warn, "warn")]
    [InlineData(ComponentFailureEffect.Fail, "warn")]       // a component that only warned passes a warn up, never a fail
    public async Task ANestedWarn_UnderASeverityPathParent_DoesWhatItsEffectSays(ComponentFailureEffect effect, string parentLabel)
    {
        // The child warns: a Warn-effect dimension failed at low severity. Under the default Averaged effect the parent
        // (no threshold: the severity rule) averages it like any score and passes; an effect carries the warn up. Pinned
        // so a change to Averaged's meaning is a decision, not an accident.
        var child = new CompositeEval("child", "Child", "test", "1.0.0",
            [new EvalComponent(new Fixed("ok", "pass", 1.0), 0.5),
             new EvalComponent(new WithSeverity("style", "fail", 0.6, "low"), 0.5) { OnFailure = ComponentFailureEffect.Warn }],
            WeightedSumAggregation.Instance, threshold: null);
        var parent = new CompositeEval("parent", "Parent", "test", "1.0.0",
            [new EvalComponent(child, 1.0) { OnFailure = effect }], WeightedSumAggregation.Instance, threshold: null);

        Assert.Equal("warn", (await child.EvaluateAsync(Input)).Score.Label);
        Assert.Equal(parentLabel, (await parent.EvaluateAsync(Input)).Score.Label);
    }

    [Fact]
    public async Task AnAveragedPass_NamesWhatItAbsorbed_ButAnEffectOrACleanPassDoesNot()
    {
        // The owner's decision on B10h (B10t): Averaged keeps its verdict, but a pass never hides a part whose own verdict
        // was warn or fail — the summary names it.
        var child = new CompositeEval("child", "Child", "test", "1.0.0",
            [new EvalComponent(new Fixed("ok", "pass", 1.0), 0.5),
             new EvalComponent(new WithSeverity("style", "fail", 0.6, "low"), 0.5) { OnFailure = ComponentFailureEffect.Warn }],
            WeightedSumAggregation.Instance, threshold: null);
        var averaged = new CompositeEval("parent", "Parent", "test", "1.0.0",
            [new EvalComponent(child, 1.0)], WeightedSumAggregation.Instance, threshold: null);
        var withEffect = new CompositeEval("parent", "Parent", "test", "1.0.0",
            [new EvalComponent(child, 1.0) { OnFailure = ComponentFailureEffect.Warn }], WeightedSumAggregation.Instance, threshold: null);
        var flat = new CompositeEval("flat", "Flat", "test", "1.0.0",
            [new EvalComponent(new Fixed("a", "pass", 1.0), 0.5), new EvalComponent(new Fixed("b", "fail", 0.6), 0.5)],
            WeightedSumAggregation.Instance, threshold: 0.7);   // 0.80 clears the bar
        var clean = new CompositeEval("clean", "Clean", "test", "1.0.0",
            [new EvalComponent(new Fixed("a", "pass", 1.0), 1.0)], WeightedSumAggregation.Instance, threshold: 0.7);

        var nested = await averaged.EvaluateAsync(Input);
        Assert.Equal("pass", nested.Score.Label);                                   // the verdict is unchanged
        Assert.Contains("Absorbed by the score", nested.Details.Summary);
        Assert.Contains("child (warn)", nested.Details.Summary);

        var flatResult = await flat.EvaluateAsync(Input);
        Assert.Equal("pass", flatResult.Score.Label);
        Assert.Contains("b (fail)", flatResult.Details.Summary);

        Assert.Null((await clean.EvaluateAsync(Input)).Details.Summary);              // nothing absorbed, nothing said
        Assert.DoesNotContain("Absorbed", (await withEffect.EvaluateAsync(Input)).Details.Summary ?? "");   // the effect names it
    }

    [Fact]
    public async Task DecidedBySeverity_NamesOnlyWhatTheSeverityRuleDecided()
    {
        // Review round 7 L1 (B10ah): the note named every medium+ Averaged part, even when a threshold the score missed
        // decided the fail, and a medium part beside the high one that did.
        var thresholdMissed = new CompositeEval("t", "T", "test", "1.0.0",
            [new EvalComponent(new Fixed("a", "pass", 1.0), 1.0), new EvalComponent(new WithSeverity("b", "fail", 0.3, "medium"), 1.0)],
            WeightedSumAggregation.Instance, threshold: 0.85) { SeverityCapsThreshold = true };   // 0.65 < 0.85
        var highAndMedium = new CompositeEval("h", "H", "test", "1.0.0",
            [new EvalComponent(new WithSeverity("hi", "fail", 0.2, "high"), 1.0), new EvalComponent(new WithSeverity("med", "fail", 0.5, "medium"), 1.0)],
            WeightedSumAggregation.Instance, threshold: null);

        var t = await thresholdMissed.EvaluateAsync(Input);
        var h = await highAndMedium.EvaluateAsync(Input);

        Assert.Equal("fail", t.Score.Label);
        Assert.DoesNotContain("Decided by severity", t.Details.Summary ?? "");
        Assert.Equal("fail", h.Score.Label);
        Assert.Contains("Decided by severity: hi (fail, high)", h.Details.Summary);
        Assert.DoesNotContain("med (fail", h.Details.Summary);
    }

    [Fact]
    public async Task DecidedBySeverity_NamesAHighPart_BesideAThresholdOrAFailEffectThatAlsoDecided()
    {
        // Review round 8 L1 (B10am): B10ah dropped the note whenever a threshold or a Fail effect also decided, so a high
        // part that alone fails the composite was named nowhere.
        var thresholdAndSeverity = new CompositeEval("t", "T", "test", "1.0.0",
            [new EvalComponent(new Fixed("a", "pass", 1.0), 1.0), new EvalComponent(new WithSeverity("hi", "fail", 0.2, "high"), 1.0)],
            WeightedSumAggregation.Instance, threshold: 0.85) { SeverityCapsThreshold = true };   // 0.60 < 0.85, and high
        var effectAndSeverity = new CompositeEval("e", "E", "test", "1.0.0",
            [new EvalComponent(new Fixed("gate", "fail", 0.3), 1.0) { OnFailure = ComponentFailureEffect.Fail },
             new EvalComponent(new WithSeverity("hi", "fail", 0.2, "high"), 1.0)],
            WeightedSumAggregation.Instance, threshold: null);

        Assert.Contains("Decided by severity: hi (fail, high)", (await thresholdAndSeverity.EvaluateAsync(Input)).Details.Summary);
        Assert.Contains("Decided by severity: hi (fail, high)", (await effectAndSeverity.EvaluateAsync(Input)).Details.Summary);
    }

    [Fact]
    public void ALabelIsStoredLowerCase_OnBothTheConstructorAndTheCopyPath()
    {
        // Review round 7 M-C (B10ag): the measurement predicates compared labels literally, so "Skipped"/"Error" counted
        // as measured while ReportStatus lowercased them. One normal form, on both paths (the AE-08 pattern).
        var built = new EvalScore(0.0, null, "Skipped", false, null, "none", null);
        var copied = new EvalScore(0.9, null, "pass", true, null, "none", null) with { Label = "Error", Passed = false };

        Assert.Equal("skipped", built.Label);
        Assert.Equal("error", copied.Label);
        Assert.False(built.CountsTowardAggregate());
        Assert.False(copied.CountsTowardAggregate());
    }

    [Fact]
    public void TheSerializedScore_KeepsItsPropertyOrder()
    {
        // Review round 8 L3 (B10am): declaring Label explicitly (B10ag) moved it after Severity, so every new result's
        // bytes differed from an identical stored one. The order every stored result has is pinned here.
        var json = System.Text.Json.JsonSerializer.Serialize(new EvalScore(0.5, null, "warn", false, 0.7, "medium", 0.9),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.StartsWith("""{"ordinal":null,"label":"warn","severity":"medium","value":0.5,"threshold":0.7,"confidence":0.9,"passed":false""", json);
    }

    [Theory]
    [InlineData("Skipped", "warn")]   // a required part that did not run withholds the pass
    [InlineData("Error", "error")]    // a required part that errored leaves no verdict
    public async Task ACapitalisedNoVerdictLabel_IsReadLikeItsLowerCaseForm(string label, string expected)
    {
        var composite = new CompositeEval("c", "C", "test", "1.0.0",
            [new EvalComponent(new Fixed("ok", "pass", 1.0), 1.0),
             new EvalComponent(new Fixed("other", label, 0.0), 1.0)],
            WeightedSumAggregation.Instance, threshold: 0.4);   // counted as a measured 0, the mean 0.5 cleared the bar

        Assert.Equal(expected, (await composite.EvaluateAsync(Input)).Score.Label);   // it read pass, saying nothing
    }

    [Fact]
    public async Task ACustomNonPassingLabel_FailsASecurityGate()
    {
        // Review round 6 (B10aa sweep): EvalScore.Label is a free string. A custom check's measured "needs-review" (not
        // passed) is a FAIL by ReportStatus, but the effects read "fail"/"warn" literally, so FailUnlessPass never fired
        // and the gate passed on its 0.95.
        var gate = new CompositeEval("gate", "Gate", "test", "1.0.0",
            [new EvalComponent(new Fixed("custom", "needs-review", 0.95), 1.0) { OnFailure = ComponentFailureEffect.FailUnlessPass }],
            WeightedSumAggregation.Instance, threshold: 0.90);

        var result = await gate.EvaluateAsync(Input);

        Assert.Equal("fail", result.Score.Label);
        Assert.False(result.Score.Passed);
    }

    [Fact]
    public async Task APartWhoseSeverityDecidedTheWarn_IsNamedAsTheReason_NotAsAbsorbed()
    {
        // Review round 6 M-1 (B10z): under the severity rule (every GDPR/EU pillar) a medium failure decides a warn, and
        // the note called that part "absorbed by the average". It now says what decided it.
        var pillar = new CompositeEval("pillar", "Pillar", "test", "1.0.0",
            [new EvalComponent(new Fixed("a", "pass", 1.0), 1.0),
             new EvalComponent(new WithSeverity("b", "fail", 0.5, "medium"), 1.0)],
            WeightedSumAggregation.Instance, threshold: null);
        var lowOnly = new CompositeEval("low", "Low", "test", "1.0.0",
            [new EvalComponent(new Fixed("a", "pass", 1.0), 1.0),
             new EvalComponent(new WithSeverity("b", "fail", 0.5, "low"), 1.0)],
            WeightedSumAggregation.Instance, threshold: null);

        var decided = await pillar.EvaluateAsync(Input);
        var absorbed = await lowOnly.EvaluateAsync(Input);

        Assert.Equal("warn", decided.Score.Label);
        Assert.DoesNotContain("Absorbed", decided.Details.Summary ?? "");
        Assert.Contains("Decided by severity: b (fail, medium)", decided.Details.Summary);
        Assert.Equal("pass", absorbed.Score.Label);                                   // low severity cannot change the label
        Assert.Contains("Absorbed by the score (OnFailure = Averaged): b (fail)", absorbed.Details.Summary);
    }

    [Fact]
    public async Task TheSummary_NeverStatesAVerdictTheLabelContradicts()
    {
        // Review round 3 M1: the threshold fails this composite (0.45 < 0.8) while only a Warn-effect dimension failed; the
        // summary said "…so the verdict is warn, not fail" beside the label fail.
        var composite = new CompositeEval("c", "C", "test", "1.0.0",
            [new EvalComponent(new Fixed("accuracy", "pass", 0.6), 0.5) { OnFailure = ComponentFailureEffect.Fail },
             new EvalComponent(new Fixed("fluency", "fail", 0.3), 0.5) { OnFailure = ComponentFailureEffect.Warn }],
            WeightedSumAggregation.Instance, threshold: 0.8);

        var result = await composite.EvaluateAsync(Input);

        Assert.Equal("fail", result.Score.Label);
        Assert.Contains("fluency", result.Details.Summary);
        Assert.DoesNotContain("verdict is warn", result.Details.Summary);
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
