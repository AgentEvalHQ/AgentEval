// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.MAF;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentResponse = AgentEval.Core.AgentResponse;
using MAFAgentResponse = Microsoft.Agents.AI.AgentResponse;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Tests for <see cref="MAFAgentAdapter"/> — verifies IHistoryInjectableAgent
/// and core adapter behavior.
/// </summary>
public class MAFAgentAdapterTests
{
    [Fact]
    public void MAFAgentAdapter_ImplementsIHistoryInjectableAgent()
    {
        var agent = new HistoryCapturingAgent("Test", "response");
        var adapter = new MAFAgentAdapter(agent);

        Assert.IsAssignableFrom<IHistoryInjectableAgent>(adapter);
    }

    [Fact]
    public async Task InjectConversationHistory_MessagesIncludedInNextInvocation()
    {
        // Arrange
        var agent = new HistoryCapturingAgent("Test", "response");
        var adapter = new MAFAgentAdapter(agent);

        // Act: inject history then invoke
        adapter.InjectConversationHistory(new[]
        {
            ("Hello", "Hi there!"),
            ("How are you?", "I'm doing well.")
        });

        await adapter.InvokeAsync("What is your name?");

        // Assert: agent should have received 5 messages (4 injected + 1 prompt)
        Assert.NotNull(agent.LastReceivedMessages);
        Assert.Equal(5, agent.LastReceivedMessages.Count);
        Assert.Equal(ChatRole.User, agent.LastReceivedMessages[0].Role);
        Assert.Equal("Hello", agent.LastReceivedMessages[0].Text);
        Assert.Equal(ChatRole.Assistant, agent.LastReceivedMessages[1].Role);
        Assert.Equal("Hi there!", agent.LastReceivedMessages[1].Text);
        Assert.Equal(ChatRole.User, agent.LastReceivedMessages[2].Role);
        Assert.Equal("How are you?", agent.LastReceivedMessages[2].Text);
        Assert.Equal(ChatRole.Assistant, agent.LastReceivedMessages[3].Role);
        Assert.Equal("I'm doing well.", agent.LastReceivedMessages[3].Text);
        Assert.Equal(ChatRole.User, agent.LastReceivedMessages[4].Role);
        Assert.Equal("What is your name?", agent.LastReceivedMessages[4].Text);
    }

    [Fact]
    public async Task InjectConversationHistory_ClearedAfterFirstInvocation()
    {
        // Arrange
        var agent = new HistoryCapturingAgent("Test", "response");
        var adapter = new MAFAgentAdapter(agent);

        adapter.InjectConversationHistory(new[]
        {
            ("Past message", "Past response")
        });

        // Act: first call includes injected history
        await adapter.InvokeAsync("First");
        Assert.Equal(3, agent.LastReceivedMessages!.Count);

        // Act: second call should NOT include injected history (cleared after first use)
        await adapter.InvokeAsync("Second");
        Assert.Single(agent.LastReceivedMessages!);
        Assert.Equal("Second", agent.LastReceivedMessages[0].Text);
    }

    [Fact]
    public async Task InjectConversationHistory_WithNoHistory_OnlyPromptSent()
    {
        // Arrange
        var agent = new HistoryCapturingAgent("Test", "response");
        var adapter = new MAFAgentAdapter(agent);

        // Act: invoke without injecting any history
        await adapter.InvokeAsync("Just a prompt");

        // Assert: only the prompt was sent
        Assert.NotNull(agent.LastReceivedMessages);
        Assert.Single(agent.LastReceivedMessages);
        Assert.Equal("Just a prompt", agent.LastReceivedMessages[0].Text);
    }

    [Fact]
    public async Task ResetSessionAsync_ClearsInjectedHistory()
    {
        // Arrange
        var agent = new HistoryCapturingAgent("Test", "response");
        var adapter = new MAFAgentAdapter(agent);

        adapter.InjectConversationHistory(new[]
        {
            ("old message", "old response")
        });

        // Act: reset session should clear injected history
        await adapter.ResetSessionAsync();
        await adapter.InvokeAsync("Fresh prompt");

        // Assert: only the fresh prompt was sent
        Assert.NotNull(agent.LastReceivedMessages);
        Assert.Single(agent.LastReceivedMessages);
        Assert.Equal("Fresh prompt", agent.LastReceivedMessages[0].Text);
    }
}

#region Test Helpers

/// <summary>
/// A fake AIAgent that captures the messages it receives for assertion.
/// </summary>
internal class HistoryCapturingAgent : AIAgent
{
    private readonly string _responseText;

    public override string? Name { get; }
    public List<ChatMessage>? LastReceivedMessages { get; private set; }

    public HistoryCapturingAgent(string name, string responseText)
    {
        Name = name;
        _responseText = responseText;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default)
        => new(new SimpleAgentSession());

    protected override Task<MAFAgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastReceivedMessages = messages.ToList();

        var responseMessage = new ChatMessage(ChatRole.Assistant, _responseText);
        return Task.FromResult(new MAFAgentResponse(responseMessage));
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Not needed for these tests.");
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => new(new SimpleAgentSession());
}

internal class SimpleAgentSession : AgentSession
{
}

#endregion
