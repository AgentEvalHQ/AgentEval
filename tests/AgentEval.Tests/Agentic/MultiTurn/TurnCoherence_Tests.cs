// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.MultiTurn;

/// <summary>
/// Per-evaluator tests for <c>turn_coherence</c>.
/// Key: turn_coherence | Category: multi-turn | CostTier: MEDIUM
/// </summary>
public class TurnCoherenceEvalTests
{
    private static IReadOnlyList<ConversationTurn> OneHistory() =>
        new List<ConversationTurn>
        {
            new("user", "Can you help me plan a trip to Tokyo?"),
        };

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("turn_coherence", new FixedScoreEvaluator(100));

        Assert.Equal("turn_coherence", eval.Key);
        Assert.Equal("Turn Coherence", eval.Name);
        Assert.Equal("multi-turn", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("turn_coherence", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What are the best times to visit?",
            Response: "The best times to visit Tokyo are spring (March–May) and autumn (September–November).",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)OneHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"turn_coherence: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("turn_coherence", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What are the best times to visit?",
            Response: "The capital of France is Paris.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)OneHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"turn_coherence: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_MissingRequiredInput_ReturnsSkipped()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("turn_coherence", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "What time is it?", Response: "It is noon.");
        // No conversation_history metadata

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }
}
