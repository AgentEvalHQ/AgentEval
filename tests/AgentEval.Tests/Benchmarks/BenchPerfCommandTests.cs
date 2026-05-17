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
            Assert.True(exitCode == 0 || exitCode == 2,
                $"Expected exit code 0 (pass) or 2 (warn/fail), got {exitCode}.");

            // Confirm a run was persisted into .agenteval/ — runs live under
            // .agenteval/subjects/{subject}/runs/{runId}/.
            var subjectsDir = Path.Combine(workspace, ".agenteval", "subjects");
            Assert.True(Directory.Exists(subjectsDir), $"subjects/ dir not created at {subjectsDir}");
            var anyRunFile = Directory.EnumerateFiles(subjectsDir, "*.json", SearchOption.AllDirectories).Any();
            Assert.True(anyRunFile, $"No run files written under {subjectsDir} after BenchPerfCommand.RunAsync.");
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

            Assert.Equal(1, exitCode);
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
