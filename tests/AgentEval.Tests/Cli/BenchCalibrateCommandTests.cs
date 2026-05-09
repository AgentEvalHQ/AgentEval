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
public class BenchCalibrateCommandTests : IDisposable
{
    private readonly string _root;

    public BenchCalibrateCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-calibrate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
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

        // Assert — command completes without exception; exit code is 0 or 2 (never 1)
        Assert.True(exitCode == 0 || exitCode == 2,
            $"Expected exit code 0 or 2 but got {exitCode}");
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
    public async Task Calibrate_AlwaysFailStub_ReturnsExitCode2()
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

        // Assert — at least one pillar fails thresholds → exit 2
        Assert.Equal(2, exitCode);
    }
}
