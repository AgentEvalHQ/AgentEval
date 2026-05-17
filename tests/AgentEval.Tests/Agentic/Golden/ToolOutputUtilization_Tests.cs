// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_output_utilization</c> evaluator.
/// Key: tool_output_utilization | Category: agentic-process | Threshold: 0.70
/// </summary>
public class ToolOutputUtilizationEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_output_utilization", new FixedScoreEvaluator(100));

        Assert.Equal("tool_output_utilization", eval.Key);
        Assert.Equal("Tool Output Utilization", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_output_utilization", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Summarize the search results",
            Response: "Based on the search results, here is the summary: ...",
            ToolCalls: new[]
            {
                new ToolCall("search", null, "{\"results\":[\"result1\",\"result2\"]}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_output_utilization: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_output_utilization", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "Summarize the search results",
            Response: "I cannot help with that.",
            ToolCalls: new[]
            {
                new ToolCall("search", null, "{\"results\":[\"result1\",\"result2\"]}"),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_output_utilization: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
