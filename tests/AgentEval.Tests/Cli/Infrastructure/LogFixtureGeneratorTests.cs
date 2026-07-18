// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Infrastructure;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Infrastructure;

/// <summary>
/// <c>agenteval log-file to-fixture</c>'s mapping logic: reads a captured JSONL file and drops everything
/// except each entry's response, mapping it onto <see cref="ScriptedFixtureTurn"/>. Exercised end-to-end
/// against real <see cref="FixtureCapturingChatClient"/> output (via <see cref="ScriptedChatClient"/> as the
/// captured client), not hand-authored JSON — proves the round-trip capture → generate is actually correct,
/// not just that the mapping function parses a hand-crafted line.
/// </summary>
public class LogFixtureGeneratorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "agenteval-logfixturegen-test-" + Guid.NewGuid().ToString("N") + ".jsonl");

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* best-effort cleanup */ }
    }

    private static IList<ChatMessage> Conversation() => new List<ChatMessage> { new(ChatRole.User, "hi") };

    [Fact]
    public async Task GenerateAsync_TextResponse_MapsToTextTurn()
    {
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(new ScriptedChatClient().AddText("hello", inTok: 3, outTok: 4), sink);
            await client.GetResponseAsync(Conversation());
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);

        var turn = Assert.Single(turns);
        Assert.Equal("hello", turn.Text);
        Assert.Equal("stop", turn.FinishReason);
        Assert.Equal(3, turn.InputTokens);
        Assert.Equal(4, turn.OutputTokens);
        Assert.Null(turn.ToolName);
        Assert.Null(turn.ToolCalls);
    }

    [Fact]
    public async Task GenerateAsync_SingleToolCallResponse_MapsToSingleToolCallTurn()
    {
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(
                new ScriptedChatClient().AddToolCall("c1", "SearchFlights", new Dictionary<string, object?> { ["to"] = "NRT" }), sink);
            await client.GetResponseAsync(Conversation());
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);

        var turn = Assert.Single(turns);
        Assert.Equal("c1", turn.ToolCallId);
        Assert.Equal("SearchFlights", turn.ToolName);
        Assert.Null(turn.ToolCalls);
    }

    [Fact]
    public async Task GenerateAsync_ParallelToolCallResponse_MapsToToolCallsList()
    {
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(
                new ScriptedChatClient().AddParallelToolCalls(
                    ("c1", "SearchFlights", new Dictionary<string, object?> { ["to"] = "NRT" }),
                    ("c2", "SearchHotels", new Dictionary<string, object?> { ["city"] = "Tokyo" })),
                sink);
            await client.GetResponseAsync(Conversation());
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);

        var turn = Assert.Single(turns);
        Assert.Null(turn.ToolName);   // the single-call trio must stay empty — ToolCalls is the populated path
        Assert.Equal(2, turn.ToolCalls!.Count);
        Assert.Contains(turn.ToolCalls, c => c.ToolName == "SearchFlights" && c.ToolCallId == "c1");
        Assert.Contains(turn.ToolCalls, c => c.ToolName == "SearchHotels" && c.ToolCallId == "c2");
    }

    [Fact]
    public async Task GenerateAsync_SingleToolCallResponse_WithAccompanyingText_PreservesText()
    {
        // Regression test for a real bug found in review: MapResponse's tool-call branches previously left
        // Text unset, silently dropping any lead-in/reasoning text a real provider attaches alongside a tool
        // call — even though ScriptedChatClient.GetResponseAsync fully supports Text + a tool call together
        // (they're not mutually exclusive), so the fixture round-trip lost real captured content for no reason.
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(
                new ScriptedChatClient().Add(new ScriptedTurn
                {
                    Text = "Let me look that up.",
                    ToolCallId = "c1",
                    ToolName = "SearchFlights",
                    ToolArgs = new Dictionary<string, object?> { ["to"] = "NRT" },
                    FinishReason = ChatFinishReason.ToolCalls,
                }),
                sink);
            await client.GetResponseAsync(Conversation());
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);

        var turn = Assert.Single(turns);
        Assert.Equal("Let me look that up.", turn.Text);
        Assert.Equal("SearchFlights", turn.ToolName);
    }

    [Fact]
    public async Task GenerateAsync_ParallelToolCallResponse_WithAccompanyingText_PreservesText()
    {
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(
                new ScriptedChatClient().Add(new ScriptedTurn
                {
                    Text = "Checking flights and hotels.",
                    ToolCalls =
                    [
                        new ScriptedToolCall { ToolCallId = "c1", ToolName = "SearchFlights", ToolArgs = new Dictionary<string, object?> { ["to"] = "NRT" } },
                        new ScriptedToolCall { ToolCallId = "c2", ToolName = "SearchHotels", ToolArgs = new Dictionary<string, object?> { ["city"] = "Tokyo" } },
                    ],
                    FinishReason = ChatFinishReason.ToolCalls,
                }),
                sink);
            await client.GetResponseAsync(Conversation());
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);

        var turn = Assert.Single(turns);
        Assert.Equal("Checking flights and hotels.", turn.Text);
        Assert.Equal(2, turn.ToolCalls!.Count);
    }

    [Fact]
    public async Task GenerateAsync_ErrorEntry_MapsToThrowTurn_MessageOnly_NotFullStackTrace()
    {
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(new ScriptedChatClient().AddThrow("simulated provider outage"), sink);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Conversation()));
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);

        var turn = Assert.Single(turns);
        Assert.NotNull(turn.Throw);
        Assert.Contains("simulated provider outage", turn.Throw, StringComparison.Ordinal);
        Assert.DoesNotContain("at AgentEval", turn.Throw, StringComparison.Ordinal);   // no stack frames, message-line only
    }

    [Fact]
    public async Task GenerateAsync_AbandonedEntry_SkippedWithoutThrowing()
    {
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(new ScriptedChatClient().AddText("a streamed answer"), sink);
            await foreach (var _ in client.GetStreamingResponseAsync(Conversation()))
            {
                break;   // abandon before natural completion
            }
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);

        Assert.Empty(turns);   // no exception, no scripted-fixture equivalent for an abandoned stream
    }

    [Fact]
    public async Task GenerateAsync_MixedEntries_PreservesOrder()
    {
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(
                new ScriptedChatClient().AddText("first").AddToolCall("c1", "search", new Dictionary<string, object?>()), sink);
            await client.GetResponseAsync(Conversation());
            await client.GetResponseAsync(Conversation());
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);

        Assert.Equal(2, turns.Count);
        Assert.Equal("first", turns[0].Text);
        Assert.Equal("search", turns[1].ToolName);
    }

    [Fact]
    public async Task GenerateAsync_RoundTripsThroughScriptedChatClientFromFixture()
    {
        // End-to-end proof: a real captured conversation becomes a fixture that replays identically.
        using (var sink = new StreamWriter(_path))
        {
            var client = new FixtureCapturingChatClient(new ScriptedChatClient().AddText("a real captured answer"), sink);
            await client.GetResponseAsync(Conversation());
        }

        var turns = await LogFixtureGenerator.GenerateAsync(_path);
        var replay = ScriptedChatClient.FromFixture(turns);
        var response = await replay.GetResponseAsync(Conversation());

        Assert.Equal("a real captured answer", response.Text);
    }
}
