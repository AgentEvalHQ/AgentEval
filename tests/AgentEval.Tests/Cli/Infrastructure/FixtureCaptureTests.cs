// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Infrastructure;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Cli.Infrastructure;

/// <summary>
/// The ambient <c>--capture-fixture</c> hook — sibling to <see cref="VerboseLog"/>, same shape, same
/// static-state-leak risk across parallel test classes (see <see cref="VerboseLogTests"/>'s matching remark
/// for why this joins the same "ConsoleTests" collection).
/// </summary>
[Collection("ConsoleTests")]
public class FixtureCaptureTests : IDisposable
{
    public FixtureCaptureTests() => FixtureCapture.Initialize(null);

    public void Dispose() => FixtureCapture.Initialize(null);

    [Fact]
    public void Initialize_NullPath_WriterStaysNull_AndReturnsNull()
    {
        var writer = FixtureCapture.Initialize(null);

        Assert.Null(writer);
        Assert.Null(FixtureCapture.Writer);
    }

    [Fact]
    public void Initialize_EmptyOrWhitespacePath_TreatedAsNotSet()
    {
        var writer = FixtureCapture.Initialize("   ");

        Assert.Null(writer);
        Assert.Null(FixtureCapture.Writer);
    }

    [Fact]
    public void Initialize_RealPath_OpensFileAndSetsWriter()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-fixturecapture-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var writer = FixtureCapture.Initialize(path);
            Assert.NotNull(writer);
            Assert.Same(writer, FixtureCapture.Writer);
            writer!.Write("hello");
            writer.Dispose();

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
        var path = Path.Combine(Path.GetTempPath(), "agenteval-fixturecapture-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var first = FixtureCapture.Initialize(path);
            first!.Write("first run content that should be gone");
            first.Dispose();

            var second = FixtureCapture.Initialize(path);
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
        FixtureCapture.Initialize(null);
        var inner = new ScriptedChatClient();

        var wrapped = FixtureCapture.Wrap(inner);

        Assert.Same(inner, wrapped);
    }

    [Fact]
    public void Wrap_WriterSet_ReturnsFixtureCapturingChatClient()
    {
        var path = Path.Combine(Path.GetTempPath(), "agenteval-fixturecapture-test-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using var writer = FixtureCapture.Initialize(path);
            var inner = new ScriptedChatClient();

            var wrapped = FixtureCapture.Wrap(inner);

            Assert.IsType<FixtureCapturingChatClient>(wrapped);
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
