// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>task_navigation_efficiency</c> evaluator.
/// Key: task_navigation_efficiency | Category: system-outcome | Threshold: 0.70
/// Composite: deterministic path_edit_distance (0.50) + LLM path_quality (0.50).
/// AgentEval-original; no direct Foundry equivalent.
/// </summary>
public class TaskNavigationEfficiencyEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_navigation_efficiency", new FixedScoreEvaluator(100));

        Assert.Equal("task_navigation_efficiency", eval.Key);
        Assert.Equal("Task Navigation Efficiency", eval.Name);
        Assert.Equal("system-outcome", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_navigation_efficiency", new FixedScoreEvaluator(100));

        // Supply both actual and expected actions so the deterministic component scores well.
        var input = new EvalInput(
            Query: "Search and summarize",
            Response: "Done.",
            ToolCalls: new[]
            {
                new ToolCall("search", null, "{\"results\":[]}"),
                new ToolCall("summarize", null, "{\"summary\":\"ok\"}"),
            },
            ExpectedActions: new[]
            {
                new ExpectedAction("search", null),
                new ExpectedAction("summarize", null),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"task_navigation_efficiency: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_navigation_efficiency", new FixedScoreEvaluator(10));

        // Mismatched actual vs expected action sequence → deterministic component scores 0.
        var input = new EvalInput(
            Query: "Do the thing",
            Response: "Done",
            ToolCalls: new[]
            {
                new ToolCall("wrong_tool_a", null, null),
                new ToolCall("wrong_tool_b", null, null),
                new ToolCall("wrong_tool_c", null, null),
            },
            ExpectedActions: new[]
            {
                new ExpectedAction("correct_tool", null),
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"task_navigation_efficiency: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
