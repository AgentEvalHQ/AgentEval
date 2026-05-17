// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Memory;

/// <summary>
/// Per-evaluator tests for <c>memory_recall_accuracy</c>.
/// Key: memory_recall_accuracy | Category: memory | CostTier: HIGH
/// </summary>
public class MemoryRecallAccuracyEvalTests
{
    private static IReadOnlyList<ConversationTurn> OneHistory() =>
        new List<ConversationTurn>
        {
            new("user", "My name is Alice and I live in Berlin."),
            new("assistant", "Got it, Alice from Berlin."),
        };

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("memory_recall_accuracy", new FixedScoreEvaluator(100));

        Assert.Equal("memory_recall_accuracy", eval.Key);
        Assert.Equal("Memory Recall Accuracy", eval.Name);
        Assert.Equal("memory", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("memory_recall_accuracy", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What is my name?",
            Response: "Your name is Alice.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)OneHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"memory_recall_accuracy: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("memory_recall_accuracy", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What is my name?",
            Response: "I don't recall you mentioning your name.",
            Metadata: new Dictionary<string, object>
            {
                ["conversation_history"] = (IReadOnlyList<ConversationTurn>)OneHistory(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"memory_recall_accuracy: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_MissingRequiredInput_ReturnsSkipped()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("memory_recall_accuracy", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "What is my name?", Response: "Alice");
        // No conversation_history metadata

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }
}
