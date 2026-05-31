// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using Xunit;

namespace AgentEval.Tests.Agentic.Conversation;

/// <summary>
/// Regression coverage for BUG-57: the conversational evaluators (memory_recall_accuracy,
/// long_conversation_coherence, turn_coherence, goal_tracking, clarification_appropriateness)
/// previously rebuilt EvalInput as <c>new EvalInput(Query, Response, Metadata)</c> when embedding
/// the transcript, silently dropping Context / GroundTruth / ToolCalls / ToolDefinitions /
/// ExpectedActions / SystemMessage. <see cref="ConversationHistoryHelper.WithEnrichedQuery"/>
/// now uses a record <c>with</c> expression so every other field survives.
/// </summary>
public class ConversationHistoryHelperTests
{
    private static EvalInput FullyPopulatedInput() => new(
        Query: "What is my name?",
        Response: "Your name is Alice.",
        Context: "context-block",
        GroundTruth: "Alice",
        ToolCalls: new[] { new ToolCall("lookup", null, "ok") },
        ToolDefinitions: new[] { new ToolDefinition("lookup", "desc", null) },
        ExpectedActions: new[] { new ExpectedAction("recall name", new[] { "lookup" }) },
        SystemMessage: "you are a helpful assistant",
        Metadata: new Dictionary<string, object> { ["k"] = "v" });

    [Fact]
    public void WithEnrichedQuery_PrependsTranscript_ToQuery()
    {
        var input = FullyPopulatedInput();

        var enriched = ConversationHistoryHelper.WithEnrichedQuery(input, "=== transcript ===");

        Assert.Equal("=== transcript ===\n\nCurrent user query: What is my name?", enriched.Query);
    }

    [Fact]
    public void WithEnrichedQuery_PreservesEveryOtherField()
    {
        var input = FullyPopulatedInput();

        var enriched = ConversationHistoryHelper.WithEnrichedQuery(input, "transcript");

        // Every field except Query must survive the enrichment (BUG-57).
        Assert.Equal(input.Response, enriched.Response);
        Assert.Equal(input.Context, enriched.Context);
        Assert.Equal(input.GroundTruth, enriched.GroundTruth);
        Assert.Same(input.ToolCalls, enriched.ToolCalls);
        Assert.Same(input.ToolDefinitions, enriched.ToolDefinitions);
        Assert.Same(input.ExpectedActions, enriched.ExpectedActions);
        Assert.Equal(input.SystemMessage, enriched.SystemMessage);
        Assert.Same(input.Metadata, enriched.Metadata);
    }

    [Fact]
    public void WithEnrichedQuery_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => ConversationHistoryHelper.WithEnrichedQuery(null!, "transcript"));
    }
}
