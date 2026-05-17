// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
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

    [Fact]
    public async Task BenchAgentic_NoEnvVars_NoStubOptIn_ReturnsExitCode2()
    {
        InitWorkspace();
        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",                                // pure-code preset; no real LLM calls
            subject: "AgenticGateTestAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,                            // exercise the env-gate path
            budgetTier: null);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task BenchAgentic_PartialAzureConfig_ReturnsExitCode2()
    {
        InitWorkspace();
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        // Missing key + deployment → partial config → exit 2

        var exit = await BenchAgenticCommand.RunAsync(
            preset: "telemetry",
            subject: "AgenticPartialAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,
            budgetTier: null);
        Assert.Equal(2, exit);
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
            inputText: null,
            evaluatorOverride: new PassingStubEvaluator(),
            budgetTier: null);

        Assert.True(exit == 0 || exit == 2,
            $"Expected exit 0 (PASS) or 2 (FAIL verdict); got {exit}. Exit code 1 would indicate a workspace/preset/config error which the smoke-test setup is meant to rule out.");

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
            inputText: null,
            evaluatorOverride: new PassingStubEvaluator(),
            budgetTier: null);

        Assert.Equal(1, exit);
    }
}
