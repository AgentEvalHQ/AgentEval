// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>relevance</c> evaluator.
/// Key: relevance | Category: rag | Threshold: 0.70
/// </summary>
public class Relevance_Tests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("relevance", new FixedScoreEvaluator(100));

        Assert.Equal("relevance", eval.Key);
        Assert.Equal("Relevance", eval.Name);
        Assert.Equal("rag", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("relevance", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "How do I reset my password?",
            Response: "To reset your password, click 'Forgot Password' and follow the link sent to your email.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"relevance: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("relevance", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What is the refund policy?",
            Response: "Our loyalty rewards program gives you points for every purchase.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"relevance: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
