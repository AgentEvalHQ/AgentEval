// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.UX;

/// <summary>
/// Per-evaluator tests for <c>verbosity_appropriateness</c>.
/// Key: verbosity_appropriateness | Category: ux | CostTier: LOW
/// </summary>
public class VerbosityAppropriatenessEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("verbosity_appropriateness", new FixedScoreEvaluator(100));

        Assert.Equal("verbosity_appropriateness", eval.Key);
        Assert.Equal("Verbosity Appropriateness", eval.Name);
        Assert.Equal("ux", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("verbosity_appropriateness", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What is 2 + 2?",
            Response: "4.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"verbosity_appropriateness: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("verbosity_appropriateness", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What is 2 + 2?",
            Response: new string('x', 5000)); // Extremely long response to a trivial question

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"verbosity_appropriateness: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
