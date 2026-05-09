// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>task_adherence</c> evaluator.
/// Key: task_adherence | Category: system-outcome | Threshold: 0.70
/// Composite with 5 equal-weight sub-dimensions.
/// </summary>
public class TaskAdherenceEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_adherence", new FixedScoreEvaluator(100));

        Assert.Equal("task_adherence", eval.Key);
        Assert.Equal("Task Adherence", eval.Name);
        Assert.Equal("system-outcome", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_adherence", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "test", Response: "compliant response");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"task_adherence: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("task_adherence", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "test", Response: "non-compliant response");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"task_adherence: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
