// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Exporters;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Models;

/// <summary>
/// A batch case whose judge produced no verdict, or whose run threw, carries Score 0 / Passed false as placeholders. It
/// is not measured: left out of the average and counted apart from the failures (T13-4). JUnit and TRX export it as an
/// error, so CI stays red; the Markdown report reads INCOMPLETE.
/// </summary>
public class TestSummaryNotMeasuredTests
{
    private static TestResult Scored(string name, int score, bool passed) =>
        new() { TestName = name, Score = score, Passed = passed };

    private static TestResult JudgeFailed(string name) =>
        new() { TestName = name, Score = 0, Passed = false, JudgeFailed = true, Details = "The judge produced no verdict" };

    private static TestResult Errored(string name) =>
        new() { TestName = name, Score = 0, Passed = false, Error = new HttpRequestException("429 Too Many Requests") };

    [Fact]
    public void AJudgeFailure_IsLeftOutOfTheAverage_AndIsNotAFailure()
    {
        // Today: AverageScore 67.5 and FailedCount 1.
        var summary = new TestSummary("s", [Scored("a", 90, true), Scored("b", 90, true), Scored("c", 90, true), JudgeFailed("d")]);

        Assert.Equal(90, summary.AverageScore);
        Assert.Equal(1, summary.NotMeasuredCount);
        Assert.Equal(3, summary.MeasuredCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.False(summary.AllPassed);   // a case with no verdict did not pass
    }

    [Fact]
    public void AnErroredRun_IsNotMeasuredEither()
    {
        var summary = new TestSummary("s", [Scored("a", 80, true), Errored("b")]);

        Assert.Equal(80, summary.AverageScore);
        Assert.Equal(1, summary.NotMeasuredCount);
        Assert.Equal(0, summary.FailedCount);
    }

    [Fact]
    public void AMeasuredFailure_IsStillAFailure()
    {
        // Guard: a low score the judge gave is a measured fail and stays in the average.
        var summary = new TestSummary("s", [Scored("a", 90, true), Scored("b", 30, false), JudgeFailed("c")]);

        Assert.Equal(1, summary.FailedCount);
        Assert.Equal(60, summary.AverageScore);
    }

    [Fact]
    public void WhenNothingWasMeasured_ThereIsNoScore()
    {
        var summary = new TestSummary("s", [JudgeFailed("a"), Errored("b")]);

        Assert.False(summary.HasMeasuredScore);
        Assert.Equal(0, summary.MeasuredCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.False(summary.AllPassed);
    }

    [Fact]
    public void TheReport_ScoresTheMeasuredCases_AndSkipsTheOneWithNoVerdict()
    {
        var summary = new TestSummary("s", [Scored("a", 90, true), Scored("b", 90, true), Scored("c", 90, true), JudgeFailed("d")]);

        var report = summary.ToEvaluationReport();

        Assert.Equal(90, report.OverallScore);
        Assert.Equal(1, report.SkippedTests);
        Assert.Equal(0, report.FailedTests);
        var notMeasured = report.TestResults.Single(t => t.Name == "d");
        Assert.True(notMeasured.NotMeasured);
        Assert.True(notMeasured.Skipped);
        Assert.Contains("no verdict", notMeasured.Error);
        Assert.All(report.TestResults.Where(t => t.Name != "d"), t => Assert.False(t.NotMeasured));
    }

    [Fact]
    public async Task TheJUnitExport_ReportsTheCaseWithNoVerdictAsAnError_NotAFailureOrASkip()
    {
        // A skip reads as "not run" in CI and keeps the build green; an error keeps it red without calling it a failure.
        // Today: errors="0" and a <skipped> element.
        var report = new TestSummary("s", [Scored("a", 90, true), JudgeFailed("d")]).ToEvaluationReport();

        using var stream = new MemoryStream();
        await new JUnitXmlExporter().ExportAsync(report, stream);
        var xml = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("failures=\"0\"", xml);
        Assert.Contains("errors=\"1\"", xml);
        Assert.Contains("skipped=\"0\"", xml);
        Assert.DoesNotContain("errors=\"0\"", xml);
        Assert.Contains("<error message=\"The judge produced no verdict\" type=\"NotMeasured\"", xml);
        Assert.DoesNotContain("<failure", xml);
        Assert.DoesNotContain("<skipped", xml);
    }

    [Fact]
    public async Task TheTrxExport_ReportsTheCaseWithNoVerdictAsAnError_AndTheRunAsFailed()
    {
        // Today: the case is NotExecuted and the run's outcome Passed.
        var report = new TestSummary("s", [Scored("a", 90, true), JudgeFailed("d")]).ToEvaluationReport();

        using var stream = new MemoryStream();
        await new TrxExporter().ExportAsync(report, stream);
        var xml = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("outcome=\"Failed\"", xml);   // the run's ResultSummary: no case itself failed
        Assert.Contains("error=\"1\"", xml);
        Assert.Contains("notExecuted=\"0\"", xml);
        Assert.Contains("executed=\"2\"", xml);
        Assert.Contains("outcome=\"Error\"", xml);
        Assert.DoesNotContain("outcome=\"NotExecuted\"", xml);
        Assert.Contains("The judge produced no verdict", xml);
    }

    [Fact]
    public async Task AGenuinelySkippedCase_IsStillASkip_InJUnitAndTrx()
    {
        // Guard: only a case with no verdict becomes an error.
        var report = new TestSummary("s", [Scored("a", 90, true), Scored("b", 90, true)]).ToEvaluationReport();
        report.TestResults[1].Skipped = true;
        report.TestResults[1].Passed = false;
        report.SkippedTests = 1;

        using var junit = new MemoryStream();
        await new JUnitXmlExporter().ExportAsync(report, junit);
        using var trx = new MemoryStream();
        await new TrxExporter().ExportAsync(report, trx);

        Assert.Contains("<skipped", Encoding.UTF8.GetString(junit.ToArray()));
        Assert.Contains("errors=\"0\"", Encoding.UTF8.GetString(junit.ToArray()));
        Assert.Contains("outcome=\"NotExecuted\"", Encoding.UTF8.GetString(trx.ToArray()));
        Assert.DoesNotContain("outcome=\"Failed\"", Encoding.UTF8.GetString(trx.ToArray()));   // the run passed
        Assert.DoesNotContain("outcome=\"Error\"", Encoding.UTF8.GetString(trx.ToArray()));
    }

    [Fact]
    public void TheMarkdownReport_WithACaseThatHasNoVerdict_IsIncomplete_NotPassed()
    {
        // Today: "✅ PASSED", since nothing failed.
        var report = new TestSummary("s", [Scored("a", 90, true), JudgeFailed("d")]).ToEvaluationReport();

        var md = new MarkdownExporter().ExportToString(report);

        Assert.Contains("INCOMPLETE (1 not measured)", md);
        Assert.DoesNotContain("PASSED", md);
        Assert.Contains("| d | n/a | ⚠️ not measured |", md);
    }

    [Fact]
    public void TheMarkdownReport_WithNothingMeasured_HasNoScore()
    {
        var report = new TestSummary("s", [JudgeFailed("a"), Errored("b")]).ToEvaluationReport();

        var md = new MarkdownExporter().ExportToString(report);

        Assert.Contains("**Score:** n/a (nothing measured)", md);
        Assert.DoesNotContain("**Score:** 0.0/100", md);
    }
}
