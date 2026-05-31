// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Tracing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Tracing;

/// <summary>
/// Regression coverage for BUG-28: ChatTraceRecorder advertised tool-call tracking (the
/// TrackToolCalls option, default true, and the CombinedToolUsage field) but never populated
/// ChatTurn.ToolCalls or the combined report, so both were always empty. It now extracts tool
/// calls from each response via <see cref="ToolUsageExtractor"/>, honoring TrackToolCalls.
/// </summary>
public class ChatTraceRecorderToolCallTests
{
    /// <summary>Agent that returns a response carrying a FunctionCallContent + matching result.</summary>
    private sealed class ToolCallingAgent : IEvaluableAgent
    {
        private readonly string _toolName;

        public ToolCallingAgent(string toolName) => _toolName = toolName;

        public string Name => "ToolCallingAgent";

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            var callId = $"call-{_toolName}";
            var rawMessages = new List<object>
            {
                new ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, new List<AIContent>
                {
                    new TextContent("calling tool"),
                    new FunctionCallContent(callId, _toolName,
                        new Dictionary<string, object?> { ["city"] = "Berlin" })
                }),
                new ChatMessage(Microsoft.Extensions.AI.ChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent(callId, "sunny")
                })
            };

            return Task.FromResult(new AgentResponse { Text = "It is sunny.", RawMessages = rawMessages });
        }
    }

    [Fact]
    public async Task TrackToolCalls_PopulatesCombinedToolUsageAndTurn()
    {
        await using var recorder = new ChatTraceRecorder(new ToolCallingAgent("get_weather"), "tools");

        await recorder.AddUserTurnAsync("Weather in Berlin?");
        var result = recorder.GetResult();

        // Combined report is now populated (was always empty before BUG-28 fix).
        Assert.NotNull(result.CombinedToolUsage);
        Assert.Equal(1, result.CombinedToolUsage!.Count);
        Assert.True(result.CombinedToolUsage.WasToolCalled("get_weather"));
        Assert.Equal("sunny", result.CombinedToolUsage.Calls[0].Result?.ToString());

        // The agent turn carries the tool calls.
        var agentTurn = result.Turns.Single(t => t.IsAgentTurn);
        Assert.NotNull(agentTurn.ToolCalls);
        Assert.Equal("get_weather", agentTurn.ToolCalls!.Single().Name);
        Assert.Equal("sunny", agentTurn.ToolCalls.Single().Result);
    }

    [Fact]
    public async Task TrackToolCalls_AccumulatesGlobalOrderAcrossTurns()
    {
        await using var recorder = new ChatTraceRecorder(new ToolCallingAgent("get_weather"), "tools-multi");

        await recorder.AddUserTurnAsync("Turn 1");
        await recorder.AddUserTurnAsync("Turn 2");
        var result = recorder.GetResult();

        Assert.Equal(2, result.CombinedToolUsage!.Count);
        // Conversation-global 1-based ordering, not per-turn restart.
        Assert.Equal(new[] { 1, 2 }, result.CombinedToolUsage.Calls.Select(c => c.Order).ToArray());
    }

    [Fact]
    public async Task TrackToolCallsDisabled_LeavesToolUsageEmpty()
    {
        var options = new ChatTraceRecorderOptions { TrackToolCalls = false };
        await using var recorder = new ChatTraceRecorder(new ToolCallingAgent("get_weather"), "no-tools", options);

        await recorder.AddUserTurnAsync("Weather?");
        var result = recorder.GetResult();

        Assert.Equal(0, result.CombinedToolUsage!.Count);
        Assert.Null(result.Turns.Single(t => t.IsAgentTurn).ToolCalls);
    }
}
