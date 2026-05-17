// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Reasoning;

/// <summary>
/// Per-evaluator tests for <c>goal_decomposition_quality</c>.
/// Key: goal_decomposition_quality | Category: reasoning | CostTier: MEDIUM
/// </summary>
public class GoalDecompositionQualityEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("goal_decomposition_quality", new FixedScoreEvaluator(100));

        Assert.Equal("goal_decomposition_quality", eval.Key);
        Assert.Equal("Goal Decomposition Quality", eval.Name);
        Assert.Equal("reasoning", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("goal_decomposition_quality", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Build a recommendation engine for our e-commerce platform.",
            Response: "To build a recommendation engine, I'll decompose this into sub-goals: " +
                      "(1) data collection and preprocessing, (2) collaborative filtering model, " +
                      "(3) API layer, (4) A/B testing framework.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"goal_decomposition_quality: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("goal_decomposition_quality", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "Build a recommendation engine.",
            Response: "Just use machine learning.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"goal_decomposition_quality: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
