// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Tests for the two evaluator-facing capabilities the adapter carries: pinned answer sampling and
/// timestamped history injection.
/// </summary>
public class ChatClientAgentAdapterCapabilityTests
{
    [Fact]
    public async Task InvokeAsync_NoSamplingRequested_SendsTheCallersOptionsUntouched()
    {
        var callerOptions = new ChatOptions { MaxOutputTokens = 128 };
        var client = new RecordingChatClient();
        var adapter = new ChatClientAgentAdapter(client, "subject", chatOptions: callerOptions);

        await adapter.InvokeAsync("hello");

        Assert.Same(callerOptions, Assert.Single(client.Options));
    }

    [Fact]
    public async Task ConfigureAnswerSampling_AppliesToTheCallWithoutMutatingTheCallersOptions()
    {
        var callerOptions = new ChatOptions { MaxOutputTokens = 128 };
        var client = new RecordingChatClient();
        var adapter = new ChatClientAgentAdapter(client, "subject", chatOptions: callerOptions);

        var acknowledgement = adapter.ConfigureAnswerSampling(
            new AnswerSamplingRequest { Temperature = 0.2, Seed = 4242 });
        await adapter.InvokeAsync("hello");

        Assert.True(acknowledgement.TemperatureApplied);
        Assert.True(acknowledgement.SeedApplied);
        var sent = Assert.Single(client.Options);
        Assert.Equal(0.2f, sent!.Temperature);
        Assert.Equal(4242, sent.Seed);
        Assert.Equal(128, sent.MaxOutputTokens);
        // The caller's instance is theirs: an adapter shared between runs must not leak one run's
        // sampling into another's.
        Assert.Null(callerOptions.Temperature);
        Assert.Null(callerOptions.Seed);
        Assert.NotSame(callerOptions, sent);
    }

    [Fact]
    public void ConfigureAnswerSampling_EmptyRequest_AcknowledgesNothing()
    {
        var adapter = new ChatClientAgentAdapter(new RecordingChatClient(), "subject");

        var acknowledgement = adapter.ConfigureAnswerSampling(new AnswerSamplingRequest());

        Assert.False(acknowledgement.TemperatureApplied);
        Assert.False(acknowledgement.SeedApplied);
    }

    [Fact]
    public async Task InjectTimestampedConversationHistory_StampsTurnsAndStatesTheQueryTime()
    {
        var client = new RecordingChatClient();
        var adapter = new ChatClientAgentAdapter(client, "subject");
        adapter.InjectTimestampedConversationHistory(new TimestampedConversationHistory
        {
            QueryTime = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
            Turns =
            [
                new TimestampedConversationTurn(
                    "I joined the club",
                    "Noted",
                    new DateTimeOffset(2026, 2, 24, 19, 45, 0, TimeSpan.Zero),
                    0)
            ]
        });

        await adapter.InvokeAsync("Can I renew yet?");

        var sent = Assert.Single(client.Messages);
        Assert.Contains(
            sent,
            message => message.Role == ChatRole.System &&
                       message.Text == "Current date and time: 2026/06/01 09:00 UTC.");
        Assert.Contains(
            sent,
            message => message.Text == "[2026/02/24 19:45] I joined the club");
        Assert.Contains(
            sent,
            message => message.Text == "[2026/02/24 19:45] Noted");
    }

    [Fact]
    public async Task ResetSessionAsync_ClearsTheInjectedQueryTimeAlongWithTheHistory()
    {
        var client = new RecordingChatClient();
        var adapter = new ChatClientAgentAdapter(client, "subject");
        adapter.InjectTimestampedConversationHistory(new TimestampedConversationHistory
        {
            QueryTime = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero),
            Turns = []
        });

        await adapter.ResetSessionAsync();
        await adapter.InvokeAsync("anything?");

        Assert.DoesNotContain(
            Assert.Single(client.Messages),
            message => message.Text.Contains("Current date and time", StringComparison.Ordinal));
    }

    private sealed class RecordingChatClient : IChatClient
    {
        public List<ChatOptions?> Options { get; } = [];

        public List<IReadOnlyList<ChatMessage>> Messages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            Messages.Add(chatMessages.ToList());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
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
