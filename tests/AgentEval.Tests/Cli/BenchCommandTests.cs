// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval bench gdpr</c> subcommand (<see cref="BenchCommand"/>).
/// </summary>
public class BenchCommandTests : IDisposable
{
    private readonly string _root;

    public BenchCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-bench-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void InitWorkspace()
    {
        var dir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(dir);
        var solutionDoc = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "BenchTestSolution" };
        File.WriteAllText(
            Path.Combine(dir, "solution.json"),
            JsonSerializer.Serialize(solutionDoc,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private sealed class PassingStubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var criteriaList = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = criteriaList
                    .Select(c => new CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
        }
    }

    private sealed class FailingStubEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var criteriaList = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 10,
                Summary = "stub-fail",
                CriteriaResults = criteriaList
                    .Select(c => new CriterionResult { Criterion = c, Met = false, Explanation = "stub-fail" })
                    .ToList()
            });
        }
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BenchGdpr_MissingWorkspace_ReturnsExitCode1()
    {
        // Arrange — no .agenteval/ directory initialised
        var noWorkspaceRoot = Path.Combine(_root, "no-workspace");
        Directory.CreateDirectory(noWorkspaceRoot);

        // Act
        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "TestAgent",
            rootOverride: noWorkspaceRoot,
            inputText: null,
            evaluatorOverride: new PassingStubEvaluator());

        // Assert
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task BenchGdpr_SmokePreset_PassingStub_ReturnsExitCode0()
    {
        // Arrange
        InitWorkspace();

        // Act
        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "SmokeTestAgent",
            rootOverride: _root,
            inputText: "What personal data do you store?",
            evaluatorOverride: new PassingStubEvaluator());

        // Assert — passing benchmark exits 0
        Assert.Equal(0, exitCode);

        // Verify that the compliance directory was created with artifacts
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        var complianceRoot = Path.Combine(agentEvalDir, "compliance", "GDPR", "SmokeTestAgent");
        Assert.True(Directory.Exists(complianceRoot),
            $"Expected compliance directory at {complianceRoot}");

        // At least one timestamp directory should exist
        var tsDirs = Directory.GetDirectories(complianceRoot);
        Assert.NotEmpty(tsDirs);
    }

    [Fact]
    public async Task BenchGdpr_SmokePreset_FailingStub_ReturnsNonZeroExitCode()
    {
        // Arrange
        InitWorkspace();

        // Act
        var exitCode = await BenchCommand.RunGdprAsync(
            preset: "smoke",
            subject: "FailAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: new FailingStubEvaluator());

        // Assert — failing benchmark exits non-zero (2)
        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task BenchGdpr_StandardPreset_PassingStub_WritesReportFiles()
    {
        // Arrange
        InitWorkspace();

        // Act
        await BenchCommand.RunGdprAsync(
            preset: "standard",
            subject: "StandardTestAgent",
            rootOverride: _root,
            inputText: null,
            evaluatorOverride: new PassingStubEvaluator());

        // Assert — evidence directory contains report.md and report.pdf
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        var complianceRoot = Path.Combine(agentEvalDir, "compliance", "GDPR", "StandardTestAgent");
        var tsDir = Directory.GetDirectories(complianceRoot).OrderByDescending(d => d).First();

        Assert.True(File.Exists(Path.Combine(tsDir, "report.md")), "report.md should be generated");
        Assert.True(File.Exists(Path.Combine(tsDir, "report.pdf")), "report.pdf should be generated");
        Assert.True(new FileInfo(Path.Combine(tsDir, "report.pdf")).Length > 0,
            "report.pdf should be non-empty");
    }

    // ── AGENTEVAL_ALLOW_STUB_JUDGE gate (batch-5 surface) ────────────────────
    //
    // These tests scrub the AZURE_OPENAI_* env vars to simulate a CI run that
    // forgot to wire the secrets. With evaluatorOverride=null the resolver
    // falls into the gate path. Marked [Collection("EnvVarTests")] so they
    // don't race with parallel tests that also touch env vars.

    [Fact]
    public async Task RunGdprAsync_NoEnvVars_NoStubOptIn_ReturnsExitCode2()
    {
        InitWorkspace();

        // Snapshot + scrub env vars so the resolver hits the no-config branch.
        var envAzureEndpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var envAzureKey        = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var envAzureDeployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        var envAllowStub       = Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
            Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", null);

            var exit = await BenchCommand.RunGdprAsync(
                preset: "smoke",
                subject: "EnvGateTest",
                rootOverride: _root,
                inputText: null,
                evaluatorOverride: null);

            Assert.Equal(2, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",     envAzureEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",      envAzureKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",   envAzureDeployment);
            Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", envAllowStub);
        }
    }

    [Fact]
    public async Task RunGdprAsync_PartialAzureConfig_ReturnsExitCode2()
    {
        // Endpoint set but API key + deployment missing — must NOT fall through
        // to the stub silently. The resolver flags partial config explicitly.
        InitWorkspace();
        var snap = (
            Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT"),
            Environment.GetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE"));
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com");
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",  null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
            Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", null);

            var exit = await BenchCommand.RunGdprAsync(
                preset: "smoke",
                subject: "PartialConfigTest",
                rootOverride: _root,
                inputText: null,
                evaluatorOverride: null);

            Assert.Equal(2, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT",     snap.Item1);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY",      snap.Item2);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT",   snap.Item3);
            Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", snap.Item4);
        }
    }
}
