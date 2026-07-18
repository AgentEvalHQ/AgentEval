// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Infrastructure;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Infrastructure;

/// <summary>
/// The single composing hook every <c>IChatClient</c>-constructing call site routes through — proves
/// <c>--log-file</c> and <c>--capture-fixture</c> apply independently and composably (either, both, or
/// neither). Joins "ConsoleTests" — see <see cref="VerboseLogTests"/>'s matching remark; exercises both
/// <see cref="VerboseLog"/> and <see cref="FixtureCapture"/> static state.
/// </summary>
[Collection("ConsoleTests")]
public class CliChatClientDiagnosticsTests : IDisposable
{
    public CliChatClientDiagnosticsTests()
    {
        VerboseLog.Initialize(null);
        FixtureCapture.Initialize(null);
    }

    public void Dispose()
    {
        VerboseLog.Initialize(null);
        FixtureCapture.Initialize(null);
    }

    [Fact]
    public void Wrap_NeitherActive_ReturnsSameInstance_NoOp()
    {
        var inner = new ScriptedChatClient();
        Assert.Same(inner, CliChatClientDiagnostics.Wrap(inner));
    }

    [Fact]
    public void Wrap_OnlyLogFileActive_WrapsWithVerboseLoggingOnly()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "agenteval-diag-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var logWriter = VerboseLog.Initialize(logPath);
            var inner = new ScriptedChatClient();

            var wrapped = CliChatClientDiagnostics.Wrap(inner);

            Assert.IsType<VerboseLoggingChatClient>(wrapped);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    [Fact]
    public void Wrap_OnlyCaptureFixtureActive_WrapsWithFixtureCapturingOnly()
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), "agenteval-diag-fixture-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using var fixtureWriter = FixtureCapture.Initialize(fixturePath);
            var inner = new ScriptedChatClient();

            var wrapped = CliChatClientDiagnostics.Wrap(inner);

            Assert.IsType<FixtureCapturingChatClient>(wrapped);
        }
        finally
        {
            if (File.Exists(fixturePath)) File.Delete(fixturePath);
        }
    }

    [Fact]
    public async Task Wrap_BothActive_BothObserveTheSameRoundTrip()
    {
        var logPath = Path.Combine(Path.GetTempPath(), "agenteval-diag-both-log-" + Guid.NewGuid().ToString("N") + ".log");
        var fixturePath = Path.Combine(Path.GetTempPath(), "agenteval-diag-both-fixture-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var logWriter = VerboseLog.Initialize(logPath);
            var fixtureWriter = FixtureCapture.Initialize(fixturePath);
            var inner = new ScriptedChatClient().AddText("composed answer");

            var wrapped = CliChatClientDiagnostics.Wrap(inner);
            // Outermost layer must still be a functioning IChatClient — proves the composition round-trips a
            // real call through BOTH decorators without either one interfering with the response.
            var response = await wrapped.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
            Assert.Equal("composed answer", response.Text);

            // Close before reading back — matches VerboseLogTests' own precedent (a StreamWriter's file handle
            // is still open here; File.ReadAllText would otherwise race an in-progress write / hit a sharing violation).
            logWriter!.Dispose();
            fixtureWriter!.Dispose();
            Assert.Contains("composed answer", File.ReadAllText(logPath), StringComparison.Ordinal);
            Assert.Contains("composed answer", File.ReadAllText(fixturePath), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
            if (File.Exists(fixturePath)) File.Delete(fixturePath);
        }
    }
}
