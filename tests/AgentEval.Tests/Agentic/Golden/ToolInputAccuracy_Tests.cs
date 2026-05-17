// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_input_accuracy</c> evaluator.
/// Key: tool_input_accuracy | Category: agentic-process | Threshold: 0.70
/// Composite: deterministic schema validation (0.50) + LLM semantic groundedness (0.50).
/// </summary>
public class ToolInputAccuracyEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_input_accuracy", new FixedScoreEvaluator(100));

        Assert.Equal("tool_input_accuracy", eval.Key);
        Assert.Equal("Tool Input Accuracy", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_input_accuracy", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Find flights from NYC to London",
            Response: "Called search_flights with correct arguments.",
            ToolCalls: new[]
            {
                new ToolCall("search_flights",
                    new Dictionary<string, object> { ["origin"] = "NYC", ["destination"] = "London" },
                    null),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_input_accuracy: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_input_accuracy", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "Find flights from NYC to London",
            Response: "Called with hallucinated arguments.",
            ToolCalls: new[]
            {
                new ToolCall("search_flights",
                    new Dictionary<string, object> { ["origin"] = "FAKE_CITY" },
                    null),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_input_accuracy: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
