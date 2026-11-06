// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Readiness;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Readiness;

/// <summary>
/// The three renderings of a readiness report —<c>readiness.md</c> (header, stage table, evidence per stage,
/// what to send back), <c>readiness.json</c> and the console view — and the report's own verdict rules. Built from a
/// hand-made report, so no transport is involved.
/// </summary>
public sealed class ReadinessReportRendererTests
{
    private const string Jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJhcGk6Ly93b3JraXEifQ.c2lnbmF0dXJlLXNpZ25hdHVyZQ";

    private static ReadinessReport SampleReport(params ReadinessStageResult[] stages) => new(stages)
    {
        RunId = "m365-20261105-140000-abcd",
        StartedAt = new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero),
        EndedAt = new DateTimeOffset(2026, 11, 5, 14, 0, 7, TimeSpan.Zero),
        TraceFile = "/tmp/doctor/m365-trace-m365-20261105-140000-abcd.jsonl",
        IssueFile = "/tmp/doctor/m365-issues.jsonl",
        ReportDirectory = "/tmp/doctor",
        ConfigSha256 = new string('a', 64),
        AdapterVersion = "1.0.0-rc.1",
        Environment = new ReadinessEnvironment("Test OS", ".NET 10.0.0", "Europe/Zurich"),
    };

    private static ReadinessReport FailedAuthReport() => SampleReport(
        new ReadinessStageResult(ReadinessStage.Config, ReadinessResult.Pass, "config valid")
        {
            Evidence = [new("endpoint", "https://workiq.svc.cloud.microsoft/a2a/"), new("tenant id", "11111111-1111-1111-1111-111111111111")],
            Meaning = "The config is complete.",
            Action = "Nothing to do.",
            ElapsedMs = 3,
        },
        new ReadinessStageResult(ReadinessStage.Auth, ReadinessResult.Fail, "sign-in failed with AADSTS65001")
        {
            Evidence = [new("AADSTS code", "AADSTS65001")],
            Meaning = "Consent | missing for WorkIQAgent.Ask.",
            Action = "An administrator grants admin consent.",
            ElapsedMs = 120,
            Issue = WorkIqIssue.Create(WorkIqTraceStage.Auth, WorkIqIssueCategory.Authentication, "auth.aadsts", "MSAL failure", "AADSTS65001: not consented") with { AadstsCode = "AADSTS65001" },
        },
        new ReadinessStageResult(ReadinessStage.Discovery, ReadinessResult.Skipped, "not run: the auth stage failed") { SkipReason = "not run: the auth stage failed" },
        new ReadinessStageResult(ReadinessStage.Protocol, ReadinessResult.Skipped, "not run: the auth stage failed") { SkipReason = "not run: the auth stage failed" },
        new ReadinessStageResult(ReadinessStage.Probe, ReadinessResult.Skipped, "not requested") { SkipReason = "not requested", Requested = false },
        new ReadinessStageResult(ReadinessStage.Census, ReadinessResult.Skipped, "not requested") { SkipReason = "not requested", Requested = false });

    [Fact]
    public void ToMarkdown_HasTheHeaderTheStageTableTheEvidenceAndWhatToSendBack()
    {
        var md = ReadinessReportRenderer.ToMarkdown(FailedAuthReport());

        Assert.StartsWith("# Microsoft 365 readiness report", md, StringComparison.Ordinal);
        Assert.Contains("**Result: FAIL**", md, StringComparison.Ordinal);
        Assert.Contains("| # | Stage | Result | Summary | What it means | What to do |", md, StringComparison.Ordinal);
        Assert.Contains("| 1 | config | **PASS** | config valid |", md, StringComparison.Ordinal);
        Assert.Contains("| 2 | auth | **FAIL** | sign-in failed with AADSTS65001 |", md, StringComparison.Ordinal);
        Assert.Contains("| 3 | discovery | **SKIPPED** | not run: the auth stage failed |", md, StringComparison.Ordinal);
        Assert.Contains("Consent \\| missing", md, StringComparison.Ordinal);
        Assert.Contains("### 2. auth — FAIL", md, StringComparison.Ordinal);
        Assert.Contains("- **AADSTS code**: AADSTS65001", md, StringComparison.Ordinal);
        Assert.Contains("- **Issue**: Authentication · `auth.aadsts` · `AADSTS65001`", md, StringComparison.Ordinal);
        Assert.Contains("## What to send back", md, StringComparison.Ordinal);
        Assert.Contains("`/tmp/doctor`", md, StringComparison.Ordinal);
        Assert.Contains("`/tmp/doctor/m365-trace-m365-20261105-140000-abcd.jsonl`", md, StringComparison.Ordinal);
        Assert.Contains("`/tmp/doctor/m365-issues.jsonl`", md, StringComparison.Ordinal);
        Assert.Contains(ReadinessReportRenderer.NoTokenStatement, md, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", md, StringComparison.Ordinal);
    }

    [Fact]
    public void ToJson_ParsesAndListsEveryStageInOrder()
    {
        var report = FailedAuthReport();

        var json = ReadinessReportRenderer.ToJson(report);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(ReadinessReport.SchemaVersion, root.GetProperty("schema").GetString());
        Assert.Equal("m365-20261105-140000-abcd", root.GetProperty("runId").GetString());
        Assert.Equal("FAIL", root.GetProperty("result").GetString());
        Assert.True(root.GetProperty("anyFailed").GetBoolean());
        Assert.False(root.GetProperty("allPassed").GetBoolean());
        Assert.Equal("/tmp/doctor/m365-issues.jsonl", root.GetProperty("issueFile").GetString());
        Assert.Equal("Europe/Zurich", root.GetProperty("environment").GetProperty("machineTimeZone").GetString());

        var stages = root.GetProperty("stages").EnumerateArray().ToArray();
        Assert.Equal(new[] { "config", "auth", "discovery", "protocol", "probe", "census" }, stages.Select(s => s.GetProperty("stage").GetString()).ToArray());
        Assert.Equal(new[] { "PASS", "FAIL", "SKIPPED", "SKIPPED", "SKIPPED", "SKIPPED" }, stages.Select(s => s.GetProperty("result").GetString()).ToArray());

        var auth = stages[1];
        Assert.Equal("AADSTS65001", auth.GetProperty("issue").GetProperty("aadstsCode").GetString());
        Assert.Equal("Authentication", auth.GetProperty("issue").GetProperty("category").GetString());
        var evidence = Assert.Single(auth.GetProperty("evidence").EnumerateArray());
        Assert.Equal("AADSTS code", evidence.GetProperty("name").GetString());
        Assert.Equal("AADSTS65001", evidence.GetProperty("value").GetString());
        Assert.Equal("not run: the auth stage failed", stages[2].GetProperty("skipReason").GetString());
        Assert.False(stages[4].GetProperty("requested").GetBoolean());
    }

    [Fact]
    public void WriteConsole_OneLinePerStage_TheFailureDetailsAndTheVerdict()
    {
        using var writer = new StringWriter();

        ReadinessReportRenderer.WriteConsole(FailedAuthReport(), writer);

        var text = writer.ToString();
        Assert.Contains("[PASS]", text, StringComparison.Ordinal);
        Assert.Contains("[FAIL]", text, StringComparison.Ordinal);
        Assert.Contains("[SKIPPED]", text, StringComparison.Ordinal);
        Assert.Contains("issue:   Authentication auth.aadsts AADSTS65001", text, StringComparison.Ordinal);
        Assert.Contains("do:      An administrator grants admin consent.", text, StringComparison.Ordinal);
        Assert.Contains("Result: FAIL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Renderings_RedactATokenThatSlippedIntoAValue()
    {
        var report = SampleReport(new ReadinessStageResult(ReadinessStage.Config, ReadinessResult.Pass, "Bearer " + Jwt)
        {
            Evidence = [new("header", "Authorization: Bearer " + Jwt)],
            Warnings = ["token " + Jwt],
        });
        using var console = new StringWriter();
        ReadinessReportRenderer.WriteConsole(report, console);

        foreach (var text in new[] { ReadinessReportRenderer.ToMarkdown(report), ReadinessReportRenderer.ToJson(report), console.ToString() })
        {
            Assert.DoesNotContain(Jwt, text, StringComparison.Ordinal);
            Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Verdict_AllRequestedPassed_IsPass_AndUnrequestedSkipsDoNotCount()
    {
        var report = SampleReport(
            new ReadinessStageResult(ReadinessStage.Config, ReadinessResult.Pass, "ok"),
            new ReadinessStageResult(ReadinessStage.Probe, ReadinessResult.Skipped, "not requested") { Requested = false, SkipReason = "not requested" });

        Assert.True(report.AllPassed);
        Assert.False(report.AnyFailed);
        Assert.False(report.AnySkippedForConsent);
        Assert.Equal("PASS", report.Verdict);
    }

    [Fact]
    public void Verdict_SkippedForConsent_IsIncomplete_NeverPass()
    {
        var report = SampleReport(
            new ReadinessStageResult(ReadinessStage.Config, ReadinessResult.Pass, "ok"),
            new ReadinessStageResult(ReadinessStage.Probe, ReadinessResult.Skipped, ReadinessStageResult.ConsentSkipReason)
            {
                SkipReason = ReadinessStageResult.ConsentSkipReason,
                SkippedForConsent = true,
            });

        Assert.False(report.AllPassed);
        Assert.True(report.AnySkippedForConsent);
        Assert.Equal("INCOMPLETE", report.Verdict);
        Assert.Contains(ReadinessStageResult.ConsentSkipReason, ReadinessReportRenderer.ToMarkdown(report), StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_NothingRequested_IsNotAPass()
    {
        var report = SampleReport(new ReadinessStageResult(ReadinessStage.Probe, ReadinessResult.Skipped, "not requested") { Requested = false });

        Assert.False(report.AllPassed);
        Assert.Equal("INCOMPLETE", report.Verdict);
    }
}
