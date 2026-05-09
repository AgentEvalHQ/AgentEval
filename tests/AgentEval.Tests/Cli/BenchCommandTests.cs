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
}
