// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Evals.Agentic.Calibration;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// A calibration category or pillar whose entries were dispatched but none was scored has no accuracy or kappa: it is
/// NOT MEASURED, never a FAIL at the 0 of an empty set, and its report reads n/a (T13-13).
/// </summary>
public class CalibrateNotMeasuredTests
{
    private static readonly CalibrationJudgeIdentity Judge = new("Test Provider", "test-model-7");

    [Fact]
    public void AnAgenticCategory_WithNothingScored_IsNotMeasured_NotFail()
    {
        var report = new CalibrationCategoryReport("x", EntryCount: 0, Accuracy: 0, CohensKappa: 0, WithinScoreRange: 0,
            MeanScoreDelta: 0, EvaluationFailures: 0, SkippedUnknownKey: 0, NotMeasured: 0, NotApplicable: 5);

        Assert.Equal("NOT MEASURED", BenchAgenticCalibrateCommand.CategoryStatus(report, 0.8, 0.6));
    }

    [Fact]
    public void AnAgenticCategory_WithScoredEntries_IsStillPassOrFail()
    {
        // Guard: a scored category keeps its measured verdict.
        var report = new CalibrationCategoryReport("x", EntryCount: 10, Accuracy: 0.5, CohensKappa: 0.2, WithinScoreRange: 5,
            MeanScoreDelta: 0.1, NotApplicable: 5);

        Assert.Equal("FAIL", BenchAgenticCalibrateCommand.CategoryStatus(report, 0.8, 0.6));
    }

    [Fact]
    public void TheAgenticReport_ShowsNoAccuracyKappaOrDelta_ForACategoryWithNothingScored()
    {
        var report = new CalibrationReport(DateTimeOffset.UtcNow, new Dictionary<string, CalibrationCategoryReport>
        {
            ["x"] = new("x", EntryCount: 0, Accuracy: 0, CohensKappa: 0, WithinScoreRange: 0, MeanScoreDelta: 0, NotApplicable: 5),
        });

        var markdown = BenchAgenticCalibrateCommand.BuildMarkdownReport(report, Judge);

        Assert.Contains("## x [NOT MEASURED]", markdown, StringComparison.Ordinal);
        Assert.Contains("| Accuracy | n/a |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Cohen's kappa | n/a |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Mean score delta | n/a |", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0, false, false, 0, "NOT MEASURED")]   // every record inapplicable: nothing scored
    [InlineData(0, 0, false, false, 10, "FAIL")]          // a measured fail stays a fail
    [InlineData(0, 1, false, false, 0, "INCOMPLETE")]     // a withheld record is still INCOMPLETE first
    [InlineData(1, 0, false, false, 0, "INFRA-FAIL")]     // and an evaluation failure before that
    public void APillar_WithNothingScored_IsNotMeasured(int failures, int notMeasured, bool accOk, bool kappaOk, int scored, string expected) =>
        Assert.Equal(expected, BenchCalibrateCommand.PillarGateStatus(failures, notMeasured, accOk, kappaOk, scored));

    [Theory]
    [InlineData(new[] { "PASS", "NOT MEASURED" }, AgentEval.Cli.ExitCodes.GateIndeterminate)]   // was 9, a failed calibration
    [InlineData(new[] { "NOT MEASURED" }, AgentEval.Cli.ExitCodes.GateIndeterminate)]
    [InlineData(new[] { "FAIL", "NOT MEASURED" }, AgentEval.Cli.ExitCodes.GateFailed)]           // a failure still wins
    [InlineData(new[] { "INCOMPLETE", "NOT MEASURED" }, AgentEval.Cli.ExitCodes.GateFailed)]
    [InlineData(new[] { "INFRA-FAIL", "PASS" }, AgentEval.Cli.ExitCodes.GateFailed)]
    [InlineData(new[] { "PASS", "PASS" }, AgentEval.Cli.ExitCodes.Success)]
    public void TheCalibrationGate_ExitsInconclusive_WhenNothingWasScoredAndNothingFailed(string[] statuses, int exitCode) =>
        Assert.Equal(exitCode, BenchCalibrateCommand.GateExitCode(statuses));

    [Fact]
    public void BothComplianceReports_ShowNotMeasured_ForAPillarWithNothingScored()
    {
        var gdpr = new AgentEval.Compliance.Gdpr.Calibration.CalibrationReport(DateTimeOffset.UtcNow,
            new Dictionary<string, AgentEval.Compliance.Gdpr.Calibration.CalibrationPillarReport>
            {
                ["p1"] = new("p1", EntryCount: 0, Accuracy: 0, CohensKappa: 0, WithinScoreRange: 0, MeanScoreDelta: 0,
                    EvaluationFailures: 0, NotMeasured: 0, NotApplicable: 4),
            });
        var eu = new AgentEval.Compliance.EuAiAct.Calibration.CalibrationReport(DateTimeOffset.UtcNow,
            new Dictionary<string, AgentEval.Compliance.EuAiAct.Calibration.CalibrationPillarReport>
            {
                ["p1"] = new("p1", EntryCount: 0, Accuracy: 0, CohensKappa: 0, WithinScoreRange: 0, MeanScoreDelta: 0,
                    EvaluationFailures: 0, NotMeasured: 0, NotApplicable: 4),
            });

        foreach (var markdown in new[] { BenchCalibrateCommand.BuildMarkdownReport(gdpr, Judge),
                                         BenchEuAiActCalibrateCommand.BuildMarkdownReport(eu, Judge) })
        {
            Assert.Contains("## p1 [NOT MEASURED]", markdown, StringComparison.Ordinal);
            Assert.Contains("| Accuracy | n/a |", markdown, StringComparison.Ordinal);
            Assert.Contains("| Mean score delta | n/a |", markdown, StringComparison.Ordinal);
        }
    }
}
