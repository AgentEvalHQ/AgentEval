// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AgentEval.Core;
using AgentEval.Exporters;
using AgentEval.Models;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Exporters;

/// <summary>
/// T71: a score, pass rate or time that was not recorded reaches no export as a 0. JSON and the directory files write
/// null, CSV an empty or "not measured" cell, JUnit and TRX leave the attribute out, Markdown says so. The input is a
/// report whose only test has no verdict and no timing, as <c>ToEvaluationReport</c> builds it.
/// </summary>
public sealed class NotRecordedExportTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "agenteval-t71-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    // A case whose judge produced no verdict, with no performance data: no score, no time, no report duration.
    private static EvaluationReport NothingRecorded() => new TestSummary("s",
    [
        new TestResult { TestName = "d", Score = 0, Passed = false, JudgeFailed = true, Details = "The judge produced no verdict" },
    ]).ToEvaluationReport();

    private static EvaluationReport Measured() => new TestSummary("s",
    [
        new TestResult
        {
            TestName = "a", Score = 90, Passed = true,
            Performance = new PerformanceMetrics { StartTime = T0, EndTime = T0.AddSeconds(2) },
        },
    ]).ToEvaluationReport();

    private static async Task<string> ExportAsync(IResultExporter exporter, EvaluationReport report)
    {
        using var stream = new MemoryStream();
        await exporter.ExportAsync(report, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ── JSON ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Json_WritesNull_NotZero()
    {
        // Today: "overallScore": 0, "durationMs": 0, the test's "score": 0 and "durationMs": 0, and no notMeasured field.
        using var doc = JsonDocument.Parse(await ExportAsync(new JsonExporter(), NothingRecorded()));
        var root = doc.RootElement;
        var test = root.GetProperty("results")[0];

        Assert.Equal(JsonValueKind.Null, root.GetProperty("overallScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("durationMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, test.GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.Null, test.GetProperty("durationMs").ValueKind);
        Assert.True(test.GetProperty("notMeasured").GetBoolean());
    }

    [Fact]
    public async Task Json_EmptyReport_HasNoPassRate()
    {
        // Today: "passRate": 0 for a report with no tests.
        using var doc = JsonDocument.Parse(await ExportAsync(new JsonExporter(), new TestSummary("s", []).ToEvaluationReport()));

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("stats").GetProperty("passRate").ValueKind);
    }

    [Fact]
    public async Task Json_MeasuredAndTimed_KeepsItsNumbers()
    {
        // Guard.
        using var doc = JsonDocument.Parse(await ExportAsync(new JsonExporter(), Measured()));
        var root = doc.RootElement;
        var test = root.GetProperty("results")[0];

        Assert.Equal(90, root.GetProperty("overallScore").GetDouble());
        Assert.Equal(2000, root.GetProperty("durationMs").GetInt64());
        Assert.Equal(100, root.GetProperty("stats").GetProperty("passRate").GetDouble());
        Assert.Equal(90, test.GetProperty("score").GetDouble());
        Assert.Equal(2000, test.GetProperty("durationMs").GetInt64());
        Assert.False(test.GetProperty("notMeasured").GetBoolean());
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Csv_WritesNoScoreOrDurationAsANumber()
    {
        // Today: Score "0.00" and DurationMs "0".
        var csv = await ExportAsync(new CsvExporter(), NothingRecorded());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var header = lines[0].Split(',');
        var row = lines[1].Split(',');

        Assert.Equal("not measured", row[Array.IndexOf(header, "Score")]);
        Assert.Equal("", row[Array.IndexOf(header, "DurationMs")]);
    }

    [Fact]
    public async Task Csv_MeasuredAndTimed_KeepsItsNumbers()
    {
        // Guard.
        var csv = await ExportAsync(new CsvExporter(), Measured());
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var header = lines[0].Split(',');
        var row = lines[1].Split(',');

        Assert.Equal("90.00", row[Array.IndexOf(header, "Score")]);
        Assert.Equal("2000", row[Array.IndexOf(header, "DurationMs")]);
    }

    // ── JUnit ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task JUnit_LeavesOutUnrecordedTimes_AndHasNoScore()
    {
        // Today: time="0.000" on testsuites, testsuite and testcase, and the overallScore property "0.0".
        var xml = XDocument.Parse(await ExportAsync(new JUnitXmlExporter(), NothingRecorded()));

        Assert.Null(xml.Root!.Attribute("time"));
        Assert.Null(xml.Descendants("testsuite").Single().Attribute("time"));
        Assert.Null(xml.Descendants("testcase").Single().Attribute("time"));
        var overallScore = xml.Descendants("property").Single(p => (string?)p.Attribute("name") == "overallScore");
        Assert.Equal("n/a", (string?)overallScore.Attribute("value"));
    }

    [Fact]
    public async Task JUnit_TimedTest_HasItsTime()
    {
        // Guard.
        var xml = XDocument.Parse(await ExportAsync(new JUnitXmlExporter(), Measured()));

        Assert.Equal("2.000", (string?)xml.Root!.Attribute("time"));
        Assert.Equal("2.000", (string?)xml.Descendants("testsuite").Single().Attribute("time"));
        Assert.Equal("2.000", (string?)xml.Descendants("testcase").Single().Attribute("time"));
    }

    // ── TRX ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Trx_LeavesOutAnUnrecordedDuration()
    {
        // Today: duration="00:00:00" and an endTime equal to the startTime.
        var xml = XDocument.Parse(await ExportAsync(new TrxExporter(), NothingRecorded()));

        var result = xml.Descendants().Single(e => e.Name.LocalName == "UnitTestResult");
        Assert.Null(result.Attribute("duration"));
        Assert.Null(result.Attribute("endTime"));
        Assert.NotNull(result.Attribute("startTime"));
    }

    [Fact]
    public async Task Trx_TimedTest_HasItsDuration()
    {
        // Guard.
        var xml = XDocument.Parse(await ExportAsync(new TrxExporter(), Measured()));

        var result = xml.Descendants().Single(e => e.Name.LocalName == "UnitTestResult");
        Assert.Equal("00:00:02.0000000", (string?)result.Attribute("duration"));
        Assert.NotNull(result.Attribute("endTime"));
    }

    private static string RunOutcome(XDocument xml) =>
        (string?)xml.Descendants().Single(e => e.Name.LocalName == "ResultSummary").Attribute("outcome") ?? "";

    [Fact]
    public async Task Trx_EmptyRun_IsInconclusive_NotPassed()
    {
        // D-31. Today: outcome="Passed" for a run with no tests, which CI reads as green.
        var xml = XDocument.Parse(await ExportAsync(new TrxExporter(), new TestSummary("s", []).ToEvaluationReport()));

        Assert.Equal("Inconclusive", RunOutcome(xml));
    }

    [Fact]
    public async Task Trx_RunWhoseEveryTestWasSkipped_IsInconclusive_NotPassed()
    {
        // D-31, same class: no test executed, so the run proved nothing. Today: outcome="Passed".
        var report = new EvaluationReport
        {
            Name = "s", TotalTests = 2, SkippedTests = 2,
            TestResults =
            [
                new TestResultSummary { Name = "a", Skipped = true },
                new TestResultSummary { Name = "b", Skipped = true },
            ],
        };

        var xml = XDocument.Parse(await ExportAsync(new TrxExporter(), report));

        Assert.Equal("Inconclusive", RunOutcome(xml));
        var counters = xml.Descendants().Single(e => e.Name.LocalName == "Counters");
        Assert.Equal("0", (string?)counters.Attribute("executed"));
        Assert.Equal("2", (string?)counters.Attribute("notExecuted"));
    }

    [Fact]
    public async Task Trx_RunOutcome_IsPassedOrFailed_WhenATestExecuted()
    {
        // Guard: a run with an executed passing test still passes; a run with a test that gave no verdict still fails.
        Assert.Equal("Passed", RunOutcome(XDocument.Parse(await ExportAsync(new TrxExporter(), Measured()))));
        Assert.Equal("Failed", RunOutcome(XDocument.Parse(await ExportAsync(new TrxExporter(), NothingRecorded()))));
    }

    // ── Markdown ──────────────────────────────────────────────────────────────

    [Fact]
    public void Markdown_SaysTheDurationWasNotRecorded()
    {
        // Today: "**Duration:** 0ms" and a row time of "0ms".
        var md = new MarkdownExporter().ExportToString(NothingRecorded());

        Assert.Contains("**Duration:** not recorded", md);
        Assert.Contains("| d | n/a | ⚠️ not measured | n/a |", md);
        Assert.DoesNotContain("0ms", md);
    }

    [Fact]
    public void Markdown_EmptyReport_HasNoPassRate_AndDidNotPass()
    {
        // Today: "(0%)" beside 0/0 tests, under "✅ PASSED".
        var md = new MarkdownExporter().ExportToString(new TestSummary("s", []).ToEvaluationReport());

        Assert.Contains("**Tests:** 0/0 passed", md);
        Assert.DoesNotContain("(0%)", md);
        Assert.Contains("**Status:** ⚠️ NO TESTS", md);
        Assert.DoesNotContain("PASSED", md);
    }

    // ── Directory (ADR-002) ───────────────────────────────────────────────────

    [Fact]
    public async Task Directory_WritesNull_NotZero()
    {
        // Today: summary.json overallScore 0 and duration "00:00:00"; results.jsonl score 0, durationMs 0 and no
        // notMeasured field; run.json duration "00:00:00".
        await new DirectoryExporter().ExportToDirectoryAsync(NothingRecorded(), _tempDir);

        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tempDir, DirectoryExporter.SummaryFileName)));
        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("overallScore").ValueKind);
        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("duration").ValueKind);

        var line = (await File.ReadAllLinesAsync(Path.Combine(_tempDir, DirectoryExporter.ResultsFileName)))
            .First(l => !string.IsNullOrWhiteSpace(l));
        using var test = JsonDocument.Parse(line);
        Assert.Equal(JsonValueKind.Null, test.RootElement.GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.Null, test.RootElement.GetProperty("durationMs").ValueKind);
        Assert.True(test.RootElement.GetProperty("notMeasured").GetBoolean());

        using var run = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tempDir, DirectoryExporter.RunFileName)));
        Assert.Equal(JsonValueKind.Null, run.RootElement.GetProperty("duration").ValueKind);
    }

    [Fact]
    public async Task Directory_EmptyReport_HasNoPassRate()
    {
        // Today: "passRate": 0 for a report with no tests.
        var json = await ExportAsync(new DirectoryExporter(), new TestSummary("s", []).ToEvaluationReport());
        using var summary = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("stats").GetProperty("passRate").ValueKind);
    }

    [Fact]
    public async Task Directory_MeasuredAndTimed_KeepsItsNumbers()
    {
        // Guard.
        await new DirectoryExporter().ExportToDirectoryAsync(Measured(), _tempDir);

        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_tempDir, DirectoryExporter.SummaryFileName)));
        Assert.Equal(90, summary.RootElement.GetProperty("overallScore").GetDouble());
        Assert.Equal("00:00:02", summary.RootElement.GetProperty("duration").GetString());
        Assert.Equal(1.0, summary.RootElement.GetProperty("stats").GetProperty("passRate").GetDouble());
    }

    [Fact]
    public async Task Directory_StoreExport_AScorelessTest_IsStoredAsNotMeasured()
    {
        // A test with no score but no NotMeasured flag (a hand-built report) would otherwise be stored as a measured 0
        // that `compare` averages in. Its untimed duration is stored as zero, the store's "not recorded".
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var manifest = await store.StartRunAsync(subject, new RunContext("TestProject", ".", "TestHarness", null, null, "eval"));
        var report = new EvaluationReport
        {
            RunId = manifest.Run.RunId,
            TotalTests = 1,
            FailedTests = 1,
            TestResults = [new TestResultSummary { Name = "x", Passed = false, Score = null, NotMeasured = false }],
        };

        await new DirectoryExporter().ExportThroughStoreAsync(store, subject, manifest, report);

        var scenarios = new List<ScenarioResult>();
        await foreach (var scenario in store.GetScenarioResultsAsync(manifest.Run.RunId))
            scenarios.Add(scenario);
        var stored = Assert.Single(scenarios);
        Assert.Equal(ScenarioMeasurement.NotMeasured, stored.Measurement);
        Assert.Equal(TimeSpan.Zero, stored.Duration);
    }
}
