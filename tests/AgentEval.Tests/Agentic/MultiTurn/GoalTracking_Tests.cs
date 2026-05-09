// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.MultiTurn;

/// <summary>
/// Per-evaluator tests for <c>goal_tracking</c>.
/// Key: goal_tracking | Category: multi-turn | CostTier: HIGH
/// </summary>
public class GoalTrackingEvalTests
{
    private static IReadOnlyList<ConversationTurn> GoalHistory() =>
        new List<ConversationTurn>
        {
            new("user", "I want to book a flight to Rome for next month."),
            new("assistant", "Sure! Let me help you find available flights to Rome."),
            new("user", "Actually, what's the weather like in Paris?"),
            new("assistant", "The weather in Paris this time of year is mild."),
        };

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("goal_tracking", new FixedScoreEvaluator(100));

        Assert.Equal("goal_tracking", eval.Key);
        Assert.Equal("Goal Tracking", eval.Name);
        Assert.Equal("multi-turn", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("goal_tracking", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "OK, back to the Rome flight — what are the cheapest options?",
            Response: "Returning to your original goal of booking a Rome flight, here are the cheapest options I found.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)GoalHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"goal_tracking: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("goal_tracking", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "OK, back to the Rome flight — what are the cheapest options?",
            Response: "I only have information about Paris weather.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)GoalHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"goal_tracking: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_MissingRequiredInput_ReturnsSkipped()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("goal_tracking", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "What was my goal?", Response: "I don't know.");
        // No conversation_history metadata

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }
}
