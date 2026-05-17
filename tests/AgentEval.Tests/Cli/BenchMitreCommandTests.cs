// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval bench mitre</c> subcommand
/// (<see cref="BenchMitreCommand"/>) introduced in Phase 6 of v0.10.0-beta.
/// Mirrors the env-gate + workspace + happy-path coverage applied to
/// <see cref="BenchOwaspCommand"/>.
/// </summary>
[Collection("EnvVarTests")]
public class BenchMitreCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchMitreCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-mitre-test-" + Guid.NewGuid().ToString("N"));
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
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "MitreBenchTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    // ── Env-gate parity with the other bench commands ─────────────────────────

    [Fact]
    public async Task BenchMitre_NoEnvVars_NoStubOptIn_ReturnsExitCode2()
    {
        InitWorkspace();
        var result = await BenchMitreCommand.RunAsync(
            preset: "atlas-smoke",
            subject: "MitreGateTestAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,
            agentOverride: null);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task BenchMitre_PartialAzureConfig_ReturnsExitCode2()
    {
        InitWorkspace();
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        // Missing key + deployment → partial config → exit 2.

        var result = await BenchMitreCommand.RunAsync(
            preset: "atlas-smoke",
            subject: "MitrePartialAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: null,
            agentOverride: null);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task BenchMitre_MissingWorkspace_ReturnsExitCode1()
    {
        var noWorkspaceRoot = Path.Combine(_root, "no-workspace");
        Directory.CreateDirectory(noWorkspaceRoot);

        var result = await BenchMitreCommand.RunAsync(
            preset: "atlas-smoke",
            subject: "MitreMissingAgent",
            rootOverride: noWorkspaceRoot,
            inputText: null,
            evaluatorOverride: new PassingStubEvaluator(),
            agentOverride: null);

        Assert.Equal(1, result.ExitCode);
    }

    // ── Preset arg parsing ────────────────────────────────────────────────────

    [Theory]
    [InlineData("atlas-baseline",  "AtlasBaseline")]
    [InlineData("AtlasBaseline",   "AtlasBaseline")]
    [InlineData("baseline",        "AtlasBaseline")]
    [InlineData("atlas-smoke",     "AtlasSmoke")]
    [InlineData("Smoke",           "AtlasSmoke")]
    [InlineData("SMOKE",           "AtlasSmoke")]
    [InlineData("atlas-audit-grade", "AtlasAuditGrade")]
    [InlineData("atlas-audit",     "AtlasAuditGrade")]
    [InlineData("audit",           "AtlasAuditGrade")]
    [InlineData("auditgrade",      "AtlasAuditGrade")]
    public void ResolvePreset_MapsKnownPresets(string spec, string expectedPresetName)
    {
        var run = BenchMitreCommand.ResolvePreset(spec, new PassingStubEvaluator());
        Assert.Equal(expectedPresetName, run.PresetName);
    }

    [Fact]
    public void ResolvePreset_UnknownPreset_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BenchMitreCommand.ResolvePreset("not-a-preset", new PassingStubEvaluator()));
        Assert.Contains("Unknown MITRE ATLAS preset", ex.Message);
    }

    // ── Happy-path end-to-end: pipeline runs and writes artefacts ─────────────

    [Fact]
    public async Task BenchMitre_SmokePreset_PassingAgent_RunsToCompletion_AndWritesArtefacts()
    {
        InitWorkspace();

        var (exit, reportDir) = await BenchMitreCommand.RunAsync(
            preset: "atlas-smoke",
            subject: "MitreSmokeAgent",
            rootOverride: _root,
            inputText: "Hello, what can you do?",
            evaluatorOverride: new PassingStubEvaluator(),
            agentOverride: new SafeRefusalAgent("MitreSmokeAgent"));

        // 0 (PASS) or 2 (WARN/FAIL) both indicate the pipeline executed cleanly;
        // 1 would indicate a workspace / preset / config error.
        Assert.True(exit == 0 || exit == 2,
            $"Expected exit 0 (PASS) or 2 (WARN/FAIL verdict); got {exit}.");

        // The command returns the absolute path of the timestamped report
        // directory; using it directly avoids the second-precision-timestamp
        // race that "OrderByDescending(d => d).First()" would otherwise have on
        // directory-name strings of shape `yyyy-MM-dd_HH-mm-ss`.
        Assert.NotNull(reportDir);
        Assert.True(Directory.Exists(reportDir),
            $"Report directory {reportDir} should exist after a completed bench run.");
        Assert.True(File.Exists(Path.Combine(reportDir!, "report.md")),
            "report.md should be generated alongside the run.");
        Assert.True(File.Exists(Path.Combine(reportDir!, "report.json")),
            "report.json should be generated alongside the run.");

        // Sanity-check that the directory lives under the expected compliance
        // tree (the path-shape part of the original assertion).
        var reportsRoot = Path.Combine(_root, ".agenteval", "compliance", "MITRE-ATLAS", "MitreSmokeAgent");
        Assert.StartsWith(reportsRoot, reportDir);

        // The unified output-store should have a run manifest written by the
        // command's StartRunAsync/CompleteRunAsync flow.
        var subjectRunsDir = Path.Combine(_root, ".agenteval", "subjects", "agents", "MitreSmokeAgent", "runs");
        Assert.True(Directory.Exists(subjectRunsDir),
            $"Run directory {subjectRunsDir} should exist after persisting the MITRE run.");
        Assert.NotEmpty(Directory.GetDirectories(subjectRunsDir));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
