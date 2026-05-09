// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>task_completion</c> evaluator.
/// Key: task_completion | Category: system-outcome | Threshold: 0.70
/// </summary>
public class TaskCompletionEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_completion", new FixedScoreEvaluator(100));

        Assert.Equal("task_completion", eval.Key);
        Assert.Equal("Task Completion", eval.Name);
        Assert.Equal("system-outcome", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_completion", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "test", Response: "complete response");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"task_completion: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_completion", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "test", Response: "bad response");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"task_completion: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
