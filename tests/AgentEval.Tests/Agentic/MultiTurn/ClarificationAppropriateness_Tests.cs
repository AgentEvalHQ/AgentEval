// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.MultiTurn;

/// <summary>
/// Per-evaluator tests for <c>clarification_appropriateness</c>.
/// Key: clarification_appropriateness | Category: multi-turn | CostTier: LOW
/// </summary>
public class ClarificationAppropriatenessEvalTests
{
    private static IReadOnlyList<ConversationTurn> OneHistory() =>
        new List<ConversationTurn>
        {
            new("user", "Do it."),
        };

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("clarification_appropriateness", new FixedScoreEvaluator(100));

        Assert.Equal("clarification_appropriateness", eval.Key);
        Assert.Equal("Clarification Appropriateness", eval.Name);
        Assert.Equal("multi-turn", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("clarification_appropriateness", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Do it.",
            Response: "I'd be happy to help! Could you clarify what task you'd like me to do?",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)OneHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"clarification_appropriateness: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("clarification_appropriateness", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "Do it.",
            Response: "Done.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)OneHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"clarification_appropriateness: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_WithoutHistory_StillEvaluates()
    {
        // ClarificationAppropriatenessEval treats conversation_history as optional — no skip.
        var eval = AgenticEvaluatorFixture.BuildEvaluator("clarification_appropriateness", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Do it.",
            Response: "Could you clarify what task you'd like me to perform?");
        // No conversation_history metadata — should still evaluate (not skipped).

        var result = await eval.EvaluateAsync(input);

        // Should not be skipped — should evaluate normally.
        Assert.NotEqual("skipped", result.Score.Label);
    }
}
