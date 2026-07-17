// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// VERIFIED AGAINST MOCK — proves MockCopilotStudioConversationClient itself behaves realistically
// (multi-turn state, activity filtering, error injection) and that CopilotStudioChatClient composes with
// it correctly end-to-end. This is NOT a live Copilot Studio verification — no real Entra app or MCS
// agent is available in this environment; see the mock's own honesty-boundary doc comment.
using AgentEval.MAF.CopilotStudio;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.CopilotStudio;

public class MockCopilotStudioConversationClientTests
{
    [Fact]
    public async Task MultiTurnConversation_TracksServerAssignedConversationId_AcrossTurns()
    {
        var mock = new MockCopilotStudioConversationClient(conversationId: "conv-abc")
            .WithReply("First reply")
            .WithReply("Second reply")
            .WithReply("Third reply");

        using var client = new CopilotStudioChatClient(mock);

        var r1 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "turn 1")]);
        var r2 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "turn 2")]);
        var r3 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "turn 3")]);

        Assert.Equal("First reply", r1.Text);
        Assert.Equal("Second reply", r2.Text);
        Assert.Equal("Third reply", r3.Text);
        Assert.Equal(1, mock.StartCallCount);   // conversation started once, reused for all 3 turns
        Assert.Equal(3, mock.AskCallCount);
        Assert.Equal(["turn 1", "turn 2", "turn 3"], mock.QuestionsAsked);
        // The conversation id is assigned during StartConversationAsync (before the FIRST ask), so every
        // ask — including the first — already carries it: proves session state is real, not stubbed.
        Assert.Equal("conv-abc", mock.ConversationIdsPassedToAsk[0]);
        Assert.Equal("conv-abc", mock.ConversationIdsPassedToAsk[1]);
        Assert.Equal("conv-abc", mock.ConversationIdsPassedToAsk[2]);
    }

    [Fact]
    public async Task NonMessageActivities_AreFilteredOut_NeverSurfacedAsText()
    {
        var mock = new MockCopilotStudioConversationClient().WithReplyAndTypingIndicator("real reply");
        using var client = new CopilotStudioChatClient(mock);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("real reply", response.Text);
        Assert.DoesNotContain("MUST NEVER SURFACE", response.Text);
    }

    [Fact]
    public async Task ThrowOnStart_SimulatesAuthFailure_PropagatesToCaller()
    {
        var mock = new MockCopilotStudioConversationClient { ThrowOnStart = new HttpRequestException("401 Unauthorized") };
        using var client = new CopilotStudioChatClient(mock);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
    }

    [Fact]
    public async Task ThrowOnAskAtCallIndex_SimulatesRateLimitMidConversation()
    {
        var mock = new MockCopilotStudioConversationClient()
            .WithReply("ok turn 1");
        mock.ThrowOnAskAtCallIndex = (1, new HttpRequestException("429 Too Many Requests"));

        using var client = new CopilotStudioChatClient(mock);

        var r1 = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "turn 1")]);
        Assert.Equal("ok turn 1", r1.Text);

        // Turn 2 hits the injected rate-limit — a caller's retry/backoff logic (Stage 4 P6) would catch this.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "turn 2")]));
    }

    [Fact]
    public async Task HangUntilCancelled_HonorsCallerCancellation_DoesNotHangForever()
    {
        var mock = new MockCopilotStudioConversationClient { HangUntilCancelled = true };
        using var client = new CopilotStudioChatClient(mock);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token));
    }

    [Fact]
    public async Task WithTurn_CustomFactory_CanEchoTheQuestion()
    {
        var mock = new MockCopilotStudioConversationClient()
            .WithTurn((question, _) => [MockCopilotStudioConversationClient.Message($"you asked: {question}")]);
        using var client = new CopilotStudioChatClient(mock);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "what is 2+2?")]);
        Assert.Equal("you asked: what is 2+2?", response.Text);
    }

    [Fact]
    public async Task NoScriptedTurn_ReturnsHonestDefault_NotAFabricatedSuccess()
    {
        var mock = new MockCopilotStudioConversationClient();   // zero scripted turns
        using var client = new CopilotStudioChatClient(mock);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Contains("mock default reply", response.Text);   // never silently empty/blank — visibly a mock artifact
    }
}
