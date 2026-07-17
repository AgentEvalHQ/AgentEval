// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Infrastructure;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.Infrastructure;

/// <summary>
/// The <c>--log-file</c> troubleshooting decorator: writes every LLM round-trip as human-readable plain text.
/// Unrelated to Glass Box's <c>TraceRecordingChatClient</c> — no structured trace, just a raw dump for
/// debugging, verified here at the level a human reading the log file would care about (does the request
/// text/options show up, does the response text/usage show up, does a failure show the FULL exception).
/// </summary>
public class VerboseLoggingChatClientTests
{
    private static IList<ChatMessage> Conversation() => new List<ChatMessage>
    {
        new(ChatRole.System, "You are a helpful travel agent."),
        new(ChatRole.User, "Plan a trip to Tokyo."),
    };

    [Fact]
    public async Task GetResponseAsync_WritesRequestAndResponseBlocks_WithExpectedContent()
    {
        var scripted = new ScriptedChatClient().AddText("Sure, here is a plan.", inTok: 42, outTok: 7);
        var log = new StringWriter();
        var client = new VerboseLoggingChatClient(scripted, log);

        await client.GetResponseAsync(Conversation(), new ChatOptions { Temperature = 0.2f, ModelId = "gpt-4o-mini" });

        var text = log.ToString();
        Assert.Contains("REQUEST", text, StringComparison.Ordinal);
        Assert.Contains("Plan a trip to Tokyo.", text, StringComparison.Ordinal);
        Assert.Contains("modelId=gpt-4o-mini", text, StringComparison.Ordinal);
        Assert.Contains("RESPONSE", text, StringComparison.Ordinal);
        Assert.Contains("Sure, here is a plan.", text, StringComparison.Ordinal);
        Assert.Contains("FinishReason: stop", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_ToolCall_LogsToolNameAndArguments()
    {
        var scripted = new ScriptedChatClient().AddToolCall("c1", "SearchFlights", new Dictionary<string, object?> { ["to"] = "NRT" });
        var log = new StringWriter();
        var client = new VerboseLoggingChatClient(scripted, log);

        await client.GetResponseAsync(Conversation());

        var text = log.ToString();
        Assert.Contains("ToolCalls: SearchFlights", text, StringComparison.Ordinal);
        Assert.Contains("to=NRT", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetResponseAsync_ModelThrows_LogsFullExceptionAndRethrows()
    {
        var scripted = new ScriptedChatClient().AddThrow("simulated provider outage");
        var log = new StringWriter();
        var client = new VerboseLoggingChatClient(scripted, log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Conversation()));

        var text = log.ToString();
        Assert.Contains("ERROR", text, StringComparison.Ordinal);
        Assert.Contains("simulated provider outage", text, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);   // ex.ToString(), not just ex.Message
    }

    [Fact]
    public async Task GetStreamingResponseAsync_LogsRequestAndAccumulatedResponse()
    {
        var scripted = new ScriptedChatClient().AddText("Streamed answer.");
        var log = new StringWriter();
        var client = new VerboseLoggingChatClient(scripted, log);

        var chunks = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(Conversation()))
        {
            chunks.Add(update);
        }

        Assert.NotEmpty(chunks);
        var text = log.ToString();
        Assert.Contains("REQUEST", text, StringComparison.Ordinal);
        Assert.Contains("RESPONSE", text, StringComparison.Ordinal);
        Assert.Contains("Streamed answer.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoRoundTrips_UseDistinctIndices()
    {
        var scripted = new ScriptedChatClient().AddText("First.").AddText("Second.");
        var log = new StringWriter();
        var client = new VerboseLoggingChatClient(scripted, log);

        await client.GetResponseAsync(Conversation());
        await client.GetResponseAsync(Conversation());

        var text = log.ToString();
        Assert.Contains("[0] REQUEST", text, StringComparison.Ordinal);
        Assert.Contains("[1] REQUEST", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Label_AppearsInEveryHeaderLine()
    {
        var scripted = new ScriptedChatClient().AddText("Answer.");
        var log = new StringWriter();
        var client = new VerboseLoggingChatClient(scripted, log, "judge");

        await client.GetResponseAsync(Conversation());

        var text = log.ToString();
        Assert.Contains("[0] judge REQUEST", text, StringComparison.Ordinal);
        Assert.Contains("[0] judge RESPONSE", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ModelThrowsMidStream_LogsFullExceptionAndRethrows()
    {
        var scripted = new ScriptedChatClient().AddThrow("simulated stream outage");
        var log = new StringWriter();
        var client = new VerboseLoggingChatClient(scripted, log);

        async Task ConsumeAsync()
        {
            await foreach (var _ in client.GetStreamingResponseAsync(Conversation())) { }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(ConsumeAsync);

        var text = log.ToString();
        Assert.Contains("ERROR", text, StringComparison.Ordinal);
        Assert.Contains("simulated stream outage", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABANDONED", text, StringComparison.Ordinal);   // the ERROR entry is already terminal
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ConsumerBreaksEarly_LogsAbandonedEntry_NotASilentGap()
    {
        var scripted = new ScriptedChatClient().AddText("a streamed answer");
        var log = new StringWriter();
        var client = new VerboseLoggingChatClient(scripted, log);

        await foreach (var _ in client.GetStreamingResponseAsync(Conversation()))
        {
            break;   // abandon before natural completion
        }

        var text = log.ToString();
        Assert.Contains("REQUEST", text, StringComparison.Ordinal);
        Assert.Contains("ABANDONED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RESPONSE", text, StringComparison.Ordinal);   // no full response was ever captured
    }

    [Fact]
    public async Task TwoClientsSharingOneSynchronizedWriter_ConcurrentWrites_DoNotInterleaveOrCorrupt()
    {
        // The real-world shape this guards: a RedTeam scan's SUT/judge/attacker clients are separate
        // VerboseLoggingChatClient instances that can all be active at once, sharing the ONE writer
        // VerboseLog.Wrap hands out. TextWriter.Synchronized (what VerboseLog.Initialize actually uses in
        // production) is what makes this safe — proven here directly rather than assumed.
        var raw = new StringWriter();
        var shared = TextWriter.Synchronized(raw);
        var clientA = new VerboseLoggingChatClient(
            new ScriptedChatClient().AddText("A0").AddText("A1").AddText("A2"), shared, "clientA");
        var clientB = new VerboseLoggingChatClient(
            new ScriptedChatClient().AddText("B0").AddText("B1").AddText("B2"), shared, "clientB");

        await Task.WhenAll(
            Task.Run(async () => { for (var i = 0; i < 3; i++) { await clientA.GetResponseAsync(Conversation()); } }),
            Task.Run(async () => { for (var i = 0; i < 3; i++) { await clientB.GetResponseAsync(Conversation()); } }));

        var lines = raw.ToString().Split('\n');
        var headerLines = lines.Where(l => l.Contains("=== [", StringComparison.Ordinal)).ToList();
        Assert.Equal(12, headerLines.Count);   // 3 round-trips × (1 REQUEST + 1 RESPONSE) × 2 clients
        Assert.Equal(6, headerLines.Count(l => l.Contains("clientA", StringComparison.Ordinal)));
        Assert.Equal(6, headerLines.Count(l => l.Contains("clientB", StringComparison.Ordinal)));
        // Every header line is well-formed (a corrupted interleave would merge two headers onto one line,
        // producing a line matching BOTH labels, or a line with an unexpected NUMBER of "=== [" markers).
        Assert.All(headerLines, l => Assert.True(
            l.Contains("clientA", StringComparison.Ordinal) ^ l.Contains("clientB", StringComparison.Ordinal),
            $"header line belongs to exactly one client, got: {l}"));
    }
}
