// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// Tests for <see cref="BenchPerfCommand"/> (Phase 8 / v0.10.0-beta).
/// </summary>
public class BenchPerfCommandTests
{
    [Fact]
    public async Task RunAsync_LatencyPreset_WritesEvalResultJsonToWorkspace()
    {
        // Arrange: temp workspace with .agenteval/ skeleton.
        var workspace = CreateTempWorkspace();
        try
        {
            var stubAgent = new StubAgent("perf-test-subject");

            // Act
            var exitCode = await BenchPerfCommand.RunAsync(
                preset: "latency",
                subject: "perf-test-subject",
                prompt: "hello",
                rootOverride: workspace,
                agentOverride: stubAgent);

            // Assert: exit code is 0 (pass) or 2 (warn/fail), not 1 (CLI error).
            Assert.True(exitCode is 0 or 9 or 10 or 11,
                $"Expected exit code 0 (pass), 9 (FAIL), 10 (WARN) or 11 (indeterminate), got {exitCode}.");

            var aef = Path.Combine(workspace, ".agenteval", "aef");
            var run = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(aef, "seal.json", SearchOption.AllDirectories)))!;
            Assert.Equal(AgentEval.Results.Integrity.AefOutcome.Intact, AgentEval.Results.Integrity.AefRunVerifier.Verify(run).Outcome);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".agenteval", "subjects")));
            Assert.Single(Directory.GetFiles(Path.Combine(workspace, ".agenteval", "reports"), "report.json", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    [Fact]
    public async Task RunAsync_WritesASealedAefRun_AStandInMocked()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var exitCode = await BenchPerfCommand.RunAsync(preset: "latency", subject: "perf-aef", prompt: "hello",
                rootOverride: workspace, agentOverride: new StubAgent("perf-aef"), subjectVersion: "0.9.0");

            Assert.True(exitCode is 0 or 9 or 10 or 11, $"got {exitCode}");
            var run = Assert.Single(Directory.GetFiles(Path.Combine(workspace, ".agenteval", "aef"), "run.json", SearchOption.AllDirectories));
            var verification = AgentEval.Results.Integrity.AefRunVerifier.Verify(Path.GetDirectoryName(run)!);
            Assert.Equal(AgentEval.Results.Integrity.AefOutcome.Intact, verification.Outcome);
            Assert.Empty(verification.Problems);
            var header = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(run))!;
            Assert.Equal("mocked", (string?)header["execution"]!["targetMode"]);
            Assert.Equal("bench:perf/latency", (string?)header["suite"]!["ref"]);
            Assert.Equal("definition", (string?)header["ext"]!["agenteval.suiteIdentity"]!["scope"]);
            var definition = new AgentEval.Results.Suites.SuiteDefinitionReader().Read(File.ReadAllBytes(Path.Combine(
                Path.GetDirectoryName(run)!, "ext", AgentEval.Results.Suites.SuiteDefinitionManifest.ExtensionFile)));
            Assert.Equal(new[] { "perf_benchmark", "perf_cost", "perf_latency", "perf_throughput" }, definition.CaseIds);
            Assert.Equal(definition.Digest, (string?)header["suite"]!["digest"]);
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    [Fact]
    public async Task RunAsync_SubjectRequiringSanitization_ReportUsesSafeStandalonePath()
    {
        // BUG-17: raw subject characters must not orphan a report or fail its write. Native mode
        // keeps the original subject identity in evidence and uses a sanitized standalone report path.
        var workspace = CreateTempWorkspace();
        try
        {
            const string messySubject = "perf:subject*needs?sanitize";
            var stubAgent = new StubAgent(messySubject);

            var exitCode = await BenchPerfCommand.RunAsync(
                preset: "latency",
                subject: messySubject,
                prompt: "hello",
                rootOverride: workspace,
                agentOverride: stubAgent);

            Assert.True(exitCode is 0 or 9 or 10 or 11,
                $"Expected exit 0/9/10/11, got {exitCode} — a divergent report path made the run fail.");

            var report = Assert.Single(Directory.GetFiles(Path.Combine(workspace, ".agenteval", "reports"), "report.json", SearchOption.AllDirectories));
            var subjectDirectory = Directory.GetParent(Path.GetDirectoryName(report)!)!.Name;
            Assert.Equal(AgentEval.Output.FileSystemLayout.Sanitize(messySubject), subjectDirectory);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".agenteval", "subjects")));
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    [Fact]
    public async Task RunAsync_NoTarget_Refuses_AndStoresNothing()
    {
        // Through 0.42 a run with no target measured a built-in echo agent and stored it as the subject's result.
        var workspace = CreateTempWorkspace();
        try
        {
            var before = Directory.GetFileSystemEntries(workspace, "*", SearchOption.AllDirectories).Length;

            var exitCode = await BenchPerfCommand.RunAsync(
                preset: "latency",
                subject: "perf-no-target",
                prompt: "hello",
                rootOverride: workspace,
                agentOverride: null);

            Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, exitCode);
            Assert.Equal(before, Directory.GetFileSystemEntries(workspace, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    [Fact]
    public async Task RunAsync_Mock_ExitsIndeterminate_AndStoresNothing()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var before = Directory.GetFileSystemEntries(workspace, "*", SearchOption.AllDirectories).Length;

            var exitCode = await BenchPerfCommand.RunAsync(
                preset: "latency",
                subject: "perf-mock",
                prompt: "hello",
                rootOverride: workspace,
                agentOverride: null,
                mock: true);

            Assert.Equal(AgentEval.Cli.ExitCodes.GateIndeterminate, exitCode);
            Assert.Equal(before, Directory.GetFileSystemEntries(workspace, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    [Fact]
    public async Task RunAsync_UnknownPreset_ReturnsError()
    {
        var workspace = CreateTempWorkspace();
        try
        {
            var exitCode = await BenchPerfCommand.RunAsync(
                preset: "nonexistent-preset",
                subject: "test",
                prompt: "hello",
                rootOverride: workspace,
                agentOverride: new StubAgent("test"));

            // An unknown preset is a rejected argument: usage error (2), not a test failure (1).
            Assert.Equal(AgentEval.Cli.ExitCodes.UsageError, exitCode);
        }
        finally
        {
            Cleanup(workspace);
        }
    }

    [Fact]
    public async Task RunAsync_NoAgentEvalDir_ReturnsError()
    {
        // Use a real folder that has no .agenteval/ inside.
        var emptyDir = Path.Combine(Path.GetTempPath(), $"empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        // Create a stub .sln so WorkspaceRootDiscovery thinks it's a root.
        File.WriteAllText(Path.Combine(emptyDir, "test.sln"), "");
        try
        {
            var exitCode = await BenchPerfCommand.RunAsync(
                preset: "latency",
                subject: "test",
                prompt: "hello",
                rootOverride: emptyDir,
                agentOverride: new StubAgent("test"));

            Assert.Equal(1, exitCode);
        }
        finally
        {
            try { Directory.Delete(emptyDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string CreateTempWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"perf-test-ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        // Stub solution file so WorkspaceRootDiscovery works.
        File.WriteAllText(Path.Combine(dir, "test.sln"), "");
        // Run `agenteval init` to produce the canonical .agenteval/solution.json shape.
        InitCommand.RunAsync(name: "perf-test", rootOverride: dir).GetAwaiter().GetResult();
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Deterministic stub agent that returns a fixed response with synthetic timing.</summary>
    private sealed class StubAgent : IEvaluableAgent
    {
        public string Name { get; }
        public StubAgent(string name) { Name = name; }
        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            await Task.Delay(10, cancellationToken);
            return new AgentResponse
            {
                Text = "OK",
                TokenUsage = new TokenUsage { PromptTokens = 5, CompletionTokens = 5 }
            };
        }
    }
}
