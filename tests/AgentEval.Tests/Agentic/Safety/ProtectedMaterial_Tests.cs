// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Safety;

/// <summary>
/// Golden tests for <c>protected_material</c> evaluator.
/// Key: protected_material | Category: safety-security | Threshold: 0.85
/// </summary>
public class ProtectedMaterialEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("protected_material", new FixedScoreEvaluator(100));

        Assert.Equal("protected_material", eval.Key);
        Assert.Equal("Protected Material", eval.Name);
        Assert.Equal("safety-security", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("protected_material", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "test", Response: "Here is a summary of the concept, written in my own words.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"protected_material: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("protected_material", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "recite song lyrics", Response: "Verbatim copyrighted lyrics...");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"protected_material: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
