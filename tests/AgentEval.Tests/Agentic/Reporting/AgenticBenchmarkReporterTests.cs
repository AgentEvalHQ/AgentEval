// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Composition;
using AgentEval.Evals.Agentic.Reporting;
using AgentEval.Output;
using Xunit;
using AgenticBenchmarkFactory = AgentEval.Evals.Agentic.Composition.AgenticBenchmark;

namespace AgentEval.Tests.Agentic.Reporting;

/// <summary>
/// Unit tests for <see cref="AgenticBenchmarkReporter"/>:
/// report persistence, disclaimer validation, and summary non-null assertions.
/// </summary>
public class AgenticBenchmarkReporterTests
{
    // ── Stub evaluator ────────────────────────────────────────────────────────

    private sealed class StubEvaluator(int score) : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input,
            string output,
            IEnumerable<string> criteria,
            CancellationToken ct = default) =>
            Task.FromResult(new EvaluationResult
            {
                OverallScore = score,
                Summary = "stub",
                CriteriaResults = criteria
                    .Select(c => new CriterionResult { Criterion = c, Met = score >= 50, Explanation = "stub" })
                    .ToList()
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(string RunId, EvalResult Result, AgenticBenchmarkResult Report)>
        RunAndReportAsync(int stubScore, string subjectName)
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, subjectName);
        var judge = new StubEvaluator(stubScore);

        // Use a small 1-evaluator composite to keep the test deterministic and fast.
        var benchmark = AgenticBenchmarkFactory.ToolCallAccuracy(judge);
        var runner = new AgenticBenchmarkRunner();
        var input = new EvalInput(
            Query: "reporter test",
            Response: "ok",
            ToolCalls: new[]
            {
                new ToolCall("tool_a", null, "{\"status\":\"success\"}"),
            });

        var (runId, result) = await runner.RunAsync(store, subject, benchmark, input);

        var reporter = new AgenticBenchmarkReporter();
        var report = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new AgenticReportOptions(Preset: "tool-call-accuracy"));

        return (runId, result, report);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveReportAsync_PersistsBenchmarkResult()
    {
        var (_, _, report) = await RunAndReportAsync(stubScore: 100, "ReporterTestAgent1");

        Assert.NotNull(report);
        Assert.NotNull(report.Summary);
        Assert.NotEmpty(report.SourceRunId);
        Assert.NotEmpty(report.SourceManifestHash);
    }

    [Fact]
    public async Task SaveReportAsync_DisclaimerNotEmpty()
    {
        var (_, _, report) = await RunAndReportAsync(stubScore: 100, "ReporterTestAgent2");

        // Disclaimer must be exactly the constant defined on the reporter.
        Assert.NotEmpty(report.Disclaimer);
        Assert.Equal(AgenticBenchmarkReporter.Disclaimer, report.Disclaimer);
    }

    [Fact]
    public async Task SaveReportAsync_AttestationHasVersion()
    {
        var (_, _, report) = await RunAndReportAsync(stubScore: 100, "ReporterTestAgent3");

        Assert.NotNull(report.Attestation);
        Assert.NotEmpty(report.Attestation.AgentEvalVersion);
    }

    [Fact]
    public async Task SaveReportAsync_SummaryOverallStatusIsValid()
    {
        var (_, _, report) = await RunAndReportAsync(stubScore: 100, "ReporterTestAgent4");

        Assert.Contains(report.Summary.OverallStatus, new[] { "PASS", "WARN", "FAIL" });
    }
}
