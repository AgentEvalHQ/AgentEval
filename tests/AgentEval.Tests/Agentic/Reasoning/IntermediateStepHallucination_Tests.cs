// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Reasoning;

/// <summary>
/// Per-evaluator tests for <c>intermediate_step_hallucination</c>.
/// Key: intermediate_step_hallucination | Category: reasoning | CostTier: MEDIUM
/// </summary>
public class IntermediateStepHallucinationEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intermediate_step_hallucination", new FixedScoreEvaluator(100));

        Assert.Equal("intermediate_step_hallucination", eval.Key);
        Assert.Equal("Intermediate Step Hallucination", eval.Name);
        Assert.Equal("reasoning", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intermediate_step_hallucination", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "What is the current price of AAPL?",
            Response: "Based on the stock_price tool result, AAPL is currently trading at $189.50.",
            ToolCalls: new[]
            {
                new ToolCall("stock_price", null, "{\"ticker\":\"AAPL\",\"price\":189.50}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"intermediate_step_hallucination: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intermediate_step_hallucination", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "What is the current price of AAPL?",
            Response: "Based on the database query, AAPL is at $210.00 and MSFT is at $380.00.",
            ToolCalls: new[]
            {
                new ToolCall("stock_price", null, "{\"ticker\":\"AAPL\",\"price\":189.50}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"intermediate_step_hallucination: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_MissingResponse_ReturnsSkipped()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intermediate_step_hallucination", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "What is the price?", Response: "");
        // Empty response — nothing to check for hallucinations

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }
}
