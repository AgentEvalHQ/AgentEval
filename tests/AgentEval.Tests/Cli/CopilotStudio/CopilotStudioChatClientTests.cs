// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.CopilotStudio;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// Unit tests for <c>CopilotStudioChatClient</c> — the shim bridging Copilot Studio's streaming Bot Framework
/// activity API into <see cref="IChatClient"/>. Exercised entirely against <see cref="FakeCopilotStudioConversationClient"/>
/// (a hand-rolled <c>ICopilotStudioConversationClient</c>, not the real SDK), so every test here is credential-free
/// and network-free — it proves the bridging LOGIC is correct for the documented activity contract, not that a
/// real Copilot Studio agent's wire traffic matches these fakes exactly (see the class's own XML doc remarks).
/// </summary>
public class CopilotStudioChatClientTests
{
    [Fact]
    public async Task FirstTurn_StartsConversation_ThenAsks_AndSurfacesMessageText()
    {
        var fake = new FakeCopilotStudioConversationClient();
        fake.OnStart = (_, _) => Empty(); // no greeting emitted (emitStartConversationEvent: false)
        fake.OnAsk = (_, _, _) => One(Message("Hello from Copilot Studio", "conv-1"));

        using var client = new CopilotStudioChatClient(fake);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("Hello from Copilot Studio", response.Text);
        Assert.Equal(1, fake.StartCallCount);
        Assert.Equal(1, fake.AskCallCount);
        Assert.Equal("hi", fake.LastQuestion);
    }

    [Fact]
    public async Task FirstTurn_CallsStartConversationAsync_WithEmitStartConversationEventFalse()
    {
        var fake = new FakeCopilotStudioConversationClient();
        fake.OnStart = (_, _) => Empty();
        fake.OnAsk = (_, _, _) => One(Message("ok", "conv-1"));

        using var client = new CopilotStudioChatClient(fake);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.False(fake.LastEmitStartConversationEvent);
    }

    [Fact]
    public async Task SecondTurn_SkipsStartConversation_AndPassesTrackedConversationId()
    {
        var fake = new FakeCopilotStudioConversationClient();
        fake.OnStart = (_, _) => One(Message(text: null, conversationId: "conv-1")); // start echoes the new conv id, no text
        fake.OnAsk = (_, _, _) => One(Message("turn reply", "conv-1"));

        using var client = new CopilotStudioChatClient(fake);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "first")]);
        Assert.Equal(1, fake.StartCallCount);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "second")]);

        Assert.Equal(1, fake.StartCallCount); // still 1 — not called again
        Assert.Equal(2, fake.AskCallCount);
        Assert.Equal("conv-1", fake.LastConversationIdPassedToAsk);
        Assert.Equal("second", fake.LastQuestion);
    }

    [Fact]
    public async Task NonMessageActivities_AreDropped_NotFabricatedAsText()
    {
        var fake = new FakeCopilotStudioConversationClient();
        fake.OnStart = (_, _) => Empty();
        fake.OnAsk = (_, _, _) => Many(
            NonMessage("typing", "conv-1"),
            Message("the real answer", "conv-1"),
            NonMessage("event", "conv-1"));

        using var client = new CopilotStudioChatClient(fake);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("the real answer", response.Text);
    }

    [Fact]
    public async Task MessageActivityWithNoText_ContributesNothing()
    {
        var fake = new FakeCopilotStudioConversationClient();
        fake.OnStart = (_, _) => Empty();
        fake.OnAsk = (_, _, _) => Many(Message(text: null, conversationId: "conv-1"), Message("", "conv-1"));

        using var client = new CopilotStudioChatClient(fake);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("", response.Text);
    }

    [Fact]
    public async Task NoUserMessage_Throws()
    {
        var fake = new FakeCopilotStudioConversationClient();
        using var client = new CopilotStudioChatClient(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.Assistant, "no user turn here")]));
    }

    [Fact]
    public async Task MultipleUserMessages_UsesTheLastOne()
    {
        var fake = new FakeCopilotStudioConversationClient();
        fake.OnStart = (_, _) => Empty();
        fake.OnAsk = (_, _, _) => One(Message("ok", "conv-1"));

        using var client = new CopilotStudioChatClient(fake);
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "first"), new ChatMessage(ChatRole.Assistant, "reply"), new ChatMessage(ChatRole.User, "second")]);

        Assert.Equal("second", fake.LastQuestion);
    }

    [Fact]
    public async Task ChatOptionsConversationId_OverridesTrackedState_AndSkipsStart()
    {
        var fake = new FakeCopilotStudioConversationClient();
        fake.OnAsk = (_, _, _) => One(Message("resumed", "conv-resumed"));

        using var client = new CopilotStudioChatClient(fake);
        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "resume please")],
            new ChatOptions { ConversationId = "conv-resumed" });

        Assert.Equal(0, fake.StartCallCount); // resuming an existing conversation never starts a new one
        Assert.Equal("conv-resumed", fake.LastConversationIdPassedToAsk);
        Assert.Equal("resumed", response.Text);
    }

    [Fact]
    public async Task StreamingResponse_YieldsOneUpdatePerMessageActivity()
    {
        var fake = new FakeCopilotStudioConversationClient();
        fake.OnStart = (_, _) => Empty();
        fake.OnAsk = (_, _, _) => Many(Message("part one", "conv-1"), Message("part two", "conv-1"));

        using var client = new CopilotStudioChatClient(fake);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")]))
        {
            updates.Add(update);
        }

        Assert.Equal(2, updates.Count);
        Assert.Equal("part one", updates[0].Text);
        Assert.Equal("part two", updates[1].Text);
        Assert.All(updates, u => Assert.Equal("conv-1", u.ConversationId));
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CopilotStudioChatClient(null!));
    }

    [Fact]
    public async Task DisposedClient_ThrowsOnNextCall()
    {
        var fake = new FakeCopilotStudioConversationClient();
        var client = new CopilotStudioChatClient(fake);
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
    }

    // ── activity builders ──

    private static Activity Message(string? text, string conversationId) => new()
    {
        Type = ActivityTypes.Message,
        Text = text,
        Conversation = new ConversationAccount { Id = conversationId },
    };

    private static Activity NonMessage(string type, string conversationId) => new()
    {
        Type = type,
        Text = "should never surface",
        Conversation = new ConversationAccount { Id = conversationId },
    };

    private static async IAsyncEnumerable<IActivity> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async IAsyncEnumerable<IActivity> One(IActivity activity)
    {
        await Task.CompletedTask;
        yield return activity;
    }

    private static async IAsyncEnumerable<IActivity> Many(params IActivity[] activities)
    {
        await Task.CompletedTask;
        foreach (var a in activities)
        {
            yield return a;
        }
    }

    /// <summary>
    /// Hand-rolled test double for <c>ICopilotStudioConversationClient</c> — the real Copilot Studio SDK does not
    /// publicly expose a mockable interface (see <c>ICopilotStudioConversationClient</c>'s own XML doc), so this
    /// fake stands in directly for AgentEval's own seam.
    /// </summary>
    private sealed class FakeCopilotStudioConversationClient : ICopilotStudioConversationClient
    {
        public Func<bool, CancellationToken, IAsyncEnumerable<IActivity>> OnStart { get; set; } = (_, _) => Empty();
        public Func<string, string?, CancellationToken, IAsyncEnumerable<IActivity>> OnAsk { get; set; } = (_, _, _) => Empty();

        public int StartCallCount { get; private set; }
        public int AskCallCount { get; private set; }
        public bool LastEmitStartConversationEvent { get; private set; }
        public string? LastQuestion { get; private set; }
        public string? LastConversationIdPassedToAsk { get; private set; }

        public IAsyncEnumerable<IActivity> StartConversationAsync(bool emitStartConversationEvent, CancellationToken cancellationToken)
        {
            StartCallCount++;
            LastEmitStartConversationEvent = emitStartConversationEvent;
            return OnStart(emitStartConversationEvent, cancellationToken);
        }

        public IAsyncEnumerable<IActivity> AskQuestionAsync(string question, string? conversationId, CancellationToken cancellationToken)
        {
            AskCallCount++;
            LastQuestion = question;
            LastConversationIdPassedToAsk = conversationId;
            return OnAsk(question, conversationId, cancellationToken);
        }
    }
}
