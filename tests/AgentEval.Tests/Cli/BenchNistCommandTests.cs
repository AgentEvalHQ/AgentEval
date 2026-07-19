// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval bench nist</c> subcommand (<see cref="BenchNistCommand"/>) — parity with
/// <see cref="BenchMitreCommandTests"/> / <see cref="BenchOwaspCommand"/>: env-gate, workspace, preset parsing,
/// and a happy-path end-to-end that writes the NIST AI RMF report artefacts + persists a run.
/// </summary>
[Collection("EnvVarTests")]
public class BenchNistCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchNistCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-nist-test-" + Guid.NewGuid().ToString("N"));
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
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "NistBenchTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    // ── Env-gate parity ───────────────────────────────────────────────────────

    [Fact]
    public async Task BenchNist_NoEnvVars_NoStubOptIn_ReturnsExitCode3()
    {
        InitWorkspace();
        var result = await BenchNistCommand.RunAsync(
            preset: "rmf-smoke", subject: "NistGateAgent", rootOverride: _root, inputText: null,
            evaluatorOverride: null, agentOverride: null);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task BenchNist_MissingWorkspace_ReturnsExitCode1()
    {
        var noWorkspaceRoot = Path.Combine(_root, "no-workspace");
        Directory.CreateDirectory(noWorkspaceRoot);
        var result = await BenchNistCommand.RunAsync(
            preset: "rmf-smoke", subject: "NistMissingAgent", rootOverride: noWorkspaceRoot, inputText: null,
            evaluatorOverride: new PassingStubEvaluator(), agentOverride: null);
        Assert.Equal(1, result.ExitCode);
    }

    // ── Preset arg parsing ────────────────────────────────────────────────────

    [Theory]
    [InlineData("rmf-baseline", "RmfBaseline")]
    [InlineData("baseline", "RmfBaseline")]
    [InlineData("rmf-smoke", "RmfSmoke")]
    [InlineData("SMOKE", "RmfSmoke")]
    [InlineData("rmf-audit-grade", "RmfAuditGrade")]
    [InlineData("audit", "RmfAuditGrade")]
    public void ResolvePreset_MapsKnownPresets(string spec, string expectedPresetName)
        => Assert.Equal(expectedPresetName, BenchNistCommand.ResolvePreset(spec, new PassingStubEvaluator()).PresetName);

    [Fact]
    public void ResolvePreset_UnknownPreset_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BenchNistCommand.ResolvePreset("not-a-preset", new PassingStubEvaluator()));
        Assert.Contains("Unknown NIST AI RMF preset", ex.Message);
    }

    // ── Happy path end-to-end ─────────────────────────────────────────────────

    [Fact]
    public async Task BenchNist_SmokePreset_PassingAgent_RunsToCompletion_AndWritesArtefacts()
    {
        InitWorkspace();

        var (exit, reportDir) = await BenchNistCommand.RunAsync(
            preset: "rmf-smoke", subject: "NistSmokeAgent", rootOverride: _root,
            inputText: "Hello, what can you do?",
            evaluatorOverride: new PassingStubEvaluator(),
            agentOverride: new SafeRefusalAgent("NistSmokeAgent"));

        Assert.True(exit is 0 or 9 or 10 or 11, $"Expected exit 0 (PASS), 9 (FAIL), 10 (WARN) or 11 (indeterminate); got {exit}.");

        Assert.NotNull(reportDir);
        Assert.True(Directory.Exists(reportDir));
        Assert.True(File.Exists(Path.Combine(reportDir!, "report.md")), "report.md should be generated.");
        Assert.True(File.Exists(Path.Combine(reportDir!, "report.json")), "report.json should be generated.");

        var reportsRoot = Path.Combine(_root, ".agenteval", "compliance", "NIST-AI-RMF", "NistSmokeAgent");
        Assert.StartsWith(reportsRoot, reportDir);

        var subjectRunsDir = Path.Combine(_root, ".agenteval", "subjects", "agents", "NistSmokeAgent", "runs");
        Assert.True(Directory.Exists(subjectRunsDir));
        Assert.NotEmpty(Directory.GetDirectories(subjectRunsDir));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class SafeRefusalAgent(string name) : IEvaluableAgent
    {
        public string Name { get; } = name;
        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(new AgentResponse { Text = "I cannot help with that request." });
    }

    private sealed class PassingStubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
            => Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = criteria.Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" }).ToList()
            });
    }
}
