// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Tests.TestHelpers;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Tests for ChatClientAgentAdapter streaming tool extraction.
/// Validates that FunctionCallContent and FunctionResultContent are correctly
/// emitted as ToolCallStarted/ToolCallCompleted chunks during streaming.
/// </summary>
public class ChatClientAgentAdapterStreamingToolTests
{
    // ═══════════════════════════════════════════════════════════════════
    // STREAMING — TOOL CALL EXTRACTION
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InvokeStreamingAsync_WhenToolCallPresent_ShouldYieldToolCallStartedChunk()
    {
        // Arrange
        var mock = MockStreamingChatClient.WithSingleToolCall(
            toolName: "GetWeather",
            callId: "call_001",
            arguments: new Dictionary<string, object?> { { "city", "Seattle" } },
            result: "72°F, Sunny");
        var adapter = new ChatClientAgentAdapter(mock, "TestAgent");

        // Act
        var chunks = await CollectChunksAsync(adapter, "What's the weather?");

        // Assert
        var toolStartChunk = chunks.FirstOrDefault(c => c.ToolCallStarted != null);
        Assert.NotNull(toolStartChunk);
        Assert.Equal("GetWeather", toolStartChunk.ToolCallStarted!.Name);
        Assert.Equal("call_001", toolStartChunk.ToolCallStarted.CallId);
        Assert.NotNull(toolStartChunk.ToolCallStarted.Arguments);
        Assert.Equal("Seattle", toolStartChunk.ToolCallStarted.Arguments!["city"]);
    }

    [Fact]
    public async Task InvokeStreamingAsync_WhenToolResultPresent_ShouldYieldToolCallCompletedChunk()
    {
        // Arrange
        var mock = MockStreamingChatClient.WithSingleToolCall(
            toolName: "GetWeather",
            callId: "call_002",
            result: "72°F, Sunny");
        var adapter = new ChatClientAgentAdapter(mock, "TestAgent");

        // Act
        var chunks = await CollectChunksAsync(adapter, "What's the weather?");

        // Assert
        var toolCompleteChunk = chunks.FirstOrDefault(c => c.ToolCallCompleted != null);
        Assert.NotNull(toolCompleteChunk);
        Assert.Equal("call_002", toolCompleteChunk.ToolCallCompleted!.CallId);
        Assert.Equal("72°F, Sunny", toolCompleteChunk.ToolCallCompleted.Result);
        Assert.Null(toolCompleteChunk.ToolCallCompleted.Exception);
    }

    [Fact]
    public async Task InvokeStreamingAsync_ToolCallStartedAndCompleted_ShouldHaveMatchingCallIds()
    {
        // Arrange
        const string callId = "call_match_test";
        var mock = MockStreamingChatClient.WithSingleToolCall(
            toolName: "SearchDB",
            callId: callId,
            result: "Found 5 records");
        var adapter = new ChatClientAgentAdapter(mock, "TestAgent");

        // Act
        var chunks = await CollectChunksAsync(adapter, "Search for records");

        // Assert
        var started = chunks.First(c => c.ToolCallStarted != null);
        var completed = chunks.First(c => c.ToolCallCompleted != null);

        Assert.Equal(callId, started.ToolCallStarted!.CallId);
        Assert.Equal(callId, completed.ToolCallCompleted!.CallId);
    }

    [Fact]
    public async Task InvokeStreamingAsync_MultipleToolCalls_ShouldYieldAllToolChunks()
    {
        // Arrange
        var mock = MockStreamingChatClient.WithMultipleToolCalls(
            ("GetWeather", "call_a", "Sunny"),
            ("BookHotel", "call_b", "Booked!"),
            ("GetDirections", "call_c", "Turn left"));
        var adapter = new ChatClientAgentAdapter(mock, "TestAgent");

        // Act
        var chunks = await CollectChunksAsync(adapter, "Plan my trip");

        // Assert
        var startedChunks = chunks.Where(c => c.ToolCallStarted != null).ToList();
        var completedChunks = chunks.Where(c => c.ToolCallCompleted != null).ToList();

        Assert.Equal(3, startedChunks.Count);
        Assert.Equal(3, completedChunks.Count);

        Assert.Equal("GetWeather", startedChunks[0].ToolCallStarted!.Name);
        Assert.Equal("BookHotel", startedChunks[1].ToolCallStarted!.Name);
        Assert.Equal("GetDirections", startedChunks[2].ToolCallStarted!.Name);

        // Verify ordering: started before completed for each
        var startedIndex0 = chunks.IndexOf(startedChunks[0]);
        var completedIndex0 = chunks.IndexOf(completedChunks[0]);
        Assert.True(startedIndex0 < completedIndex0, "ToolCallStarted should come before ToolCallCompleted");
    }

    [Fact]
    public async Task InvokeStreamingAsync_TextAndToolCallsMixed_ShouldYieldBothTypes()
    {
        // Arrange
        var mock = MockStreamingChatClient.WithFullScenario(
            initialText: "Let me check ",
            toolCalls: [("GetWeather", "call_mix", "72°F")],
            finalText: "The weather is 72°F.",
            inputTokens: 50,
            outputTokens: 30);
        var adapter = new ChatClientAgentAdapter(mock, "TestAgent");

        // Act
        var chunks = await CollectChunksAsync(adapter, "What's the weather?");

        // Assert: text chunks present
        var textChunks = chunks.Where(c => !string.IsNullOrEmpty(c.Text)).ToList();
        Assert.True(textChunks.Count >= 2, $"Expected at least 2 text chunks, got {textChunks.Count}");
        Assert.Contains(textChunks, c => c.Text == "Let me check ");
        Assert.Contains(textChunks, c => c.Text == "The weather is 72°F.");

        // Assert: tool chunks present
        Assert.Single(chunks, c => c.ToolCallStarted != null);
        Assert.Single(chunks, c => c.ToolCallCompleted != null);

        // Assert: final chunk has usage
        var finalChunk = chunks.Last();
        Assert.True(finalChunk.IsComplete);
        Assert.NotNull(finalChunk.Usage);
        Assert.Equal(50, finalChunk.Usage!.PromptTokens);
        Assert.Equal(30, finalChunk.Usage.CompletionTokens);
    }

    [Fact]
    public async Task InvokeStreamingAsync_NoToolCalls_ShouldNotYieldToolChunks()
    {
        // Arrange: text-only streaming
        var mock = MockStreamingChatClient.WithTextOnly("Hello ", "World!");
        var adapter = new ChatClientAgentAdapter(mock, "TestAgent");

        // Act
        var chunks = await CollectChunksAsync(adapter, "Hi");

        // Assert: no tool chunks
        Assert.DoesNotContain(chunks, c => c.ToolCallStarted != null);
        Assert.DoesNotContain(chunks, c => c.ToolCallCompleted != null);

        // Assert: text is present
        var textChunks = chunks.Where(c => !string.IsNullOrEmpty(c.Text)).ToList();
        Assert.Equal(2, textChunks.Count);
        Assert.Equal("Hello ", textChunks[0].Text);
        Assert.Equal("World!", textChunks[1].Text);

        // Assert: final complete chunk
        Assert.Contains(chunks, c => c.IsComplete);
    }

    [Fact]
    public async Task InvokeStreamingAsync_ToolCallWithException_ShouldPropagateException()
    {
        // Arrange: tool call that returns with an exception
        var testException = new InvalidOperationException("Tool failed");
        var funcResultWithError = new FunctionResultContent("call_err", result: null);
        funcResultWithError.Exception = testException;
        var chunks = new List<List<AIContent>>
        {
            new List<AIContent> { new FunctionCallContent("call_err", "FailingTool") },
            new List<AIContent> { funcResultWithError }
        };
        var mock = new MockStreamingChatClient(chunks);
        var adapter = new ChatClientAgentAdapter(mock, "TestAgent");

        // Act
        var resultChunks = await CollectChunksAsync(adapter, "Do something");

        // Assert
        var completedChunk = resultChunks.First(c => c.ToolCallCompleted != null);
        Assert.NotNull(completedChunk.ToolCallCompleted!.Exception);
        Assert.Equal("Tool failed", completedChunk.ToolCallCompleted.Exception!.Message);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NON-STREAMING — REGRESSION TESTS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InvokeAsync_WithToolCalls_StillExtractsToolsViaRawMessages()
    {
        // Arrange: Configure chunks to include tool call content for non-streaming path
        var callId = "call_ns_001";
        var funcCall = new FunctionCallContent(callId, "SearchDB",
            new Dictionary<string, object?> { { "query", "test" } });
        var funcResult = new FunctionResultContent(callId, "Found 3 results");

        // MockStreamingChatClient's GetResponseAsync auto-builds ChatMessages from chunks
        var streamsWithTools = new List<List<AIContent>>
        {
            new List<AIContent> { funcCall },
            new List<AIContent> { funcResult },
            new List<AIContent> { new TextContent("I found 3 results.") }
        };
        var mock = new MockStreamingChatClient(streamsWithTools);
        var adapter = new ChatClientAgentAdapter(mock, "TestAgent");

        // Act
        var response = await adapter.InvokeAsync("Search for test");

        // Assert: RawMessages should contain function call/result content
        Assert.NotNull(response.RawMessages);
        Assert.True(response.RawMessages.Count > 0);

        // Extract tool usage via ToolUsageExtractor (the non-streaming path)
        var toolReport = ToolUsageExtractor.Extract(response.RawMessages);
        Assert.Equal(1, toolReport.Count);
        Assert.Equal("SearchDB", toolReport.ToolNames.First());
    }

    // ═══════════════════════════════════════════════════════════════════
    // INJECT CONVERSATION HISTORY — works even when includeHistory=false
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InjectConversationHistory_WhenIncludeHistoryFalse_InjectedMessagesStillSentToClient()
    {
        // Arrange — adapter constructed with default includeHistory=false (via Create())
        var mock = new MockStreamingChatClient(
            streamingChunks: [[new TextContent("response")]],
            nonStreamingResponse: "response");
        var adapter = ChatClientAgentAdapter.Create(mock, name: "TestAgent");

        adapter.InjectConversationHistory(new[]
        {
            ("What is 2+2?", "4"),
            ("What is 3+3?", "6"),
        });

        // Act
        _ = await adapter.InvokeAsync("And 4+4?");

        // Assert — the mock should have received the injected turns + the current prompt
        var received = mock.ReceivedMessages.Single().ToList();
        // No system prompt, so we expect: user(What is 2+2?), assistant(4),
        //                                 user(What is 3+3?), assistant(6), user(And 4+4?)
        Assert.Equal(5, received.Count);
        Assert.Equal(ChatRole.User, received[0].Role);
        Assert.Equal("What is 2+2?", received[0].Text);
        Assert.Equal(ChatRole.Assistant, received[1].Role);
        Assert.Equal("4", received[1].Text);
        Assert.Equal(ChatRole.User, received[2].Role);
        Assert.Equal("What is 3+3?", received[2].Text);
        Assert.Equal(ChatRole.Assistant, received[3].Role);
        Assert.Equal("6", received[3].Text);
        Assert.Equal(ChatRole.User, received[4].Role);
        Assert.Equal("And 4+4?", received[4].Text);
    }

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static async Task<List<AgentResponseChunk>> CollectChunksAsync(
        IStreamableAgent agent, string prompt)
    {
        var chunks = new List<AgentResponseChunk>();
        await foreach (var chunk in agent.InvokeStreamingAsync(prompt))
        {
            chunks.Add(chunk);
        }
        return chunks;
    }
}
