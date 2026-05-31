// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Testing;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.Testing;

/// <summary>
/// Glass Box Phase 0 (T0.4): the <see cref="ScriptedChatClient"/> test driver — scripts tool calls,
/// finish reasons, token usage, streaming, and throws (the inputs chat-boundary / Trace Fidelity tests need
/// and that the existing text-only FakeChatClient cannot produce).
/// </summary>
public class ScriptedChatClientTests
{
    [Fact]
    public async Task AddText_YieldsTextResponseWithStopFinishReasonAndUsage()
    {
        // Arrange
        var client = new ScriptedChatClient().AddText("hello", inTok: 10, outTok: 5);

        // Act
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        // Assert
        Assert.Equal("hello", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(10, response.Usage!.InputTokenCount);
        Assert.Equal(5, response.Usage.OutputTokenCount);
    }

    [Fact]
    public async Task AddToolCall_YieldsFunctionCallContentWithNameCallIdAndArguments()
    {
        // Arrange
        var args = new Dictionary<string, object?> { ["query"] = "tokyo" };
        var client = new ScriptedChatClient().AddToolCall("c1", "SearchFlights", args);

        // Act
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "plan a trip") });

        // Assert
        var call = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal("SearchFlights", call.Name);
        Assert.Equal("c1", call.CallId);
        Assert.Equal("tokyo", call.Arguments!["query"]);
        Assert.Equal(ChatFinishReason.ToolCalls, response.FinishReason);
    }

    [Fact]
    public async Task AddThrow_MakesGetResponseAsyncThrow()
    {
        // Arrange
        var client = new ScriptedChatClient().AddThrow("provider down");

        // Act / Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") }));
        Assert.Equal("provider down", ex.Message);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_YieldsAtLeastOneUpdateCarryingTheText()
    {
        // Arrange
        var client = new ScriptedChatClient().AddText("streamed");

        // Act
        var text = string.Empty;
        var updateCount = 0;
        await foreach (var update in client.GetStreamingResponseAsync(new[] { new ChatMessage(ChatRole.User, "go") }))
        {
            updateCount++;
            text += update.Text;
        }

        // Assert
        Assert.True(updateCount >= 1);
        Assert.Contains("streamed", text);
    }

    [Fact]
    public async Task ConsumesTurnsInOrder_AndTracksReceivedMessagesAndCallCount()
    {
        // Arrange
        var client = new ScriptedChatClient().AddText("first").AddText("second");

        // Act
        var r1 = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "a") });
        var r2 = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "b") });

        // Assert
        Assert.Equal("first", r1.Text);
        Assert.Equal("second", r2.Text);
        Assert.Equal(2, client.CallCount);
        Assert.Equal(2, client.ReceivedMessages.Count);
    }

    [Fact]
    public async Task WhenQueueEmpty_ReturnsBenignStopResponse()
    {
        // Arrange — no scripted turns
        var client = new ScriptedChatClient();

        // Act
        var response = await client.GetResponseAsync(new[] { new ChatMessage(ChatRole.User, "hi") });

        // Assert
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(string.Empty, response.Text);
    }
}
