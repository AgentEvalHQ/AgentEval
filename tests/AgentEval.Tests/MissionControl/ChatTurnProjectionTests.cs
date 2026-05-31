// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

// MissionControl is net10.0-only (its ProjectReference is guarded in the test csproj), so these tests
// only compile/run on net10.0 — matching every other tests/MissionControl/*.cs file.
#if NET10_0_OR_GREATER

using AgentEval.MissionControl.GraphQL;
using AgentEval.Tracing;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Glass Box Phase 7: the Mission Control <see cref="ChatTurnProjection"/> — projects a rich v1.1
/// <see cref="AgentTrace"/> into the per-turn <see cref="ChatTurn"/> timeline (net10-only, like Mission Control).
/// </summary>
public class ChatTurnProjectionTests
{
    [Fact]
    public void FromTrace_ProjectsOneChatTurnPerResponse_MergingRequestToolDefinitions()
    {
        // Arrange — a 2-turn chat-boundary trace; turn 0 advertises a tool and emits a tool call
        var trace = new AgentTrace();
        trace.Entries.Add(TraceEntry.ForChatRequest(0, "inv", "be helpful", "look it up",
            new List<TraceToolDefinition> { new() { Name = "Lookup" } }, null));
        trace.Entries.Add(TraceEntry.ForChatResponse(0, "inv", "calling tool", 11,
            new TraceTokenUsage { PromptTokens = 12, CompletionTokens = 3 },
            new List<TraceToolCall> { new() { Name = "Lookup" } }, "tool_calls", null));
        trace.Entries.Add(TraceEntry.ForChatResponse(1, "inv", "all done", 8,
            new TraceTokenUsage { PromptTokens = 20, CompletionTokens = 6 }, null, "stop", null));

        // Act
        var turns = ChatTurnProjection.FromTrace(trace);

        // Assert
        Assert.Equal(2, turns.Count);
        Assert.Equal(0, turns[0].Index);
        Assert.Equal("tool_calls", turns[0].FinishReason);
        Assert.Equal(new[] { "Lookup" }, turns[0].ToolNames);
        Assert.Equal(new[] { "Lookup" }, turns[0].ToolDefinitions);   // merged from the matching Request
        Assert.Equal(12, turns[0].PromptTokens);
        Assert.Equal(11, turns[0].LatencyMs);
        Assert.Equal("stop", turns[1].FinishReason);
        Assert.Empty(turns[1].ToolNames);
    }

    [Fact]
    public void FromTrace_V10AgentTrace_HasNoChatTurns()
    {
        // Arrange — agent-boundary (v1.0) entries are not ChatTurn-scoped
        var trace = new AgentTrace { Version = "1.0" };
        trace.Entries.Add(new TraceEntry { Type = TraceEntryType.Request, Index = 0, Prompt = "hi" });
        trace.Entries.Add(new TraceEntry { Type = TraceEntryType.Response, Index = 0, Text = "ok" });

        // Act / Assert
        Assert.Empty(ChatTurnProjection.FromTrace(trace));
    }
}

#endif
