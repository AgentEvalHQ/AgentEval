// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Infrastructure;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Cli.Infrastructure;

/// <summary>
/// The ambient <c>--log-file</c> hook every IChatClient-constructing call site wraps its result through.
/// Tests reset <see cref="VerboseLog.Writer"/> to null on both entry and exit so ordering against other tests
/// in the same process (xUnit does not guarantee isolation between static state by default) can never leak.
/// </summary>
/// <remarks>
/// Joined to the "ConsoleTests" collection (the reset-on-entry/exit pattern above only protects against
/// SEQUENTIAL leakage within a single-threaded run; xUnit parallelizes across test CLASSES by default, so
/// without this, a concurrently-running class that indirectly reads/writes <see cref="VerboseLog.Writer"/> —
/// e.g. <c>AzureChatAgentFactoryTests</c>, already in this same collection — could race these tests).
/// </remarks>
[Collection("ConsoleTests")]
public class VerboseLogTests : IDisposable
{
    public VerboseLogTests() => VerboseLog.Initialize(null);

    public void Dispose() => VerboseLog.Initialize(null);

    [Fact]
    public void Initialize_NullPath_WriterStaysNull_AndReturnsNull()
    {
        var writer = VerboseLog.Initialize(null);

        Assert.Null(writer);
        Assert.Null(VerboseLog.Writer);
    }

    [Fact]
    public void Initialize_EmptyOrWhitespacePath_TreatedAsNotSet()
    {
        var writer = VerboseLog.Initialize("   ");

        Assert.Null(writer);
        Assert.Null(VerboseLog.Writer);
    }

    [Fact]
    public void Initialize_RealPath_OpensFileAndSetsWriter()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-verboselog-test-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var writer = VerboseLog.Initialize(path);
            Assert.NotNull(writer);
            Assert.Same(writer, VerboseLog.Writer);
            writer!.Write("hello");
            writer.Dispose();   // close before reading back — matches how a user reads the log after the CLI exits

            Assert.Equal("hello", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Initialize_ReRunOverwritesPriorContent()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-verboselog-test-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var first = VerboseLog.Initialize(path);
            first!.Write("first run content that should be gone");
            first.Dispose();

            var second = VerboseLog.Initialize(path);
            second!.Write("second run");
            second.Dispose();

            Assert.DoesNotContain("first run", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Wrap_NoWriterSet_ReturnsSameInstance_NoOp()
    {
        VerboseLog.Initialize(null);
        var inner = new ScriptedChatClient();

        var wrapped = VerboseLog.Wrap(inner);

        Assert.Same(inner, wrapped);
    }

    [Fact]
    public void Wrap_WriterSet_ReturnsVerboseLoggingChatClient()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-verboselog-test-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            using var writer = VerboseLog.Initialize(path);
            var inner = new ScriptedChatClient();

            var wrapped = VerboseLog.Wrap(inner);

            Assert.IsType<VerboseLoggingChatClient>(wrapped);
            Assert.NotSame(inner, wrapped);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
