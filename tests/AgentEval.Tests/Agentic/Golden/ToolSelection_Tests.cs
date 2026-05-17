// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_selection</c> evaluator.
/// Key: tool_selection | Category: agentic-process | Threshold: 0.70
/// </summary>
public class ToolSelectionEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_selection", new FixedScoreEvaluator(100));

        Assert.Equal("tool_selection", eval.Key);
        Assert.Equal("Tool Selection", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_selection", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "Search for flights", Response: "I called search_flights.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_selection: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_selection", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "Search for flights", Response: "I called send_email instead.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_selection: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
