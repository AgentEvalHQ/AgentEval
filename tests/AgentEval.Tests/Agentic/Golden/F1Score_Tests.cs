// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Golden;

/// <summary>
/// Golden tests for <c>f1_score</c> evaluator.
/// Key: f1_score | Category: rag | Threshold: 0.50 (default)
/// <para>
/// <see cref="F1ScoreEval"/> is a pure-code deterministic evaluator with zero LLM dependency.
/// Tests use <see cref="EvalInput.GroundTruth"/> to provide the reference for F1 computation.
/// </para>
/// </summary>
public class F1Score_Tests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        // F1ScoreEval does not require a judge — no judge parameter needed.
        var eval = AgenticEvaluatorFixture.BuildEvaluator("f1_score", new FixedScoreEvaluator(100));

        Assert.Equal("f1_score", eval.Key);
        Assert.Equal("F1 Score", eval.Name);
        Assert.Equal("rag", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_IdenticalStrings_ReportsPass()
    {
        // Identical response and ground truth → F1 = 1.0 → pass (threshold 0.50).
        var eval = AgenticEvaluatorFixture.BuildEvaluator("f1_score", new FixedScoreEvaluator(100));
        var text = "machine learning is a field of artificial intelligence";
        var input = new EvalInput(
            Query: "What is machine learning?",
            Response: text,
            GroundTruth: text);

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"f1_score: identical strings should produce F1=1.0 and pass, got score={result.Score.Value}");
        Assert.InRange(result.Score.Value, 0.99, 1.01);
    }

    [Fact]
    public async Task EvaluateAsync_CompletelyDifferentStrings_ReportsFail()
    {
        // Completely different tokens → F1 = 0.0 → fail (threshold 0.50).
        var eval = AgenticEvaluatorFixture.BuildEvaluator("f1_score", new FixedScoreEvaluator(0));
        var input = new EvalInput(
            Query: "Describe photosynthesis.",
            Response: "Alpha bravo charlie delta echo foxtrot",
            GroundTruth: "photosynthesis converts sunlight carbon dioxide water into glucose oxygen plants");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"f1_score: completely different tokens should produce F1=0.0 and fail, got score={result.Score.Value}");
        Assert.InRange(result.Score.Value, 0.0, 0.01);
    }
}
