// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>intent_identification</c> evaluator.
/// Key: intent_identification | Category: system-outcome | Threshold: 0.70
/// AgentEval-original evaluator (no Foundry equivalent).
/// </summary>
public class IntentIdentificationEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intent_identification", new FixedScoreEvaluator(100));

        Assert.Equal("intent_identification", eval.Key);
        Assert.Equal("Intent Identification", eval.Name);
        Assert.Equal("system-outcome", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intent_identification", new FixedScoreEvaluator(100));
        var input = new EvalInput(Query: "Book a flight to Paris", Response: "I'll book a flight to Paris for you.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"intent_identification: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("intent_identification", new FixedScoreEvaluator(10));
        var input = new EvalInput(Query: "Book a flight to Paris", Response: "Here is a hotel in Paris.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"intent_identification: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
