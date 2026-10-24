// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// A scenario that errored, was skipped or had no judge verdict is stored as Score 0 / Passed false. That 0 is a
/// placeholder: <c>compare</c> leaves it out of the mean delta and lists the scenario instead (T13-5).
/// </summary>
public class RunComparisonNotMeasuredTests
{
    private static ScenarioResult Scenario(string id, double score, bool passed, string? measurement = null) =>
        new(id, id, "in", "out", passed, score, new Dictionary<string, double>(), [], TimeSpan.Zero, 0.0)
        {
            StimulusHash = "sha256:aaa",
            Comparability = new ComparabilityFacts("eval.k", "1.0.0") { EffectiveBar = 0.7, Judge = new JudgeFingerprint("gpt-5.5") },
            Measurement = measurement,
        };

    [Fact]
    public void AScenarioTheCandidateDidNotMeasure_HasNoDelta_InTheMean()
    {
        // Today the mean is -0.9: a score drop nobody measured.
        var c = RunComparison.Of(
            [Scenario("X", 0.9, passed: true)],
            [Scenario("X", 0.0, passed: false, ScenarioMeasurement.NotMeasured)]);

        Assert.Equal(ComparisonVerdict.Comparable, c.Verdict);
        Assert.Null(c.MeanScoreDelta);
        Assert.Equal(["X"], c.NotMeasuredScenarios);
        Assert.False(c.Scenarios.Single().BothMeasured);
    }

    [Fact]
    public void TheMean_IsOverTheScenariosBothRunsMeasured()
    {
        var c = RunComparison.Of(
            [Scenario("X", 0.5, passed: true), Scenario("Y", 0.9, passed: true)],
            [Scenario("X", 0.7, passed: true), Scenario("Y", 0.0, passed: false, ScenarioMeasurement.NotMeasured)]);

        Assert.Equal(0.2, c.MeanScoreDelta!.Value, 10);
        Assert.Equal(["Y"], c.NotMeasuredScenarios);
    }

    [Fact]
    public void AnUnmeasuredCandidate_AfterAPassingBaseline_StillCountsAsRegressed()
    {
        // Guard: "passed, then failed, errored or not run" is the regression gate's rule, and it stays.
        var c = RunComparison.Of(
            [Scenario("X", 0.9, passed: true)],
            [Scenario("X", 0.0, passed: false, ScenarioMeasurement.NotMeasured)]);

        Assert.Equal(1, c.Regressed);
    }

    [Fact]
    public void ScenariosWithNoMeasurementRecorded_CompareAsBefore()
    {
        // Guard: every scenario written before Measurement existed carries null, which reads as measured.
        var c = RunComparison.Of(
            [Scenario("X", 0.9, passed: true)],
            [Scenario("X", 0.0, passed: false)]);

        Assert.Equal(-0.9, c.MeanScoreDelta!.Value, 10);
        Assert.Empty(c.NotMeasuredScenarios);
    }

    [Theory]
    [InlineData("error", ScenarioMeasurement.NotMeasured)]
    [InlineData("skipped", ScenarioMeasurement.NotMeasured)]
    [InlineData("pass", null)]
    [InlineData("fail", null)]
    public void ToScenarioResult_RecordsWhetherTheScoreIsAMeasurement(string label, string? expected)
    {
        var result = new EvalResult(
            new EvalMetadata("eval.k", "K", "test", "1.0.0"),
            new EvalScore(label == "pass" ? 1 : 0, null, label, label == "pass", null, "none", null),
            new EvalDetails(null, null, null, null, null),
            new EvalProvenance("atomic-llm", "judge", null, null, null, 0, false),
            DateTimeOffset.UtcNow);

        var scenario = EvalResultPersistence.ToScenarioResult(result, "s1", "S1");

        Assert.Equal(expected, scenario.Measurement);
        Assert.Equal(expected is null, ScenarioMeasurement.IsMeasured(scenario));
    }
}
