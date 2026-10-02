// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;

// Disambiguate: AgentEval.Tracing also declares a ChatRole, and AgentEval.Testing also declares a ToolCallInfo.
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using ToolCallInfo = AgentEval.Core.ToolCallInfo;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// <see cref="TraceRecordingAgent"/> records the agent boundary's own account of each invocation: the finish
/// reason the wrapped agent reported and the tool calls it surfaced, with arguments serialized the way the
/// chat-boundary recorder serializes them. Before this, a trace recorded here and used as the agent side of a
/// Trace Fidelity reconciliation reported its own recording gaps as framework findings: every
/// content_filter/length turn as suppressed_finish_reason, every non-streaming tool call as missing_tool_calls,
/// and every streamed tool call with arguments as argument_drift.
/// </summary>
public class TraceRecordingAgentBoundaryTests
{
    // Fails on the old recorder: its response entry had no FinishReason (suppressed_finish_reason = 1 for the
    // content_filter turn) and no ToolCalls (missing_tool_calls = 1 for "search"). Passes now: every class is 0.
    [Fact]
    public async Task NonStreaming_TraceReconcilesCleanlyAgainstTheChatBoundaryOfTheSameRun()
    {
        var chatTrace = new AgentTrace();
        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", "search", new Dictionary<string, object?> { ["q"] = "flights" })
            .Add(new ScriptedTurn { Text = "Partial answer.", FinishReason = ChatFinishReason.ContentFilter });
        using var chatBoundary = new TraceRecordingChatClient(scripted, "agent", chatTrace);
        await using var recorder = new TraceRecordingAgent(new ToolLoopAgent(chatBoundary), "fidelity");

        await recorder.InvokeAsync("Find flights.");

        var report = new TraceFidelityRunner().Reconcile(recorder.Trace, chatTrace);

        Assert.All(report.Discrepancies, d => Assert.True(d.Count == 0, $"{d.ClassKey}: {string.Join("; ", d.Examples)}"));
        Assert.Equal(1.0, report.OverallScore, 6);
    }

    // Fails on the old recorder: FinishReason and ToolCalls were both null on the non-streaming response entry.
    [Fact]
    public async Task NonStreaming_RecordsTheReportedFinishReasonAndToolCalls_WithResultsPairedByCallId()
    {
        var weatherArgs = new Dictionary<string, object?> { ["city"] = "Berlin" };
        var agent = new FixedResponseAgent(new AgentResponse
        {
            Text = "Done.",
            FinishReason = "length",
            RawMessages = new List<object>
            {
                new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    new FunctionCallContent("call_a", "get_weather", weatherArgs),
                    new FunctionCallContent("call_b", "get_time"),
                }),
                new ChatMessage(ChatRole.Tool, new List<AIContent>
                {
                    // Results arrive in a different order from the calls; pairing is by call id.
                    new FunctionResultContent("call_b", "unknown") { Exception = new TimeoutException("clock down") },
                    new FunctionResultContent("call_a", "sunny"),
                }),
            },
        });
        await using var recorder = new TraceRecordingAgent(agent, "non_streaming_account");

        await recorder.InvokeAsync("Weather and time?");

        var entry = recorder.Trace.Entries.Single(e => e.Type == TraceEntryType.Response);
        Assert.Equal("length", entry.FinishReason);
        Assert.NotNull(entry.ToolCalls);
        Assert.Equal(new[] { "get_weather", "get_time" }, entry.ToolCalls!.Select(c => c.Name).ToArray());

        var weather = entry.ToolCalls[0];
        Assert.Equal(TraceMapping.ToToolCall(new FunctionCallContent("call_a", "get_weather", weatherArgs)).Arguments, weather.Arguments);
        Assert.Equal("sunny", weather.Result);
        Assert.True(weather.Succeeded);

        var time = entry.ToolCalls[1];
        Assert.Null(time.Arguments);
        Assert.Equal("unknown", time.Result);
        Assert.False(time.Succeeded);
        Assert.Equal("clock down", time.Error);
    }

    // Fails on the old recorder: a streamed tool call was recorded with its name only (Arguments null), which
    // reconciles as argument_drift = 1 against a chat boundary that saw {"q":"flights"}. Passes now: 0.
    [Fact]
    public async Task Streaming_RecordsToolCallArguments_SerializedLikeTheChatBoundary()
    {
        var args = new Dictionary<string, object?> { ["q"] = "flights" };
        var agent = new ScriptedStreamingAgent(
            new AgentResponseChunk { ToolCallStarted = new ToolCallInfo { Name = "search", CallId = "call_1", Arguments = args } },
            new AgentResponseChunk { Text = "Found 3.", IsComplete = true });
        await using var recorder = new TraceRecordingAgent(agent, "streaming_args");

        await foreach (var _ in recorder.InvokeStreamingAsync("Find flights."))
        {
        }

        var entry = recorder.Trace.Entries.Single(e => e.Type == TraceEntryType.Response);
        var call = Assert.Single(entry.ToolCalls!);
        Assert.Equal("search", call.Name);
        var chatSideCall = TraceMapping.ToToolCall(new FunctionCallContent("call_1", "search", args));
        Assert.Equal(chatSideCall.Arguments, call.Arguments);

        var chatTrace = new AgentTrace();
        chatTrace.Entries.Add(TraceEntry.ForChatResponse(
            index: 0, correlationId: null, text: null, durationMs: 1, usage: null,
            toolCalls: new List<TraceToolCall> { chatSideCall }, finishReason: "tool_calls", providerMetadata: null));
        var report = new TraceFidelityRunner().Reconcile(recorder.Trace, chatTrace);
        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.ArgumentDrift).Count);
    }

    // Fails on the old recorder: after a streamed invocation it created Trace.Performance carrying only the
    // time-to-first-token, so the token totals read 0 until the trace was finalized, and Trace Fidelity (which
    // reads Performance before the entries) reported token_under_reporting = 1 against a chat boundary that
    // saw the same 15 tokens. Passes now: the totals come from the recorded entry until finalization.
    [Fact]
    public async Task Streaming_UnfinalizedTrace_DoesNotReportRecordedTokensAsUnderReported()
    {
        var agent = new ScriptedStreamingAgent(
            new AgentResponseChunk { Text = "Hello" },
            new AgentResponseChunk { IsComplete = true, Usage = new TokenUsage { PromptTokens = 10, CompletionTokens = 5 } });
        await using var recorder = new TraceRecordingAgent(agent, "streaming_tokens");

        await foreach (var _ in recorder.InvokeStreamingAsync("Hi"))
        {
        }

        var chatTrace = new AgentTrace();
        chatTrace.Entries.Add(TraceEntry.ForChatResponse(
            index: 0, correlationId: null, text: "Hello", durationMs: 1,
            usage: new TraceTokenUsage { PromptTokens = 10, CompletionTokens = 5 },
            toolCalls: null, finishReason: "stop", providerMetadata: null));
        var report = new TraceFidelityRunner().Reconcile(recorder.Trace, chatTrace);

        Assert.Equal(0, report.Discrepancies.Single(d => d.ClassKey == TraceFidelityRubric.TokenUnderReporting).Count);

        // Time-to-first-token still reaches the finalized performance block.
        await recorder.ToJsonAsync();
        Assert.NotNull(recorder.Trace.Performance?.TimeToFirstTokenMs);
        Assert.Equal(15, recorder.Trace.Performance!.TotalTokens);
    }

    // Fails on the old recorder: it attached a result to the first call whose NAME occurs inside the result's
    // call id, so "call_get_weather" was attached to the "get" call and "get_weather" got no result.
    [Fact]
    public async Task Streaming_PairsEachToolResultWithItsCallByCallId()
    {
        var agent = new ScriptedStreamingAgent(
            new AgentResponseChunk { ToolCallStarted = new ToolCallInfo { Name = "get", CallId = "call_1" } },
            new AgentResponseChunk { ToolCallStarted = new ToolCallInfo { Name = "get_weather", CallId = "call_get_weather" } },
            new AgentResponseChunk { ToolCallCompleted = new ToolResultInfo { CallId = "call_get_weather", Result = "sunny" } },
            new AgentResponseChunk { ToolCallCompleted = new ToolResultInfo { CallId = "call_1", Exception = new InvalidOperationException("no such key") } },
            new AgentResponseChunk { Text = "It is sunny.", IsComplete = true });
        await using var recorder = new TraceRecordingAgent(agent, "streaming_pairing");

        await foreach (var _ in recorder.InvokeStreamingAsync("Weather?"))
        {
        }

        var calls = recorder.Trace.Entries.Single(e => e.Type == TraceEntryType.Response).ToolCalls!;
        var weather = calls.Single(c => c.Name == "get_weather");
        Assert.Equal("sunny", weather.Result);
        Assert.True(weather.Succeeded);

        var get = calls.Single(c => c.Name == "get");
        Assert.Null(get.Result);
        Assert.False(get.Succeeded);
        Assert.Equal("no such key", get.Error);
    }

    /// <summary>
    /// Runs one tool round-trip over an <see cref="IChatClient"/> and reports it the way MAFAgentAdapter does:
    /// the run's messages in <see cref="AgentResponse.RawMessages"/> and the last turn's finish reason.
    /// </summary>
    private sealed class ToolLoopAgent : IEvaluableAgent
    {
        private readonly IChatClient _client;

        public ToolLoopAgent(IChatClient client) => _client = client;

        public string Name => "ToolLoopAgent";

        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var history = new List<ChatMessage> { new(ChatRole.User, prompt) };

            var first = await _client.GetResponseAsync(history, cancellationToken: cancellationToken);
            history.AddRange(first.Messages);
            var call = first.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
            history.Add(new ChatMessage(ChatRole.Tool, new List<AIContent> { new FunctionResultContent(call.CallId, "3 results") }));

            var second = await _client.GetResponseAsync(history, cancellationToken: cancellationToken);
            history.AddRange(second.Messages);

            return new AgentResponse
            {
                Text = second.Text,
                RawMessages = history.Skip(1).Cast<object>().ToList(),
                FinishReason = second.FinishReason?.Value,
            };
        }
    }

    private sealed class FixedResponseAgent : IEvaluableAgent
    {
        private readonly AgentResponse _response;

        public FixedResponseAgent(AgentResponse response) => _response = response;

        public string Name => "FixedResponseAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(_response);
    }

    private sealed class ScriptedStreamingAgent : IStreamableAgent
    {
        private readonly AgentResponseChunk[] _chunks;

        public ScriptedStreamingAgent(params AgentResponseChunk[] chunks) => _chunks = chunks;

        public string Name => "ScriptedStreamingAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming only.");

        public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
            string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in _chunks)
            {
                await Task.Yield();
                yield return chunk;
            }
        }
    }
}
