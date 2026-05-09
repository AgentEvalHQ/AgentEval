// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>fluency</c> evaluator.
/// Key: fluency | Category: rag | Threshold: 0.70
/// </summary>
public class Fluency_Tests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("fluency", new FixedScoreEvaluator(100));

        Assert.Equal("fluency", eval.Key);
        Assert.Equal("Fluency", eval.Name);
        Assert.Equal("rag", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("fluency", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Describe the benefits of cloud computing.",
            Response: "Cloud computing provides scalability and reduces capital expenditure by replacing on-premise hardware with pay-as-you-go services.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"fluency: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("fluency", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "Explain containerization.",
            Response: "Container it make app run same place everywhere. Is good because no problem different computer have.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"fluency: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
