// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_call_accuracy</c> aggregate evaluator.
/// Key: tool_call_accuracy | Category: agentic-process | Threshold: 0.70
/// Composite of 5 process sub-evaluators with configured weights.
/// </summary>
public class ToolCallAccuracyAggregateEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_call_accuracy", new FixedScoreEvaluator(100));

        Assert.Equal("tool_call_accuracy", eval.Key);
        Assert.Equal("Tool Call Accuracy", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_call_accuracy", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Use tools correctly",
            Response: "All tools called correctly.",
            ToolCalls: new[]
            {
                new ToolCall("search", null, "{\"status\":\"success\",\"results\":[]}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_call_accuracy: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_call_accuracy", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "Use tools correctly", Response: "Everything failed.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_call_accuracy: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
