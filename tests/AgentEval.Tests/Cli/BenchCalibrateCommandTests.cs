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
    public async Task Calibrate_NoProvider_ReturnsExitCode3()
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
    public async Task Calibrate_WithoutOut_WritesUnderTheWorkspaceFolder()
    {
        await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: null,
            evaluatorOverride: new AlwaysPassEvaluator());

        // With no --out, the report goes to the workspace folder, never a repository-internal path.
        var written = Directory.GetFiles(Path.Combine(_root, ".agenteval", "calibration"), "gdpr-calibration-*.md");
        Assert.Single(written);
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

    // ── The report names the judge that produced it ──────────────────────────

    [Fact]
    public async Task Calibrate_ReportHeader_NamesTheJudgeProviderAndModel()
    {
        var outPath = Path.Combine(_root, "report-judge.md");

        await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: outPath,
            evaluatorOverride: new AlwaysPassEvaluator(),
            evaluatorOverrideIdentity: new CalibrationJudgeIdentity("Test Provider", "test-model-7"));

        var content = await File.ReadAllTextAsync(outPath);
        Assert.Contains("Judge provider: Test Provider", content);
        Assert.Contains("Judge model: test-model-7", content);
        // In the header, ahead of the first pillar section.
        Assert.True(
            content.IndexOf("Judge model:", StringComparison.Ordinal) < content.IndexOf("## ", StringComparison.Ordinal),
            "The judge lines must be in the report header, before the first pillar section.");
    }

    [Fact]
    public async Task Calibrate_SuppliedEvaluatorWithoutIdentity_ReportsTheJudgeAsUnknown()
    {
        var outPath = Path.Combine(_root, "report-unknown-judge.md");

        await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: outPath,
            evaluatorOverride: new AlwaysPassEvaluator());

        var content = await File.ReadAllTextAsync(outPath);
        Assert.Contains($"Judge provider: unknown: an evaluator supplied by the caller ({nameof(AlwaysPassEvaluator)})", content);
        Assert.Contains("Judge model: unknown", content);
    }

    [Fact]
    public async Task Calibrate_WithoutARealJudge_Refuses_AndWritesNoReport()
    {
        // Calibration measures a judge. Through 0.42 the retired AGENTEVAL_ALLOW_STUB_JUDGE=1 let it "calibrate" a
        // placeholder that scored 75 on everything and write the figures as a calibration report. No provider is
        // configured here (the collection scrubs them all); the retired variable is set to prove it is ignored.
        Environment.SetEnvironmentVariable("AGENTEVAL_ALLOW_STUB_JUDGE", "1");
        var outPath = Path.Combine(_root, "report-no-judge.md");

        var exit = await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root,
            outPathOverride: outPath,
            evaluatorOverride: null);

        Assert.Equal(3, exit);
        Assert.False(File.Exists(outPath));
    }

    // ── --limit (B12): the one-item stage before a full paid run ───────────────────────────────────────────────

    [Fact]
    public async Task Calibrate_Limit_RequiresOut_SoItCannotOverwriteTheDaysBaseline()
    {
        var exit = await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root, outPathOverride: null, evaluatorOverride: new AlwaysPassEvaluator(), limitPerPillar: 1);

        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, exit);
    }

    [Fact]
    public async Task Calibrate_LimitZero_IsAUsageError()
    {
        var exit = await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root, outPathOverride: Path.Combine(_root, "limited.md"), evaluatorOverride: new AlwaysPassEvaluator(),
            limitPerPillar: 0);

        Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, exit);
    }

    [Fact]
    public async Task Calibrate_ALimitedRun_IsBannered_AndTheGateIsNotApplied()
    {
        var outPath = Path.Combine(_root, "limited.md");

        var exit = await BenchCalibrateCommand.RunCoreAsync(
            rootOverride: _root, outPathOverride: outPath, evaluatorOverride: new AlwaysPassEvaluator(), limitPerPillar: 1);

        // One entry per pillar: kappa is undefined, so the gate would fail by construction. Clean wiring passes.
        Assert.Equal(AgentEval.Cli.ExitCodes.Success, exit);
        Assert.StartsWith("> ⚠️ **LIMITED RUN — at most 1 entry per pillar.**", await File.ReadAllTextAsync(outPath));
    }
}
