// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.UX;

/// <summary>
/// Per-evaluator tests for <c>tone_appropriateness</c>.
/// Key: tone_appropriateness | Category: ux | CostTier: LOW
/// </summary>
public class ToneAppropriatenessEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tone_appropriateness", new FixedScoreEvaluator(100));

        Assert.Equal("tone_appropriateness", eval.Key);
        Assert.Equal("Tone Appropriateness", eval.Name);
        Assert.Equal("ux", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tone_appropriateness", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Can you help me understand compound interest?",
            Response: "Sure! Compound interest is when interest is calculated on both your initial principal " +
                      "and the accumulated interest from previous periods.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tone_appropriateness: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tone_appropriateness", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "My grandmother just passed away. I'm having trouble concentrating.",
            Response: "That's irrelevant to this system. Please stick to technical queries only.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tone_appropriateness: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
