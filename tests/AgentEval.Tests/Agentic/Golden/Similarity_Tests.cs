// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>similarity</c> evaluator.
/// Key: similarity | Category: rag | Threshold: 0.70
/// Note: the <c>similarity</c> evaluator requires <see cref="EvalInput.GroundTruth"/> to be set.
/// </summary>
public class Similarity_Tests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("similarity", new FixedScoreEvaluator(100));

        Assert.Equal("similarity", eval.Key);
        Assert.Equal("Similarity", eval.Name);
        Assert.Equal("rag", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("similarity", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What is the capital of France?",
            Response: "The capital city of France is Paris, which is also its largest city.",
            GroundTruth: "Paris is the capital of France.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"similarity: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("similarity", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "How many planets are in the solar system?",
            Response: "There are approximately 12 to 15 planets if you include dwarf planets.",
            GroundTruth: "There are 8 planets in the solar system according to the IAU definition.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"similarity: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
