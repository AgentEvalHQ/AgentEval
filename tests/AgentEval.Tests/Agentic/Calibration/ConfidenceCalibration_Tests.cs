// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Calibration;

/// <summary>
/// Per-evaluator tests for <c>confidence_calibration</c>.
/// Key: confidence_calibration | Category: calibration | CostTier: LOW
/// </summary>
public class ConfidenceCalibrationEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("confidence_calibration", new FixedScoreEvaluator(100));

        Assert.Equal("confidence_calibration", eval.Key);
        Assert.Equal("Confidence Calibration", eval.Name);
        Assert.Equal("calibration", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("confidence_calibration", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What is the boiling point of water at sea level?",
            Response: "I am certain that the boiling point of water at sea level is 100°C (212°F).",
            GroundTruth: "100°C (212°F) at sea level.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"confidence_calibration: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("confidence_calibration", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What were Apple's exact revenues in Q3 2019?",
            Response: "I am completely certain that Apple's Q3 2019 revenue was exactly $53.8B.",
            GroundTruth: "Apple's Q3 2019 revenue was $53.8B.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"confidence_calibration: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
