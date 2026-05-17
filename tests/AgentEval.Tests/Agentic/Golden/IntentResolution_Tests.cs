// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>intent_resolution</c> evaluator.
/// Key: intent_resolution | Category: system-outcome | Threshold: 0.70
/// Composite with 2 equal-weight sub-dimensions: intent_identified and intent_resolved.
/// </summary>
public class IntentResolutionEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intent_resolution", new FixedScoreEvaluator(100));

        Assert.Equal("intent_resolution", eval.Key);
        Assert.Equal("Intent Resolution", eval.Name);
        Assert.Equal("system-outcome", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intent_resolution", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "Find me a recipe for pasta", Response: "Here is a classic pasta carbonara recipe.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"intent_resolution: expected Passed==true with stub score=100, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intent_resolution", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "Find me a recipe for pasta", Response: "I cannot help with that.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"intent_resolution: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
