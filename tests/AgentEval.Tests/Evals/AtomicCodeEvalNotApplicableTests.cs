// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Output;

namespace AgentEval.Tests.Evals;

/// <summary>
/// Task 1.2. <c>AtomicCodeEval.NotApplicable</c> is the undecidable verdict for the deterministic
/// lane, mirroring <see cref="EvalResult.Skipped"/> (ADR-030 D13). Both shipped consumers of the
/// join had hand-written the same five-positional record under a private <c>Undecidable</c> helper;
/// these tests pin the discipline that helper has to keep.
/// </summary>
public class AtomicCodeEvalNotApplicableTests
{
    private sealed class UndecidableEval() : AtomicCodeEval("undecidable_eval", "Undecidable", "test", "1.0.0")
    {
        public string Reason { get; set; } = "the tool was never called, so nothing could be checked";
        public EvalEvidence? Evidence { get; set; }

        protected override EvalResult Evaluate(EvalInput input) => NotApplicable(Reason, Evidence);
    }

    private sealed class MeasuredFailEval() : AtomicCodeEval("measured_fail", "Measured fail", "test", "1.0.0")
    {
        protected override EvalResult Evaluate(EvalInput input) => Build(0.0, passed: false, "none");
    }

    private static readonly EvalInput Input = new("q");

    [Fact]
    public async Task NotApplicable_IsNeverPassed_AndCensusesAsNotApplicable()
    {
        var result = await new UndecidableEval().EvaluateAsync(Input);

        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotApplicable, result.Score.CensusBucket());
        Assert.False(result.Score.CountsTowardAggregate());
    }

    [Fact]
    public async Task AMeasured0Fail_IsNotTheSameStateAsUndecidable()
    {
        // The distinction the helper exists to protect. A 0.0 fail says the eval LOOKED and found
        // nothing good; NotApplicable says it could not look. Both have Passed == false and
        // Value == 0.0, so only the census bucket separates them — and an absence recorded as a
        // measured zero is the defect this repository has shipped five times.
        var measured = await new MeasuredFailEval().EvaluateAsync(Input);
        var undecidable = await new UndecidableEval().EvaluateAsync(Input);

        Assert.Equal(measured.Score.Value, undecidable.Score.Value);
        Assert.Equal(measured.Score.Passed, undecidable.Score.Passed);

        Assert.Equal(MeasurementState.Measured, measured.Score.CensusBucket());
        Assert.Equal(MeasurementState.NotApplicable, undecidable.Score.CensusBucket());
        Assert.True(measured.Score.CountsTowardAggregate());
        Assert.False(undecidable.Score.CountsTowardAggregate());
    }

    [Fact]
    public async Task NotApplicable_CarriesTheReasonTwice()
    {
        // Renderers read one or the other. A reason nobody displays is a reason nobody acts on.
        var eval = new UndecidableEval { Reason = "no tool call named GetWeather was recorded" };

        var result = await eval.EvaluateAsync(Input);

        Assert.Equal("no tool call named GetWeather was recorded", result.Details.Summary);
        Assert.Equal(["no tool call named GetWeather was recorded"], result.Details.Recommendations);
    }

    [Fact]
    public async Task NotApplicable_CarriesOptionalEvidence_AndOmitsItWhenAbsent()
    {
        var withEvidence = new UndecidableEval { Evidence = new EvalEvidence("tool-calls", "GetWeather", "absent") };

        var yes = await withEvidence.EvaluateAsync(Input);
        var no = await new UndecidableEval().EvaluateAsync(Input);

        Assert.Single(yes.Details.Evidence!);
        Assert.Null(no.Details.Evidence);
    }

    [Fact]
    public async Task NotApplicable_ThroughTheDoor_CarriesTheFloorAndStaysUndecidable()
    {
        // An undecidable result still gets its floor stamped: "this eval could not measure anything"
        // and "what an arm that understands nothing would score" are independent facts, and a reader
        // comparing two runs needs both.
        var admitted = FloorAdmittedEval.Admit(new UndecidableEval(), ChanceFloor.UniformChoice(2));

        var result = await admitted.EvaluateAsync(Input);

        Assert.True(result.Details.Dimensions!.ContainsKey(ComparabilityFacts.ChanceFloorDimension));
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotApplicable, result.Score.CensusBucket());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NotApplicable_RefusesABlankReason(string blank)
    {
        // "Nobody could decide" and "nobody said why" are different facts, and only the reason
        // separates them.
        var eval = new UndecidableEval { Reason = blank };

        await Assert.ThrowsAsync<ArgumentException>(() => eval.EvaluateAsync(Input));
    }
}
