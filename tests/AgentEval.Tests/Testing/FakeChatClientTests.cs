// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for FakeChatClient - the test utility itself needs testing!
/// </summary>
public class FakeChatClientTests
{
    private static ChatMessage[] CreateMessages(string text) =>
        [new ChatMessage(ChatRole.User, text)];

    [Fact]
    public async Task GetResponseAsync_WithQueuedResponse_ReturnsQueuedValue()
    {
        // Arrange
        var expectedResponse = """{"score": 95, "reasoning": "Test response"}""";
        var client = new FakeChatClient(expectedResponse);

        // Act
        var response = await client.GetResponseAsync(CreateMessages("test"));

        // Assert
        Assert.Equal(expectedResponse, response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_EmptyQueue_ReturnsDefaultResponse()
    {
        // Arrange
        var client = new FakeChatClient(); // Empty queue

        // Act
        var response = await client.GetResponseAsync(CreateMessages("test"));

        // Assert
        Assert.Contains("50", response.Text); // Default score is 50
        Assert.Contains("Default fake response", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_MultipleResponses_ConsumedInOrder()
    {
        // Arrange
        var client = new FakeChatClient("first", "second", "third");

        // Act
        var r1 = await client.GetResponseAsync(CreateMessages("1"));
        var r2 = await client.GetResponseAsync(CreateMessages("2"));
        var r3 = await client.GetResponseAsync(CreateMessages("3"));

        // Assert
        Assert.Equal("first", r1.Text);
        Assert.Equal("second", r2.Text);
        Assert.Equal("third", r3.Text);
    }

    [Fact]
    public async Task GetResponseAsync_QueueExhausted_FallsBackToDefault()
    {
        // Arrange
        var client = new FakeChatClient("only-one");

        // Act
        var r1 = await client.GetResponseAsync(CreateMessages("1"));
        var r2 = await client.GetResponseAsync(CreateMessages("2")); // Queue exhausted

        // Assert
        Assert.Equal("only-one", r1.Text);
        Assert.Contains("50", r2.Text); // Default
    }

    [Fact]
    public async Task WithResponse_ChainsCorrectly()
    {
        // Arrange
        var client = new FakeChatClient()
            .WithResponse("response1")
            .WithResponse("response2")
            .WithResponse("response3");

        // Act
        var r1 = await client.GetResponseAsync(CreateMessages("1"));
        var r2 = await client.GetResponseAsync(CreateMessages("2"));
        var r3 = await client.GetResponseAsync(CreateMessages("3"));

        // Assert
        Assert.Equal("response1", r1.Text);
        Assert.Equal("response2", r2.Text);
        Assert.Equal("response3", r3.Text);
    }

    [Fact]
    public async Task ThrowOnNextCall_ThrowsException()
    {
        // Arrange
        var client = new FakeChatClient("normal-response");
        client.ThrowOnNextCall = true;
        client.ThrowMessage = "Simulated API failure";

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync(CreateMessages("test")));

        Assert.Equal("Simulated API failure", ex.Message);
    }

    [Fact]
    public async Task ThrowOnNextCall_ResetsAfterThrow()
    {
        // Arrange
        var client = new FakeChatClient("normal-response");
        client.ThrowOnNextCall = true;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetResponseAsync(CreateMessages("1")));

        // Second call should succeed
        var response = await client.GetResponseAsync(CreateMessages("2"));

        // Assert
        Assert.False(client.ThrowOnNextCall);
        Assert.Equal("normal-response", response.Text);
    }

    [Fact]
    public async Task ReceivedMessages_TracksAllCalls()
    {
        // Arrange
        var client = new FakeChatClient("r1", "r2", "r3");

        // Act
        await client.GetResponseAsync(CreateMessages("message1"));
        await client.GetResponseAsync(CreateMessages("message2"));
        await client.GetResponseAsync(CreateMessages("message3"));

        // Assert
        Assert.Equal(3, client.ReceivedMessages.Count);
        Assert.Equal("message1", client.ReceivedMessages[0].First().Text);
        Assert.Equal("message2", client.ReceivedMessages[1].First().Text);
        Assert.Equal("message3", client.ReceivedMessages[2].First().Text);
    }

    [Fact]
    public async Task CallCount_IncrementsCorrectly()
    {
        // Arrange
        var client = new FakeChatClient("r1", "r2", "r3");

        // Assert initial
        Assert.Equal(0, client.CallCount);

        // Act & Assert
        await client.GetResponseAsync(CreateMessages("1"));
        Assert.Equal(1, client.CallCount);

        await client.GetResponseAsync(CreateMessages("2"));
        Assert.Equal(2, client.CallCount);

        await client.GetResponseAsync(CreateMessages("3"));
        Assert.Equal(3, client.CallCount);
    }

    [Fact]
    public async Task LastPrompt_ReturnsLastMessageText()
    {
        // Arrange
        var client = new FakeChatClient("response");

        // Act
        await client.GetResponseAsync(CreateMessages("first prompt"));
        await client.GetResponseAsync(CreateMessages("second prompt"));
        await client.GetResponseAsync(CreateMessages("third prompt"));

        // Assert
        Assert.Equal("third prompt", client.LastPrompt);
    }

    [Fact]
    public void LastPrompt_NoCallsMade_ReturnsNull()
    {
        // Arrange
        var client = new FakeChatClient();

        // Assert
        Assert.Null(client.LastPrompt);
    }

    [Fact]
    public async Task GetResponseAsync_WithMultipleMessagesInConversation_TracksAll()
    {
        // Arrange
        var client = new FakeChatClient("response");
        var conversation = new[]
        {
            new ChatMessage(ChatRole.System, "You are a helpful assistant"),
            new ChatMessage(ChatRole.User, "Hello"),
            new ChatMessage(ChatRole.Assistant, "Hi there!"),
            new ChatMessage(ChatRole.User, "What's the weather?")
        };

        // Act
        await client.GetResponseAsync(conversation);

        // Assert
        Assert.Single(client.ReceivedMessages);
        Assert.Equal(4, client.ReceivedMessages[0].Count());
    }

    [Fact]
    public void GetStreamingResponseAsync_ThrowsNotImplemented()
    {
        // Arrange
        var client = new FakeChatClient();

        // Act & Assert
        Assert.Throws<NotImplementedException>(() =>
            client.GetStreamingResponseAsync(CreateMessages("test")));
    }

    [Fact]
    public void GetService_ReturnsNull()
    {
        // Arrange
        var client = new FakeChatClient();

        // Act
        var service = client.GetService(typeof(string));

        // Assert
        Assert.Null(service);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        // Arrange
        var client = new FakeChatClient();

        // Act & Assert - should not throw
        client.Dispose();
    }

    [Fact]
    public async Task GetResponseAsync_WithEmptyString_HandlesGracefully()
    {
        // Arrange
        var client = new FakeChatClient("");

        // Act
        var response = await client.GetResponseAsync(CreateMessages("test"));

        // Assert
        Assert.Equal("", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_WithJsonResponse_PreservesExactFormat()
    {
        // Arrange
        var jsonResponse = """
            {
                "score": 85,
                "faithfulClaims": ["claim1", "claim2"],
                "hallucinatedClaims": [],
                "reasoning": "All claims are supported"
            }
            """;
        var client = new FakeChatClient(jsonResponse);

        // Act
        var response = await client.GetResponseAsync(CreateMessages("test"));

        // Assert
        Assert.Equal(jsonResponse, response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_WithUnicodeContent_HandlesCorrectly()
    {
        // Arrange
        var unicodeResponse = """{"score": 90, "reasoning": "日本語テスト 🚀 مرحبا"}""";
        var client = new FakeChatClient(unicodeResponse);

        // Act
        var response = await client.GetResponseAsync(CreateMessages("Unicode: 日本語"));

        // Assert
        Assert.Equal(unicodeResponse, response.Text);
        Assert.Contains("日本語", client.LastPrompt);
    }

    [Fact]
    public async Task GetResponseAsync_ConcurrentCalls_ThreadSafe()
    {
        // Arrange - queue 100 responses
        var responses = Enumerable.Range(1, 100).Select(i => $"response-{i}").ToArray();
        var client = new FakeChatClient(responses);

        // Act - make 100 concurrent calls
        var tasks = Enumerable.Range(1, 100)
            .Select(i => client.GetResponseAsync(CreateMessages($"message-{i}")))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.Equal(100, client.CallCount);
        Assert.Equal(100, client.ReceivedMessages.Count);
        // Note: Order is not guaranteed in concurrent scenario, but all should complete
    }
}
