// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Calibration;

/// <summary>
/// Per-evaluator tests for <c>self_correction_quality</c>.
/// Key: self_correction_quality | Category: calibration | CostTier: MEDIUM
/// </summary>
public class SelfCorrectionQualityEvalTests
{
    private static ConversationTurn GoodCorrectionTurn() =>
        new("user", "Actually that's wrong — Paris is the capital of France, not Lyon.");

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("self_correction_quality", new FixedScoreEvaluator(100));

        Assert.Equal("self_correction_quality", eval.Key);
        Assert.Equal("Self-Correction Quality", eval.Name);
        Assert.Equal("calibration", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("self_correction_quality", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What is the capital of France?",
            Response: "You're absolutely right — I apologize for the error. Paris is the capital of France, not Lyon. " +
                      "Lyon is the third-largest city but not the capital.",
            Metadata: new Dictionary<string, object>
            {
                ["correction_turn"] = GoodCorrectionTurn(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"self_correction_quality: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("self_correction_quality", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What is the capital of France?",
            Response: "No, I'm quite confident that Lyon is the capital of France.",
            Metadata: new Dictionary<string, object>
            {
                ["correction_turn"] = GoodCorrectionTurn(),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"self_correction_quality: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_MissingRequiredInput_ReturnsSkipped()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("self_correction_quality", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What is the capital of France?",
            Response: "You are correct, Paris is the capital.");
        // No correction_turn metadata

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }
}
