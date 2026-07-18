// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Cli.Infrastructure;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// <c>agenteval log-file to-fixture</c> — the CLI surface over <see cref="LogFixtureGenerator"/>. Credential-free
/// (no LLM call — <c>ScriptedChatClient</c> produces the captured input).
/// </summary>
public class LogFileCommandTests : IDisposable
{
    private readonly string _capturedPath = Path.Combine(Path.GetTempPath(), "agenteval-logfilecmd-captured-" + Guid.NewGuid().ToString("N") + ".jsonl");
    private readonly string _outPath = Path.Combine(Path.GetTempPath(), "agenteval-logfilecmd-fixture-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_capturedPath)) File.Delete(_capturedPath); } catch { /* best-effort cleanup */ }
        try { if (File.Exists(_outPath)) File.Delete(_outPath); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Create_ReturnsLogFileCommand_WithToFixtureSubcommand()
    {
        var command = LogFileCommand.Create();
        Assert.Equal("log-file", command.Name);
        Assert.Contains(command.Subcommands, c => c.Name == "to-fixture");
    }

    [Fact]
    public void Create_ReturnsLogFileCommand_WithReplaySubcommand()
    {
        var command = LogFileCommand.Create();
        Assert.Contains(command.Subcommands, c => c.Name == "replay");
    }

    [Fact]
    public async Task ReplayAsync_MissingCaptureFile_ReturnsRuntimeError()
    {
        var missing = new FileInfo(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".jsonl"));
        var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-replay-out-" + Guid.NewGuid().ToString("N") + ".md"));

        var exit = await LogFileCommand.ReplayAsync(missing, outFile, endpoint: null, model: null, apiKey: null, azureFromEnv: false, strictText: false, default);

        Assert.Equal(ExitCodes.RuntimeError, exit);
    }

    [Fact]
    public async Task ReplayAsync_NoTargetSpecified_ReturnsUsageError()
    {
        var captured = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-replay-captured-" + Guid.NewGuid().ToString("N") + ".jsonl"));
        File.WriteAllText(captured.FullName, "");
        var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-replay-out-" + Guid.NewGuid().ToString("N") + ".md"));
        try
        {
            var exit = await LogFileCommand.ReplayAsync(captured, outFile, endpoint: null, model: null, apiKey: null, azureFromEnv: false, strictText: false, default);
            Assert.Equal(ExitCodes.UsageError, exit);
        }
        finally
        {
            captured.Delete();
        }
    }

    [Fact]
    public async Task ReplayAsync_EndpointWithoutModel_ReturnsUsageError()
    {
        var captured = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-replay-captured-" + Guid.NewGuid().ToString("N") + ".jsonl"));
        File.WriteAllText(captured.FullName, "");
        var outFile = new FileInfo(Path.Combine(Path.GetTempPath(), "agenteval-replay-out-" + Guid.NewGuid().ToString("N") + ".md"));
        try
        {
            var exit = await LogFileCommand.ReplayAsync(
                captured, outFile, endpoint: "https://example.test", model: null, apiKey: null, azureFromEnv: false, strictText: false, default);
            Assert.Equal(ExitCodes.UsageError, exit);
        }
        finally
        {
            captured.Delete();
        }
    }

    [Fact]
    public async Task ToFixtureAsync_RealCapture_WritesFixtureJson_LoadableByScriptedChatClient()
    {
        using (var sink = new StreamWriter(_capturedPath))
        {
            var client = new FixtureCapturingChatClient(new ScriptedChatClient().AddText("captured via CLI test"), sink);
            await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });
        }

        var exit = await LogFileCommand.ToFixtureAsync(new FileInfo(_capturedPath), new FileInfo(_outPath), default);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(File.Exists(_outPath));

        var turns = await LogFileCommand.LoadFixtureAsync(_outPath);
        var replay = ScriptedChatClient.FromFixture(turns);
        var response = await replay.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });
        Assert.Equal("captured via CLI test", response.Text);
    }

    [Fact]
    public async Task ToFixtureAsync_MissingCaptureFile_ReturnsRuntimeError()
    {
        var exit = await LogFileCommand.ToFixtureAsync(
            new FileInfo(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".jsonl")),
            new FileInfo(_outPath), default);

        Assert.Equal(ExitCodes.RuntimeError, exit);
        Assert.False(File.Exists(_outPath));
    }

    [Fact]
    public async Task ToFixtureAsync_CreatesOutputParentDirectory_WhenItDoesNotExist()
    {
        using (var sink = new StreamWriter(_capturedPath))
        {
            var client = new FixtureCapturingChatClient(new ScriptedChatClient().AddText("ok"), sink);
            await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });
        }

        var nestedOut = Path.Combine(Path.GetTempPath(), "agenteval-logfilecmd-nested-" + Guid.NewGuid().ToString("N"), "sub", "fixture.json");
        try
        {
            var exit = await LogFileCommand.ToFixtureAsync(new FileInfo(_capturedPath), new FileInfo(nestedOut), default);
            Assert.Equal(ExitCodes.Success, exit);
            Assert.True(File.Exists(nestedOut));
        }
        finally
        {
            var top = Path.GetDirectoryName(Path.GetDirectoryName(nestedOut))!;
            try { Directory.Delete(top, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
