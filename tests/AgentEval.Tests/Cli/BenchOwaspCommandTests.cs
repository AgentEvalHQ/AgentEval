// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval bench owasp</c> subcommand
/// (<see cref="BenchOwaspCommand"/>) introduced in Phase 5 of v0.10.0-beta.
/// Mirrors the env-gate + workspace + happy-path coverage applied to
/// <see cref="BenchAgenticCommand"/> and <see cref="BenchEuAiActCommand"/>.
/// </summary>
[Collection("EnvVarTests")]
public class BenchOwaspCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchOwaspCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-owasp-test-" + Guid.NewGuid().ToString("N"));
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
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",      _envSnapshot.Endpoint);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",       _envSnapshot.Key);
        Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",    _envSnapshot.Deployment);
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
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "OwaspBenchTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    // ── Env-gate parity with the other bench commands ─────────────────────────

    [Fact]
    public async Task BenchOwasp_NoEnvVars_NoStubOptIn_ReturnsExitCode2()
    {
        InitWorkspace();
        var exit = await BenchOwaspCommand.RunAsync(
            preset: "smoke",
            subject: "OwaspGateTestAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,
            agentOverride: null);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task BenchOwasp_PartialAzureConfig_ReturnsExitCode2()
    {
        InitWorkspace();
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        // Missing key + deployment → partial config → exit 2

        var exit = await BenchOwaspCommand.RunAsync(
            preset: "smoke",
            subject: "OwaspPartialAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,
            agentOverride: null);
        Assert.Equal(2, exit);
    }

    [Fact]
    public async Task BenchOwasp_MissingWorkspace_ReturnsExitCode1()
    {
        var noWorkspaceRoot = Path.Combine(_root, "no-workspace");
        Directory.CreateDirectory(noWorkspaceRoot);

        var exit = await BenchOwaspCommand.RunAsync(
            preset: "smoke",
            subject: "OwaspMissingAgent",
            rootOverride: noWorkspaceRoot,
            inputText: null,
            evaluatorOverride: new PassingStubEvaluator(),
            agentOverride: null);

        Assert.Equal(1, exit);
    }

    // ── Preset arg parsing ────────────────────────────────────────────────────

    [Theory]
    [InlineData("top10",       "Top10")]
    [InlineData("Smoke",       "Smoke")]
    [InlineData("SMOKE",       "Smoke")]
    [InlineData("audit",       "AuditGrade")]
    [InlineData("auditgrade",  "AuditGrade")]
    [InlineData("top10-rag",   "Top10ForRag")]
    [InlineData("top10forrag", "Top10ForRag")]
    public void ResolvePreset_MapsKnownPresets(string spec, string expectedPresetName)
    {
        var run = BenchOwaspCommand.ResolvePreset(spec, new PassingStubEvaluator());
        Assert.Equal(expectedPresetName, run.PresetName);
    }

    [Fact]
    public void ResolvePreset_UnknownPreset_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BenchOwaspCommand.ResolvePreset("not-a-preset", new PassingStubEvaluator()));
        Assert.Contains("Unknown OWASP preset", ex.Message);
    }

    // ── Happy-path end-to-end: pipeline runs and writes artefacts ─────────────

    [Fact]
    public async Task BenchOwasp_SmokePreset_PassingAgent_RunsToCompletion_AndWritesArtefacts()
    {
        InitWorkspace();

        var exit = await BenchOwaspCommand.RunAsync(
            preset: "smoke",
            subject: "OwaspSmokeAgent",
            rootOverride: _root,
            inputText: "Hello, what can you do?",
            evaluatorOverride: new PassingStubEvaluator(),
            agentOverride: new SafeRefusalAgent("OwaspSmokeAgent"));

        // 0 (PASS) or 2 (WARN/FAIL) both indicate the pipeline executed cleanly;
        // 1 would indicate a workspace / preset / config error.
        Assert.True(exit == 0 || exit == 2,
            $"Expected exit 0 (PASS) or 2 (WARN/FAIL verdict); got {exit}.");

        // The OWASP report directory should exist with at least one timestamped folder.
        var reportsRoot = Path.Combine(_root, ".agenteval", "compliance", "OWASP-LLM-Top10", "OwaspSmokeAgent");
        Assert.True(Directory.Exists(reportsRoot),
            $"Reports root {reportsRoot} should exist after a completed bench run.");
        var tsDirs = Directory.GetDirectories(reportsRoot);
        Assert.NotEmpty(tsDirs);
        var latestTs = tsDirs.OrderByDescending(d => d).First();
        Assert.True(File.Exists(Path.Combine(latestTs, "report.md")),
            "report.md should be generated alongside the run.");
        Assert.True(File.Exists(Path.Combine(latestTs, "report.json")),
            "report.json should be generated alongside the run.");

        // The unified output-store should have a run manifest written by the
        // command's StartRunAsync/CompleteRunAsync flow. FileSystemLayout writes
        // runs under .agenteval/subjects/<kind>/<name>/runs/<runId>.
        var subjectRunsDir = Path.Combine(_root, ".agenteval", "subjects", "agents", "OwaspSmokeAgent", "runs");
        Assert.True(Directory.Exists(subjectRunsDir),
            $"Run directory {subjectRunsDir} should exist after persisting the OWASP run.");
        Assert.NotEmpty(Directory.GetDirectories(subjectRunsDir));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Always-safe-refusal agent for happy-path testing. Mirrors the CLI's
    /// internal stub but lives here so the test does not depend on that
    /// internal symbol.
    /// </summary>
    private sealed class SafeRefusalAgent : IEvaluableAgent
    {
        public string Name { get; }
        public SafeRefusalAgent(string name) { Name = name; }
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse
            {
                Text = "I cannot help with that request."
            });
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
                CriteriaResults = list
                    .Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
        }
    }
}
