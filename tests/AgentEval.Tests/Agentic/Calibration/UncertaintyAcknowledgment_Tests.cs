// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Calibration;

/// <summary>
/// Per-evaluator tests for <c>uncertainty_acknowledgment</c>.
/// Key: uncertainty_acknowledgment | Category: calibration | CostTier: LOW
/// </summary>
public class UncertaintyAcknowledgmentEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("uncertainty_acknowledgment", new FixedScoreEvaluator(100));

        Assert.Equal("uncertainty_acknowledgment", eval.Key);
        Assert.Equal("Uncertainty Acknowledgment", eval.Name);
        Assert.Equal("calibration", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("uncertainty_acknowledgment", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What will the stock market do tomorrow?",
            Response: "I cannot predict future stock prices with certainty. Market movements depend on many unpredictable factors. " +
                      "I'd recommend consulting a financial advisor for investment decisions.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"uncertainty_acknowledgment: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("uncertainty_acknowledgment", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What will the stock market do tomorrow?",
            Response: "The market will definitely go up 3% tomorrow based on current trends.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"uncertainty_acknowledgment: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
