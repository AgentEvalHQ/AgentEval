// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>tool_efficiency</c> evaluator.
/// Key: tool_efficiency | Category: agentic-process | Threshold: 0.80
/// AgentEval-original; no direct Foundry equivalent.
/// </summary>
public class ToolEfficiencyEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_efficiency", new FixedScoreEvaluator(100));

        Assert.Equal("tool_efficiency", eval.Key);
        Assert.Equal("Tool Efficiency", eval.Name);
        Assert.Equal("agentic-process", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_efficiency", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "Do it once", Response: "Called tool once, result used.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_efficiency: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_efficiency", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "Do it once", Response: "Called tool 10 times redundantly.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_efficiency: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
