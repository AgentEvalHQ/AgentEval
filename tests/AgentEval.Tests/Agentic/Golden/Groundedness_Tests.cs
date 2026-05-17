// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>groundedness</c> evaluator.
/// Key: groundedness | Category: rag | Threshold: 0.75
/// </summary>
public class Groundedness_Tests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("groundedness", new FixedScoreEvaluator(100));

        Assert.Equal("groundedness", eval.Key);
        Assert.Equal("Groundedness", eval.Name);
        Assert.Equal("rag", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("groundedness", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What was Q3 revenue?",
            Response: "Q3 revenue was $1.2B per the earnings report.",
            Context: "The earnings report shows Q3 revenue of $1.2B.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"groundedness: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("groundedness", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What did the report say?",
            Response: "The report said revenue was $99B and the CEO won a Nobel Prize.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"groundedness: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
