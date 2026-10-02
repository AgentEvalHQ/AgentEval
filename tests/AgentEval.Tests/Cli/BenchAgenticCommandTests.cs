// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Smoke tests for the <c>agenteval bench agentic</c> subcommand
/// (<see cref="BenchAgenticCommand"/>). Phase-4 Task 4.6 — closes the
/// coverage gap.
/// </summary>
[Collection("EnvVarTests")]
public class BenchAgenticCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchAgenticCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-agentic-bench-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _envSnapshot = (
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE"));
        ScrubEnv();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",     _envSnapshot.Endpoint);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",      _envSnapshot.Key);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",   _envSnapshot.Deployment);
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", _envSnapshot.Stub);
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void ScrubEnv()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", null);
    }

    private void InitWorkspace()
    {
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "AgenticBenchTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private sealed class CapturingEvaluator : IEvaluator
    {
        public string? LastInput { get; private set; }
        public string? LastOutput { get; private set; }

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            LastInput = input;
            LastOutput = output;
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "captured",
                CriteriaResults = criteria.Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "captured" }).ToList(),
            });
        }
    }

    private sealed class PassingStubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = list.Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" }).ToList()
            });
        }
    }

    private const string SuppliedQuestion = "Find ACME Corp's latest quarterly results.";
    private const string SuppliedAnswer = "I searched the filings: revenue was $4.2B, up 12% year over year.";

    private string[] WorkspaceFiles() =>
        Directory.GetFileSystemEntries(Path.Combine(_root, ".agenteval"), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public async Task BenchAgentic_NoAnswer_Refuses_AndStoresNothing()
    {
        // Through 0.43 a run with no --response graded a built-in answer and stored it as the subject's evidence.
        InitWorkspace();
        var before = WorkspaceFiles();

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",
            subject: "AgenticNoAnswerAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            responseText: null,
            evaluatorOverride: new PassingStubEvaluator(),
            budgetTier: null);

        Assert.Equal(ExitCodes.UsageError, exit);
        Assert.Equal(before, WorkspaceFiles());
    }

    [Fact]
    public async Task BenchAgentic_SuppliedAnswerWithoutItsQuestion_Refuses()
    {
        InitWorkspace();

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",
            subject: "AgenticNoQuestionAgent",
            rootOverride: _root,
            inputText: null,
            responseText: SuppliedAnswer,
            evaluatorOverride: new PassingStubEvaluator(),
            budgetTier: null);

        Assert.Equal(ExitCodes.UsageError, exit);
    }

    [Fact]
    public async Task BenchAgentic_Mock_GradesTheCannedAnswer_ExitsIndeterminate_AndStoresNothing()
    {
        InitWorkspace();
        var capturing = new CapturingEvaluator();
        var before = WorkspaceFiles();

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "agentic-execution",
            subject: "AgenticMockAgent",
            rootOverride: _root,
            inputText: null,
            responseText: null,
            evaluatorOverride: capturing,
            budgetTier: null,
            mock: true);

        Assert.Equal(ExitCodes.GateIndeterminate, exit);
        Assert.Contains("ACME Corp", capturing.LastOutput ?? "");
        Assert.Equal(before, WorkspaceFiles());
    }

    [Fact]
    public async Task BenchAgentic_TraceOnly_GradesTheAnswerTheTraceRecorded()
    {
        // A captured run is a real target: its own question and final answer are graded, nothing is made up.
        // Through 0.43 a --trace without --response graded a built-in answer next to the real trace.
        InitWorkspace();
        var trace = new AgentEval.Tracing.AgentTrace { TraceName = "captured-run" };
        trace.AddEntry(new AgentEval.Tracing.TraceEntry
        {
            Type = AgentEval.Tracing.TraceEntryType.Request,
            Scope = AgentEval.Tracing.TraceEntryScope.AgentInvocation,
            Prompt = "QUESTION-FROM-THE-TRACE",
        });
        trace.AddEntry(new AgentEval.Tracing.TraceEntry
        {
            Type = AgentEval.Tracing.TraceEntryType.Response,
            Scope = AgentEval.Tracing.TraceEntryScope.ChatTurn,
            Text = "an inner chat turn, not the agent's answer",
        });
        trace.AddEntry(new AgentEval.Tracing.TraceEntry
        {
            Type = AgentEval.Tracing.TraceEntryType.Response,
            Scope = AgentEval.Tracing.TraceEntryScope.AgentInvocation,
            Text = "ANSWER-FROM-THE-TRACE",
        });
        var traceFile = Path.Combine(_root, "run.trace.json");
        await AgentEval.Tracing.TraceSerializer.SaveToFileAsync(trace, traceFile);
        var capturing = new CapturingEvaluator();

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "agentic-execution",
            subject: "AgenticTraceAgent",
            rootOverride: _root,
            inputText: null,
            responseText: null,
            evaluatorOverride: capturing,
            budgetTier: null,
            traceFile: traceFile);

        Assert.True(exit is 0 or 9 or 10 or 11, $"Expected a gate verdict; got {exit}.");
        Assert.Equal("ANSWER-FROM-THE-TRACE", capturing.LastOutput);
        Assert.Contains("QUESTION-FROM-THE-TRACE", capturing.LastInput ?? "");
    }

    [Fact]
    public async Task BenchAgentic_MockWithARealTarget_IsRefusedByTheCommandItself()
    {
        InitWorkspace();

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",
            subject: "AgenticMockPlusAnswerAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            responseText: SuppliedAnswer,
            evaluatorOverride: new PassingStubEvaluator(),
            budgetTier: null,
            mock: true);

        Assert.Equal(ExitCodes.UsageError, exit);
    }

    [Fact]
    public async Task BenchAgentic_NoEnvVars_NoStubOptIn_ReturnsExitCode3()
    {
        InitWorkspace();
        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",                                // pure-code preset; no real LLM calls
            subject: "AgenticGateTestAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            responseText: SuppliedAnswer,
            evaluatorOverride: null,                            // exercise the env-gate path
            budgetTier: null);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task BenchAgentic_PartialAzureConfig_ReturnsExitCode3()
    {
        InitWorkspace();
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        // Missing key + deployment → partial config → exit 2

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",
            subject: "AgenticPartialAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            responseText: SuppliedAnswer,
            evaluatorOverride: null,
            budgetTier: null);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task BenchAgentic_TelemetryPreset_PassingStubOverride_RunsToCompletion()
    {
        // The `telemetry` preset is pure-code (no LLM judge calls); the
        // evaluatorOverride bypasses the env gate. The bench harness runs to
        // completion and writes report files; we don't pin a specific verdict
        // because telemetry evaluators legitimately return 0% when no
        // telemetry metadata is supplied (this synthetic test provides none),
        // which exits 2 (FAIL verdict). 0 (PASS) or 2 (FAIL) both indicate
        // the pipeline executed cleanly; 1 would indicate a workspace /
        // preset / config error.
        InitWorkspace();

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",
            subject: "AgenticTelemetryAgent",
            rootOverride: _root,
            inputText: SuppliedQuestion,
            responseText: SuppliedAnswer,
            evaluatorOverride: new PassingStubEvaluator(),
            budgetTier: null);

        Assert.True(exit is 0 or 9 or 10 or 11,
            $"Expected exit 0 (PASS), 9 (FAIL), 10 (WARN) or 11 (indeterminate); got {exit}. Exit code 1 would indicate a workspace/preset/config error which the smoke-test setup is meant to rule out.");

        // Report files MUST be present regardless of pass/fail verdict.
        var reportsRoot = Path.Combine(_root, ".agenteval", "benchmarks", "agentic", "AgenticTelemetryAgent");
        Assert.True(Directory.Exists(reportsRoot), $"Reports root {reportsRoot} should exist after a completed bench run.");
        var tsDirs = Directory.GetDirectories(reportsRoot);
        Assert.NotEmpty(tsDirs);
        var latestTs = tsDirs.OrderByDescending(d => d).First();
        Assert.True(File.Exists(Path.Combine(latestTs, "report.md")), "report.md should be generated.");
    }

    [Fact]
    public async Task BenchAgentic_MissingWorkspace_ReturnsExitCode1()
    {
        var noWorkspaceRoot = Path.Combine(_root, "no-workspace");
        Directory.CreateDirectory(noWorkspaceRoot);

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",
            subject: "MissingWorkspaceAgent",
            rootOverride: noWorkspaceRoot,
            inputText: SuppliedQuestion,
            responseText: SuppliedAnswer,
            evaluatorOverride: new PassingStubEvaluator(),
            budgetTier: null);

        Assert.Equal(1, exit);
    }
}
