// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Infrastructure;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Infrastructure;

/// <summary>
/// <c>agenteval log-file replay</c>'s core diff logic. Every test captures a REAL round-trip via
/// <see cref="FixtureCapturingChatClient"/> (not hand-authored JSON — matches <c>LogFixtureGeneratorTests</c>'
/// own discipline), then replays it against a DIFFERENT <see cref="ScriptedChatClient"/> standing in for "a
/// different model/deployment," and asserts on the resulting <see cref="ReplayVerdict"/>.
/// </summary>
public class LogFileReplayerTests
{
    private static IList<ChatMessage> Conversation() => new List<ChatMessage> { new(ChatRole.User, "hi") };

    private static async Task<IReadOnlyList<FixtureCaptureEntry>> CaptureAsync(ScriptedChatClient capturedClient, ChatOptions? options = null)
    {
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(capturedClient, sink);
        await client.GetResponseAsync(Conversation(), options);

        return sink.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => JsonSerializer.Deserialize<FixtureCaptureEntry>(l)!)
            .ToList();
    }

    [Fact]
    public async Task SameToolCallsAndFinishReason_DifferentText_Passes()
    {
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("hello"));
        var replayTarget = new ScriptedChatClient().AddText("hi there");

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        var row = Assert.Single(report.Rows);
        Assert.Equal(ReplayVerdict.Pass, row.Verdict);
    }

    [Fact]
    public async Task DifferentToolCalls_Fails()
    {
        var entries = await CaptureAsync(new ScriptedChatClient().AddToolCall("c1", "search", new Dictionary<string, object?> { ["q"] = "x" }));
        var replayTarget = new ScriptedChatClient().AddText("no tool call this time");

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        var row = Assert.Single(report.Rows);
        Assert.Equal(ReplayVerdict.Fail, row.Verdict);
        Assert.Contains(row.Details, d => d.Contains("Tool calls differ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DifferentFinishReason_Fails()
    {
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("plain text reply"));
        var replayTarget = new ScriptedChatClient().AddToolCall("c1", "search", new Dictionary<string, object?>());

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        var row = Assert.Single(report.Rows);
        Assert.Equal(ReplayVerdict.Fail, row.Verdict);
        Assert.Contains(row.Details, d => d.Contains("FinishReason differs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SameToolCallsAndFinishReason_DifferentResponseShape_Flags()
    {
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("short"));
        var longListText = "1. first item\n2. second item\n3. third item — " + new string('x', 500);
        var replayTarget = new ScriptedChatClient().AddText(longListText);

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        var row = Assert.Single(report.Rows);
        Assert.Equal(ReplayVerdict.Flag, row.Verdict);
        Assert.Contains(row.Details, d => d.Contains("Response shape differs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolCalls_SameNameAndArgKeys_DifferentArgValues_StillPasses()
    {
        // Exact argument VALUES are a diff detail, never a pass/fail signal — a different but valid phrasing
        // of the same intent is not a regression.
        var entries = await CaptureAsync(new ScriptedChatClient().AddToolCall("c1", "search", new Dictionary<string, object?> { ["q"] = "tokyo" }));
        var replayTarget = new ScriptedChatClient().AddToolCall("c2", "search", new Dictionary<string, object?> { ["q"] = "paris" });

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        Assert.Equal(ReplayVerdict.Pass, Assert.Single(report.Rows).Verdict);
    }

    [Fact]
    public async Task StrictText_ExactTextDiffers_Fails_EvenWhenStructurallyIdentical()
    {
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("hello"));
        var replayTarget = new ScriptedChatClient().AddText("hello!");

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: true);

        var row = Assert.Single(report.Rows);
        Assert.Equal(ReplayVerdict.Fail, row.Verdict);
        Assert.Contains(row.Details, d => d.Contains("--strict-text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StrictText_ExactTextMatches_Passes()
    {
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("hello"));
        var replayTarget = new ScriptedChatClient().AddText("hello");

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: true);

        Assert.Equal(ReplayVerdict.Pass, Assert.Single(report.Rows).Verdict);
    }

    [Fact]
    public async Task TargetThrows_Fails_ReportsExceptionType()
    {
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("hello"));
        var replayTarget = new ScriptedChatClient().AddThrow("replay target outage");

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        var row = Assert.Single(report.Rows);
        Assert.Equal(ReplayVerdict.Fail, row.Verdict);
        Assert.Contains("InvalidOperationException", row.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ErrorEntry_Skipped_ReportedInSkippedCount_NotAsARow()
    {
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(new ScriptedChatClient().AddThrow("captured-side failure"), sink);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Conversation()));
        var entries = sink.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => JsonSerializer.Deserialize<FixtureCaptureEntry>(l)!)
            .ToList();

        var report = await LogFileReplayer.ReplayAsync(entries, new ScriptedChatClient().AddText("ok"), strictText: false);

        Assert.Empty(report.Rows);
        Assert.Equal(1, report.SkippedNonResponseCount);
    }

    [Fact]
    public async Task MultipleEntries_ReplayedIndependently_NotChained()
    {
        // Two captured turns from a real multi-turn conversation, replayed against a target that has its OWN
        // two-turn script — proves each captured (messages, options) is resent as a complete, self-contained
        // request (the standard IChatClient contract), not simulated as a chained session.
        var captureClient = new ScriptedChatClient().AddText("first captured reply").AddText("second captured reply");
        var sink = new StringWriter();
        var capturing = new FixtureCapturingChatClient(captureClient, sink);
        await capturing.GetResponseAsync(Conversation());
        await capturing.GetResponseAsync(Conversation());
        var entries = sink.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => JsonSerializer.Deserialize<FixtureCaptureEntry>(l)!)
            .ToList();

        var replayTarget = new ScriptedChatClient().AddText("first replay reply").AddText("second replay reply");
        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        Assert.Equal(2, report.Rows.Count);
        Assert.All(report.Rows, r => Assert.Equal(ReplayVerdict.Pass, r.Verdict));
    }

    [Fact]
    public async Task ToolsOnRequest_AreDeclaredToTheTarget_ButNeverInvoked()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "search", "Searches things.");
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("ok"), new ChatOptions { Tools = [tool] });

        // A ScriptedChatClient never invokes tools itself (no UseFunctionInvocation in this test) — this just
        // proves the replay call doesn't throw when reconstructing ChatOptions.Tools from the capture.
        var replayTarget = new ScriptedChatClient().AddText("ok");
        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        Assert.Equal(ReplayVerdict.Pass, Assert.Single(report.Rows).Verdict);
    }

    [Fact]
    public async Task NullEntries_Throws()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => LogFileReplayer.ReplayAsync(null!, new ScriptedChatClient(), false));

    [Fact]
    public async Task NullAgainst_Throws()
    {
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("hi"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => LogFileReplayer.ReplayAsync(entries, null!, false));
    }

    [Fact]
    public async Task ToolWithNoCapturedSchema_ReplaysWithoutCrashing()
    {
        // Regression test for a real bug found in review: a captured tool with no schema (ParametersSchema
        // null — the capture-side outcome for any non-AIFunction AITool, or an AIFunction whose own JsonSchema
        // is ValueKind.Undefined) previously became JsonSchema = default(JsonElement) on replay's reconstructed
        // tool declaration. JsonElement.GetRawText() throws on ValueKind.Undefined — exactly what a real
        // provider's serializer calls the moment it sees this tool declaration (see the identical documented
        // gotcha at TraceRecordingChatClient.cs). SchemaProbingChatClient stands in for that serializer.
        var entries = await CaptureAsync(new ScriptedChatClient().AddText("ok"));
        var schemalessTool = entries[0] with
        {
            Request = entries[0].Request with
            {
                Options = new FixtureCaptureOptions(null, null, null,
                    Tools: new[] { new FixtureCaptureTool("search", "Searches things.", ParametersSchema: null) }),
            },
        };

        var probe = new SchemaProbingChatClient();
        var report = await LogFileReplayer.ReplayAsync(new[] { schemalessTool }, probe, strictText: false);

        Assert.Equal(ReplayVerdict.Pass, Assert.Single(report.Rows).Verdict);
        Assert.True(probe.SawValidSchema);
    }

    [Fact]
    public async Task DuplicateToolCall_CapturedTwiceActualOnce_Fails()
    {
        // Regression test for a real bug found in review: comparing tool calls via HashSet.SetEquals loses
        // call-count multiplicity — captured=[foo(),foo()] vs actual=[foo()] must FAIL (a real divergence),
        // not silently PASS because the two SETS happen to contain the same tool name.
        var entries = await CaptureAsync(new ScriptedChatClient().AddParallelToolCalls(
            ("c1", "search", new Dictionary<string, object?> { ["q"] = "a" }),
            ("c2", "search", new Dictionary<string, object?> { ["q"] = "b" })));
        var replayTarget = new ScriptedChatClient().AddToolCall("c1", "search", new Dictionary<string, object?> { ["q"] = "x" });

        var report = await LogFileReplayer.ReplayAsync(entries, replayTarget, strictText: false);

        var row = Assert.Single(report.Rows);
        Assert.Equal(ReplayVerdict.Fail, row.Verdict);
        Assert.Contains(row.Details, d => d.Contains("Tool calls differ", StringComparison.Ordinal));
    }

    // Stands in for a real provider's request serializer: touches JsonSchema.GetRawText() on every declared
    // AIFunction tool, exactly where ValueKind.Undefined throws InvalidOperationException.
    private sealed class SchemaProbingChatClient : IChatClient
    {
        public bool SawValidSchema { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            foreach (var tool in options?.Tools ?? [])
            {
                if (tool is AIFunction f)
                {
                    _ = f.JsonSchema.GetRawText();   // throws on ValueKind.Undefined — the bug this test guards
                    SawValidSchema = true;
                }
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { FinishReason = ChatFinishReason.Stop });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
