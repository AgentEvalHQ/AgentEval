// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

// Disambiguate: AgentEval.Tracing also declares a ChatRole; use MEAI's for ChatMessage construction.
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Glass Box Phase 1 (T1.4): the <see cref="TraceRecordingChatClient"/> chat-boundary recorder.
/// Verifies one ChatTurn entry per LLM round-trip, the evidence classes (proposal §3.3), token mapping,
/// streaming, errors, correlation grouping, replay compatibility, and — critically — that the recorder
/// must be composed INNER of UseFunctionInvocation to capture every round-trip.
/// </summary>
public class TraceRecordingChatClientTests
{
    private static ChatOptions OptionsWithTool() => new()
    {
        Temperature = 0.2f,
        MaxOutputTokens = 100,
        Tools = new List<AITool> { AIFunctionFactory.Create(Ping) },
    };

    private static string Ping() => "pong";

    private static IList<ChatMessage> Conversation() => new List<ChatMessage>
    {
        new(ChatRole.System, "You are a helpful travel agent."),
        new(ChatRole.User, "Plan a trip to Tokyo."),
    };

    [Fact]
    public async Task FourCalls_ProduceOneChatTurnRequestAndResponsePerRoundTrip_WithDistinctIndices()
    {
        // Arrange — script a 4-turn loop: two tool calls, a summary, a final answer
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "SearchFlights", new Dictionary<string, object?> { ["to"] = "NRT" })
            .AddToolCall("c2", "SearchHotels", new Dictionary<string, object?> { ["city"] = "Tokyo" })
            .AddText("Here is a summary.")
            .AddText("Final answer.", inTok: 100, outTok: 50);
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(scripted, "planner", trace);

        // Act — issue four round-trips
        for (var i = 0; i < 4; i++)
        {
            await recorder.GetResponseAsync(Conversation(), OptionsWithTool());
        }

        // Assert — 4 ChatTurn Request + 4 ChatTurn Response, each with a distinct round-trip index
        var requests = trace.Entries.Where(e => e is { Scope: TraceEntryScope.ChatTurn, Type: TraceEntryType.Request }).ToList();
        var responses = trace.Entries.Where(e => e is { Scope: TraceEntryScope.ChatTurn, Type: TraceEntryType.Response }).ToList();
        Assert.Equal(4, requests.Count);
        Assert.Equal(4, responses.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, responses.Select(r => r.Index).ToArray());
    }

    [Fact]
    public async Task RecordsAtLeastEightEvidenceClasses()
    {
        // Arrange — a tool-call turn so tool-call args are present; options carry tools + sampling
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "SearchFlights", new Dictionary<string, object?> { ["to"] = "NRT" });
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(scripted, "planner", trace);

        // Act
        await recorder.GetResponseAsync(Conversation(), OptionsWithTool());
        var request = trace.Entries.First(e => e.Type == TraceEntryType.Request);
        var response = trace.Entries.First(e => e.Type == TraceEntryType.Response);

        // Assert — ≥8 of the 11 evidence classes in proposal §3.3 are captured
        var toolDef = Assert.Single(request.ToolDefinitions!);
        var toolCall = Assert.Single(response.ToolCalls!);
        Assert.Equal("You are a helpful travel agent.", request.SystemPrompt);                 // 1 system prompt
        Assert.Equal("Ping", toolDef.Name);                                                    // 2 tool definitions
        Assert.NotNull(toolDef.ParametersSchema);                                              //   (schema captured)
        Assert.Equal(0.2, Convert.ToDouble(request.RequestOptions!["temperature"]), 3);        // 3 request options
        Assert.Equal(100, Convert.ToInt32(request.RequestOptions["maxOutputTokens"]));         //   (max tokens)
        Assert.Equal("tool_calls", response.FinishReason);                                     // 4 finish reason
        Assert.Equal("SearchFlights", toolCall.Name);                                          // 5 tool-call args
        Assert.Contains("NRT", toolCall.Arguments);                                            //   (args serialized)
        Assert.NotNull(response.DurationMs);                                                   // 6 per-turn latency
        Assert.NotNull(response.Text);                                                         // 7 per-turn text (may be empty)
        Assert.Null(response.ProviderMetadata);                                                // 8 provider metadata handled (null when absent)
    }

    [Fact]
    public async Task PerTurnTokenUsage_MapsLongToInt()
    {
        // Arrange
        var scripted = new ScriptedChatClient().AddText("answer", inTok: 100, outTok: 50);
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(scripted, "planner", trace);

        // Act
        await recorder.GetResponseAsync(Conversation());
        var response = trace.Entries.First(e => e.Type == TraceEntryType.Response);

        // Assert
        Assert.Equal(100, response.TokenUsage!.PromptTokens);
        Assert.Equal(50, response.TokenUsage.CompletionTokens);
    }

    [Fact]
    public async Task WhenInnerThrows_RecordsChatTurnErrorEntryAndRethrows()
    {
        // Arrange
        var scripted = new ScriptedChatClient().AddThrow("provider down");
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(scripted, "planner", trace);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.GetResponseAsync(Conversation()));
        var error = Assert.Single(trace.Entries, e => e.Error is not null);
        Assert.Equal(TraceEntryScope.ChatTurn, error.Scope);
        Assert.Equal("provider down", error.Error!.Message);
    }

    [Fact]
    public async Task Streaming_RecordsChunksAndFinalizesStreamingResponseEntry()
    {
        // Arrange
        var scripted = new ScriptedChatClient().AddText("streamed text");
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(scripted, "planner", trace);

        // Act
        await foreach (var _ in recorder.GetStreamingResponseAsync(Conversation()))
        {
            // drain
        }

        // Assert
        var response = trace.Entries.First(e => e.Type == TraceEntryType.Response);
        Assert.True(response.IsStreaming);
        Assert.NotEmpty(response.StreamingChunks!);
        Assert.Equal("streamed text", response.Text);
    }

    [Fact]
    public async Task Streaming_WithToolCallAndUsage_RecordsToolCallsAndTokenUsage_LikeNonStreaming()
    {
        // Arrange — a streamed tool-call turn that also reports token usage (in a trailing usage chunk, as
        // real providers do). Before the fix, the streaming path hard-coded toolCalls/usage to null.
        var scripted = new ScriptedChatClient().Add(new ScriptedTurn
        {
            ToolCallId = "c1",
            ToolName = "SearchFlights",
            ToolArgs = new Dictionary<string, object?> { ["to"] = "NRT" },
            FinishReason = ChatFinishReason.ToolCalls,
            InputTokens = 80,
            OutputTokens = 12,
        });
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(scripted, "planner", trace);

        // Act — drain the full stream
        await foreach (var _ in recorder.GetStreamingResponseAsync(Conversation(), OptionsWithTool()))
        {
            // drain
        }

        // Assert — streaming now captures tool calls + usage + finish reason, matching the non-streaming path
        var response = trace.Entries.First(e => e.Type == TraceEntryType.Response);
        Assert.True(response.IsStreaming);
        Assert.Equal("SearchFlights", Assert.Single(response.ToolCalls!).Name);
        Assert.Equal(80, response.TokenUsage!.PromptTokens);
        Assert.Equal(12, response.TokenUsage.CompletionTokens);
        Assert.Equal("tool_calls", response.FinishReason);
    }

    [Fact]
    public async Task Streaming_ConsumerBreaksEarly_StillFinalizesResponseEntry_NoDanglingRequest()
    {
        // Arrange — a streamed text turn (text chunk + trailing usage chunk = 2 updates)
        var scripted = new ScriptedChatClient().AddText("partial", inTok: 5, outTok: 5);
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(scripted, "planner", trace);

        // Act — consume only the first update, then break (an abandoned/short-circuited stream)
        await foreach (var _ in recorder.GetStreamingResponseAsync(Conversation()))
        {
            break;
        }

        // Assert — the Request must NOT be left dangling: a streaming Response entry is finalized on break
        Assert.Equal(1, trace.Entries.Count(e => e.Type == TraceEntryType.Request));
        var response = Assert.Single(trace.Entries.Where(e => e.Type == TraceEntryType.Response));
        Assert.True(response.IsStreaming);
        Assert.Equal("partial", response.Text);   // what streamed before the break is captured
    }

    [Fact]
    public async Task WhenInsideCorrelationScope_AllEntriesShareCorrelationId_ElseNull()
    {
        // Arrange
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(new ScriptedChatClient().AddText("a").AddText("b"), "planner", trace);

        // Act — first call inside a scope, second call outside
        using (new ToolCorrelationScope("inv-1"))
        {
            await recorder.GetResponseAsync(Conversation());
        }

        await recorder.GetResponseAsync(Conversation());

        // Assert
        Assert.Equal("inv-1", trace.Entries[0].CorrelationId);   // request, turn 0
        Assert.Equal("inv-1", trace.Entries[1].CorrelationId);   // response, turn 0
        Assert.Null(trace.Entries[2].CorrelationId);             // request, turn 1 (no scope)
    }

    [Fact]
    public async Task V11Trace_ReplaysThroughTraceReplayingAgent_WithoutCorruptingPairs()
    {
        // Arrange — record a 2-turn v1.1 chat-boundary trace
        var scripted = new ScriptedChatClient().AddText("first").AddText("second");
        var trace = new AgentTrace();
        var recorder = new TraceRecordingChatClient(scripted, "planner", trace);
        await recorder.GetResponseAsync(Conversation());
        await recorder.GetResponseAsync(Conversation());

        // Act — replay groups by Index and pairs Request+Response; distinct ChatTurn indices must not corrupt it
        var replay = new TraceReplayingAgent(trace);
        var r1 = await replay.InvokeAsync("Plan a trip to Tokyo.");
        var r2 = await replay.InvokeAsync("Plan a trip to Tokyo.");

        // Assert — both recorded responses replay without BuildRequestResponsePairs choking (order-independent)
        var replayed = new[] { r1.Text, r2.Text };
        Assert.Contains("first", replayed);
        Assert.Contains("second", replayed);
    }

    [Fact]
    public async Task ComposedInnerOfFunctionInvocation_RecordsOneEntryPerRoundTrip()
    {
        // Arrange — FICC loops: tool-call turn, then final-answer turn (2 real round-trips)
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "Ping", new Dictionary<string, object?>())
            .AddText("done");
        var trace = new AgentTrace();
        var client = scripted.AsBuilder()
            .UseFunctionInvocation()                       // outer: the tool loop
            .UseTraceRecording("agent", trace)             // INNER of FICC → one entry per real round-trip ✅
            .Build();

        // Act — a single external call; FICC loops internally
        await client.GetResponseAsync(Conversation(), OptionsWithTool());

        // Assert — TWO ChatTurn responses (one per round-trip), not one
        var responses = trace.Entries.Count(e => e is { Scope: TraceEntryScope.ChatTurn, Type: TraceEntryType.Response });
        Assert.Equal(2, responses);
    }

    [Fact]
    public async Task ComposedOuterOfFunctionInvocation_RecordsOnlyOneEntry_DocumentingWhyInnerIsRequired()
    {
        // Arrange — the WRONG order: recorder outer of FICC. The loop runs below it, invisible.
        var scripted = new ScriptedChatClient()
            .AddToolCall("c1", "Ping", new Dictionary<string, object?>())
            .AddText("done");
        var trace = new AgentTrace();
        var client = scripted.AsBuilder()
            .UseTraceRecording("agent", trace)             // outer of FICC → sees ONE call for the whole loop ❌
            .UseFunctionInvocation()
            .Build();

        // Act
        await client.GetResponseAsync(Conversation(), OptionsWithTool());

        // Assert — only ONE response recorded, proving the inner-of-FICC composition rule
        var responses = trace.Entries.Count(e => e is { Scope: TraceEntryScope.ChatTurn, Type: TraceEntryType.Response });
        Assert.Equal(1, responses);
    }
}
