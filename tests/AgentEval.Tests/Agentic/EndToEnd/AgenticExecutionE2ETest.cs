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

namespace AgentEval.Tests.Agentic.EndToEnd;

/// <summary>
/// End-to-end smoke tests for the Agentic Execution preset:
/// composite evaluation, run persistence, and Markdown reporting —
/// driven by a fixed-score stub evaluator.
/// </summary>
public class AgenticExecutionE2ETest
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

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AgenticExecution_CompositeHas6Components()
    {
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.AgenticExecution(judge);

        // The AgenticExecution preset defines 6 top-level component evaluators.
        Assert.Equal(6, benchmark.Components.Count);
    }

    [Fact]
    public async Task AgenticExecution_RunCompletes_StoreHasRunManifest()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-AgenticAgent");
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.AgenticExecution(judge);
        var runner = new AgenticBenchmarkRunner();

        var input = new EvalInput(
            Query: "Complete this agentic task",
            Response: "Task completed successfully.",
            ToolCalls: new[]
            {
                new ToolCall("search_tool", null, "{\"status\":\"success\",\"data\":\"result\"}"),
            });

        var (runId, result) = await runner.RunAsync(store, subject, benchmark, input);

        Assert.NotEmpty(runId);
        Assert.NotNull(result);

        var manifest = await store.GetRunManifestAsync(runId);
        Assert.NotNull(manifest);
    }

    [Fact]
    public async Task AgenticExecution_ReportDisclaimer_NotEmpty()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-AgenticAgent2");
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.AgenticExecution(judge);
        var runner = new AgenticBenchmarkRunner();

        var input = new EvalInput(Query: "Test", Response: "Response");
        var (runId, result) = await runner.RunAsync(store, subject, benchmark, input);

        var reporter = new AgenticBenchmarkReporter();
        var reportResult = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new AgenticReportOptions(Preset: "agentic-execution"));

        // Disclaimer must be non-empty and match the constant.
        Assert.NotEmpty(reportResult.Disclaimer);
        Assert.Equal(AgenticBenchmarkReporter.Disclaimer, reportResult.Disclaimer);
    }

    [Fact]
    public async Task AgenticExecution_SaveReport_ReturnsSummaryWithScore()
    {
        var store = new InMemoryOutputStore();
        var subject = new SubjectIdentity(SubjectKind.Agent, "E2E-AgenticAgent3");
        var judge = new StubEvaluator(100);
        var benchmark = AgenticBenchmarkFactory.AgenticExecution(judge);
        var runner = new AgenticBenchmarkRunner();

        var input = new EvalInput(Query: "Test", Response: "Response");
        var (runId, result) = await runner.RunAsync(store, subject, benchmark, input);

        var reporter = new AgenticBenchmarkReporter();
        var reportResult = await reporter.SaveReportAsync(
            store, subject, runId, result,
            new AgenticReportOptions(Preset: "agentic-execution"));

        Assert.NotNull(reportResult);
        Assert.NotNull(reportResult.Summary);
        // OverallScore must be in [0, 1].
        Assert.InRange(reportResult.Summary.OverallScore, 0.0, 1.0);
        // OverallStatus must be one of PASS / WARN / FAIL.
        Assert.Contains(reportResult.Summary.OverallStatus, new[] { "PASS", "WARN", "FAIL" });
    }
}
