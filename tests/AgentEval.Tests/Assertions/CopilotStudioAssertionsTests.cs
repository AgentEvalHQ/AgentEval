// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// Unit tests for <see cref="CopilotStudioAssertions"/> and <see cref="ChatResponseAssertions"/>. Exercised
/// entirely against hand-built plain <see cref="ChatResponse"/> objects — no Copilot Studio package
/// dependency, matching the assertions' own design (they inspect plain MEAI types, never
/// <c>CopilotStudioChatClient</c>). For proof against the real Copilot Studio bridge, see
/// <c>CopilotStudioChatClientTests</c>.
/// </summary>
public class CopilotStudioAssertionsTests
{
    private static ChatResponse Response(string text, string? conversationId = null) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { ConversationId = conversationId };

    [Fact]
    public void HaveRespondedWithNonEmptyMessage_NonEmptyText_Passes()
        => "hello".Should().HaveRespondedWithNonEmptyMessage();

    [Fact]
    public void HaveRespondedWithNonEmptyMessage_EmptyText_ThrowsWithFidelityCeilingExplanation()
    {
        var ex = Assert.Throws<ResponseAssertionException>(() => "".Should().HaveRespondedWithNonEmptyMessage());
        Assert.Contains("adaptive card", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fidelity ceiling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HaveRespondedWithNonEmptyMessage_WhitespaceOnly_Throws()
        => Assert.Throws<ResponseAssertionException>(() => "   ".Should().HaveRespondedWithNonEmptyMessage());

    [Fact]
    public void HaveContinuedConversation_MatchingId_Passes()
        => Response("reply", "conv-1").Should().HaveContinuedConversation("conv-1");

    [Fact]
    public void HaveContinuedConversation_DifferentId_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(
            () => Response("reply", "conv-2").Should().HaveContinuedConversation("conv-1"));
        Assert.Contains("conv-1", ex.Message);
        Assert.Contains("conv-2", ex.Message);
    }

    [Fact]
    public void HaveContinuedConversation_NullConversationId_Throws()
        => Assert.Throws<ResponseAssertionException>(
            () => Response("reply", conversationId: null).Should().HaveContinuedConversation("conv-1"));

    [Fact]
    public void HaveStartedNewConversation_NonEmptyId_Passes()
        => Response("reply", "conv-1").Should().HaveStartedNewConversation();

    [Fact]
    public void HaveStartedNewConversation_NullId_Throws()
        => Assert.Throws<ResponseAssertionException>(
            () => Response("reply", conversationId: null).Should().HaveStartedNewConversation());

    [Fact]
    public void HaveStartedDifferentConversation_DifferentId_Passes()
        => Response("reply", "conv-2").Should().HaveStartedDifferentConversation("conv-1");

    [Fact]
    public void HaveStartedDifferentConversation_SameId_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(
            () => Response("reply", "conv-1").Should().HaveStartedDifferentConversation("conv-1"));
        Assert.Contains("same as previous", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HaveStartedDifferentConversation_NullId_Throws()
        => Assert.Throws<ResponseAssertionException>(
            () => Response("reply", conversationId: null).Should().HaveStartedDifferentConversation("conv-1"));

    [Fact]
    public void HaveStartedNewConversation_SingleArgCall_NeverAmbiguouslyBindsToPreviousId()
    {
        // Regression guard for the overload-ambiguity bug found while writing these tests: a call with exactly
        // one positional string argument must resolve to the (because) overload, not silently be mistaken for
        // a "differs from previous id" check — HaveStartedDifferentConversation exists precisely so a single
        // positional string can never mean two different things across two same-named overloads.
        Response("reply", "conv-1").Should().HaveStartedNewConversation("any string is just a because reason here");
    }

    [Fact]
    public void HaveStayedWithinCreditBudget_UnderCap_Passes()
        => Response("reply", "conv-1").Should().HaveStayedWithinCreditBudget(estimatedCreditsUsed: 3, maxCredits: 5);

    [Fact]
    public void HaveStayedWithinCreditBudget_AtCap_Passes()
        => Response("reply", "conv-1").Should().HaveStayedWithinCreditBudget(estimatedCreditsUsed: 5, maxCredits: 5);

    [Fact]
    public void HaveStayedWithinCreditBudget_OverCap_Throws()
    {
        var ex = Assert.Throws<ResponseAssertionException>(
            () => Response("reply", "conv-1").Should().HaveStayedWithinCreditBudget(estimatedCreditsUsed: 6, maxCredits: 5));
        Assert.Contains("6", ex.Message);
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void ChatResponseShould_ChainsAcrossMultipleAssertions()
        => Response("reply", "conv-1").Should()
            .HaveContinuedConversation("conv-1")
            .HaveStartedNewConversation()
            .HaveStayedWithinCreditBudget(1, 5);

    [Fact]
    public void ChatResponseAssertions_NullResponse_Throws()
        => Assert.Throws<ArgumentNullException>(() => new ChatResponseAssertions(null!));
}
