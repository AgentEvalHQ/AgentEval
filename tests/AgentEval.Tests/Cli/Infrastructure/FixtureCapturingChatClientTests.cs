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
/// The <c>--capture-fixture</c> decorator: writes every LLM round-trip as a structured JSONL line with FULL
/// fidelity (unlike <c>VerboseLoggingChatClient</c>'s lossy human-readable log). Verified by parsing each
/// written line back into <see cref="FixtureCaptureEntry"/> and asserting on the structured fields, matching
/// the format's own structured nature (not text-substring checks).
/// </summary>
public class FixtureCapturingChatClientTests
{
    private static IList<ChatMessage> Conversation() => new List<ChatMessage>
    {
        new(ChatRole.System, "You are a helpful travel agent."),
        new(ChatRole.User, "Plan a trip to Tokyo."),
    };

    private static List<FixtureCaptureEntry> ParseLines(string jsonl) =>
        jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => JsonSerializer.Deserialize<FixtureCaptureEntry>(l)!)
            .ToList();

    [Fact]
    public async Task GetResponseAsync_WritesOneLine_WithFullMessageArray_NotJustTheNewestMessage()
    {
        // The exact gap --log-file's format cannot fill: the WHOLE message array, not "N total (...)" + newest only.
        var scripted = new ScriptedChatClient().AddText("Sure, here is a plan.", inTok: 42, outTok: 7);
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(scripted, sink);

        await client.GetResponseAsync(Conversation(), new ChatOptions { Temperature = 0.2f, ModelId = "gpt-4o-mini" });

        var entry = Assert.Single(ParseLines(sink.ToString()));
        Assert.Equal("response", entry.Kind);
        Assert.Equal(0, entry.Index);
        Assert.Equal(2, entry.Request.Messages.Count);
        Assert.Equal("system", entry.Request.Messages[0].Role);
        Assert.Equal("You are a helpful travel agent.", entry.Request.Messages[0].Text);
        Assert.Equal("user", entry.Request.Messages[1].Role);
        Assert.Equal("Plan a trip to Tokyo.", entry.Request.Messages[1].Text);
        Assert.Equal("gpt-4o-mini", entry.Request.Options!.ModelId);
        Assert.Equal(0.2f, entry.Request.Options.Temperature);
        Assert.Equal("Sure, here is a plan.", entry.Response!.Text);
        Assert.Equal("stop", entry.Response.FinishReason);
        Assert.Equal(42, entry.Response.Usage!.InputTokens);
        Assert.Equal(7, entry.Response.Usage.OutputTokens);
    }

    [Fact]
    public async Task GetResponseAsync_ToolCall_CapturesCallIdNameAndArguments()
    {
        var scripted = new ScriptedChatClient().AddToolCall("c1", "SearchFlights", new Dictionary<string, object?> { ["to"] = "NRT" });
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(scripted, sink);

        await client.GetResponseAsync(Conversation());

        var entry = Assert.Single(ParseLines(sink.ToString()));
        var call = Assert.Single(entry.Response!.ToolCalls!);
        Assert.Equal("c1", call.CallId);
        Assert.Equal("SearchFlights", call.Name);
        Assert.Equal("NRT", call.Arguments!["to"]!.ToString());
    }

    [Fact]
    public async Task GetResponseAsync_ModelThrows_CapturesFullExceptionText_AndRethrows()
    {
        var scripted = new ScriptedChatClient().AddThrow("simulated provider outage");
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(scripted, sink);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Conversation()));

        var entry = Assert.Single(ParseLines(sink.ToString()));
        Assert.Equal("error", entry.Kind);
        Assert.Null(entry.Response);
        Assert.Contains("simulated provider outage", entry.Error, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", entry.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WritesOneLine_WithAccumulatedResponse()
    {
        var scripted = new ScriptedChatClient().AddText("Streamed answer.");
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(scripted, sink);

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(Conversation()))
        {
            chunks.Add(update);
        }

        Assert.NotEmpty(chunks);
        var entry = Assert.Single(ParseLines(sink.ToString()));
        Assert.Equal("response", entry.Kind);
        Assert.Equal("Streamed answer.", entry.Response!.Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ConsumerBreaksEarly_WritesAbandonedEntry()
    {
        var scripted = new ScriptedChatClient().AddText("a streamed answer");
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(scripted, sink);

        await foreach (var _ in client.GetStreamingResponseAsync(Conversation()))
        {
            break;
        }

        var entry = Assert.Single(ParseLines(sink.ToString()));
        Assert.Equal("abandoned", entry.Kind);
        Assert.Null(entry.Response);
    }

    [Fact]
    public async Task TwoRoundTrips_UseDistinctIndices()
    {
        var scripted = new ScriptedChatClient().AddText("First.").AddText("Second.");
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(scripted, sink);

        await client.GetResponseAsync(Conversation());
        await client.GetResponseAsync(Conversation());

        var entries = ParseLines(sink.ToString());
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries[0].Index);
        Assert.Equal(1, entries[1].Index);
    }

    [Fact]
    public async Task Label_AppearsOnEveryEntry()
    {
        var scripted = new ScriptedChatClient().AddText("Answer.");
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(scripted, sink, "judge");

        await client.GetResponseAsync(Conversation());

        var entry = Assert.Single(ParseLines(sink.ToString()));
        Assert.Equal("judge", entry.Label);
    }

    [Fact]
    public async Task ToolsOnOptions_CapturedWithNameAndDescription()
    {
        var tool = AIFunctionFactory.Create((string x) => x, "search", "Searches for flights.");
        var scripted = new ScriptedChatClient().AddText("ok");
        var sink = new StringWriter();
        var client = new FixtureCapturingChatClient(scripted, sink);

        await client.GetResponseAsync(Conversation(), new ChatOptions { Tools = [tool] });

        var entry = Assert.Single(ParseLines(sink.ToString()));
        var capturedTool = Assert.Single(entry.Request.Options!.Tools!);
        Assert.Equal("search", capturedTool.Name);
        Assert.Equal("Searches for flights.", capturedTool.Description);
        Assert.NotNull(capturedTool.ParametersSchema);
    }
}
