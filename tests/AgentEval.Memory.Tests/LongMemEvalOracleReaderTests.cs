// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.LongMemEval;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

public sealed class LongMemEvalOracleReaderTests
{
    [Fact]
    public async Task InvokeAsync_InjectedHistory_UsesDirectChatPathAndReturnsText()
    {
        var client = new RecordingChatClient("oracle answer");
        var reader = new LongMemEvalOracleReader(client);
        reader.InjectConversationHistory(
        [
            ("safe user history", "safe assistant history"),
            ("second user history", "second assistant history")
        ]);

        var response = await reader.InvokeAsync("current question");

        Assert.Equal("oracle answer", response.Text);
        var messages = Assert.Single(client.Calls);
        Assert.Equal(
            [ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant, ChatRole.User],
            messages.Select(message => message.Role));
        Assert.Equal("safe user history", messages[0].Text);
        Assert.Equal("safe assistant history", messages[1].Text);
        Assert.Equal("current question", messages[^1].Text);
        Assert.All(messages, message =>
        {
            Assert.DoesNotContain("has_answer", message.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("answer_session_ids", message.Text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task ResetSessionAsync_BetweenQuestions_PreventsOracleStateCarryover()
    {
        var client = new RecordingChatClient("first", "second");
        var reader = new LongMemEvalOracleReader(client);
        reader.InjectConversationHistory([("first-only evidence", "ack")]);
        await reader.InvokeAsync("first question");

        await reader.ResetSessionAsync();
        reader.InjectConversationHistory([("second-only evidence", "ack")]);
        await reader.InvokeAsync("second question");

        Assert.Equal(2, client.Calls.Count);
        var secondPayload = string.Join("\n", client.Calls[1].Select(message => message.Text));
        Assert.DoesNotContain("first-only evidence", secondPayload);
        Assert.Contains("second-only evidence", secondPayload);
    }

    private sealed class RecordingChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public ChatClientMetadata Metadata { get; } =
            new("oracle-reader-test", new Uri("http://localhost"), "same-model");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(chatMessages.Select(message =>
                new ChatMessage(message.Role, message.Text)).ToArray());
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, _responses.Dequeue())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
