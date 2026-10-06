// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>bench memory</c> and <c>bench longmemeval</c> without a <c>.agenteval/</c> workspace must point at the command
/// that creates one. That is <c>agenteval init-workspace</c>; <c>agenteval init</c> scaffolds a dataset and does not
/// create <c>.agenteval/</c>, so following the old hint left the user where they started.
/// </summary>
/// <remarks>
/// Both commands stop at the workspace check before resolving a model, so no provider is needed.
/// </remarks>
[Collection("ConsoleTests")]
public class BenchMemoryWorkspaceHintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agenteval-memhint-" + Guid.NewGuid().ToString("N"));

    public BenchMemoryWorkspaceHintTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task BenchMemory_NoWorkspace_PointsAtInitWorkspace()
    {
        // Fails on the old hint, "Run `agenteval init` first."
        var (exitCode, stderr) = await CaptureStderrAsync(() => BenchMemoryCommand.RunAsync("quick", "hint-subject", _root));

        Assert.Equal(1, exitCode);
        Assert.Contains("Run `agenteval init-workspace` first.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Run `agenteval init` first.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BenchLongMemEval_NoWorkspace_PointsAtInitWorkspace()
    {
        // Fails on the old hint, "Run `agenteval init` first."
        var (exitCode, stderr) = await CaptureStderrAsync(() => BenchLongMemEvalCommand.RunAsync("subset", "hint-subject", _root));

        Assert.Equal(1, exitCode);
        Assert.Contains("Run `agenteval init-workspace` first.", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Run `agenteval init` first.", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stderr)> CaptureStderrAsync(Func<Task<int>> action)
    {
        var previous = Console.Error;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            var exitCode = await action();
            return (exitCode, stderr.ToString());
        }
        finally
        {
            Console.SetError(previous);
        }
    }
}
