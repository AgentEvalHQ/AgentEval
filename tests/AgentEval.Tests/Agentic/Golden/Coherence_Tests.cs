// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>coherence</c> evaluator.
/// Key: coherence | Category: rag | Threshold: 0.70
/// </summary>
public class Coherence_Tests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("coherence", new FixedScoreEvaluator(100));

        Assert.Equal("coherence", eval.Key);
        Assert.Equal("Coherence", eval.Name);
        Assert.Equal("rag", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("coherence", new FixedScoreEvaluator(100));
        var input = new EvalInput(
            Query: "Summarize the quarterly results.",
            Response: "Revenue grew 15% to $4.2B. This was driven by enterprise adoption. Operating margin improved to 18%.");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"coherence: expected Passed==true with stub score=100, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("coherence", new FixedScoreEvaluator(10));
        var input = new EvalInput(
            Query: "Explain the deployment process.",
            Response: "First compile. The database was made in 2019. Blue whales. Then push.");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"coherence: expected Passed==false with stub score=10, got score={result.Score.Value}");
    }
}
