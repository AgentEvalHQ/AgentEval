// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for the <c>agenteval bench gdpr calibrate</c> subcommand
/// (<see cref="BenchCalibrateCommand"/>).
/// </summary>
[Collection("EnvVarTests")]
public class BenchCalibrateCommandTests : IDisposable
{
    private readonly string _root;
    private readonly (string? Endpoint, string? Key, string? Deployment, string? Stub) _envSnapshot;

    public BenchCalibrateCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-calibrate-test-" + Guid.NewGuid().ToString("N"));
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

    // ── Stub evaluators ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns score=100 for all evaluations so AtomicLlmEval produces "pass" labels.
    /// The calibration gate will PASS when all expected verdicts in the golden datasets
    /// align with this (pass entries win, but mixed golden entries may cause disagreement
    /// — we test that the command completes and exits with a numeric code).
    /// </summary>
    private sealed class AlwaysPassEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 100,
                Summary = "stub-pass",
                CriteriaResults = list.Select(c =>
                    new CriterionResult { Criterion = c, Met = true, Explanation = "stub" })
                    .ToList()
            });
        }
    }

    /// <summary>Returns score=0 for all evaluations — judge always disagrees with "pass" entries.</summary>
    private sealed class AlwaysFailEvaluator : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(
            string input, string output, IEnumerable<string> criteria,
            CancellationToken cancellationToken = default)
        {
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = 0,
                Summary = "stub-fail",
                CriteriaResults = list.Select(c =>
                    new CriterionResult { Criterion = c, Met = false, Explanation = "stub-fail" })
                    .ToList()
            });
        }
    }

    // ── Env-gate trio (Phase-4 gate-review follow-up) ─────────────────────────

    [Fact]
    public async Task Calibrate_NoEnvVars_NoStubOptIn_ReturnsExitCode3()
    {
        // env already scrubbed by ctor.
        var exit = await BenchCalibrateCommand.RunAsync(_root, outPathOverride: null);
        Assert.Equal(3, exit);
    }

    [Fact]
    public async Task Calibrate_PartialAzureConfig_ReturnsExitCode3()
    {
        Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", "https://example.openai.azure.com/");
        // Missing key + deployment — JudgeFactory should refuse to construct an Azure client.

        var exit = await BenchCalibrateCommand.RunAsync(_root, outPathOverride: null);
        Assert.Equal(3, exit);
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Calibrate_CompletesAndReturnsExitCode()
    {
        // Arrange
        var outPath = Path.Combine(_root, "calibration-report.md");

        // Act — use the always-pass stub with the real golden datasets loaded from this assembly
        var exitCode = await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: outPath,
            evaluatorOverride: new AlwaysPassEvaluator());

        // Assert — command completes without exception; exit code is 0 or 9 (GateFailed) (never 1)
        Assert.True(exitCode is 0 or 9,
            $"Expected exit code 0 or 9 (GateFailed) but got {exitCode}");
    }

    [Fact]
    public async Task Calibrate_WritesMarkdownReportWithPerPillarHeadings()
    {
        // Arrange
        var outPath = Path.Combine(_root, "report.md");

        // Act
        await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: outPath,
            evaluatorOverride: new AlwaysPassEvaluator());

        // Assert — Markdown report was written and contains per-pillar headings
        Assert.True(File.Exists(outPath), $"Markdown report not found at {outPath}");
        var content = await File.ReadAllTextAsync(outPath);
        Assert.Contains("# GDPR Calibration Report", content);
        // At least one pillar heading should be present
        Assert.Contains("## ", content);
    }

    [Fact]
    public async Task Calibrate_AlwaysFailStub_ReturnsExitCode9()
    {
        // Arrange — always-fail judge will produce "fail" for every entry;
        // the golden datasets have many "pass"-expected entries so accuracy will be
        // well below the 0.85 threshold, triggering exit code 2.
        var outPath = Path.Combine(_root, "report-fail.md");

        // Act
        var exitCode = await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: outPath,
            evaluatorOverride: new AlwaysFailEvaluator());

        // Assert — at least one pillar fails thresholds → exit 9 (GateFailed)
        Assert.Equal(9, exitCode);
    }
}
