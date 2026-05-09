// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.StochasticStability;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.StochasticStability;

/// <summary>
/// Golden tests for <c>stochastic_stability</c> evaluator.
/// Key: stochastic_stability | Category: operational | Threshold: 0.80
/// </summary>
public class StochasticStabilityEvalTests
{
    private static EvalResult MakeResult(bool passed, double score, string label = "pass") =>
        new(
            Metric: new("stub", "Stub", "test", "1.0.0"),
            Score: new(score, null, passed ? "pass" : "fail", passed, 0.70, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("stub", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalInput MakeInput(IEnumerable<EvalResult> runResults) =>
        new(Query: "stability-test",
            Metadata: new Dictionary<string, object>
            {
                [StochasticStabilityEval.MetadataRunResultsKey] = runResults
            });

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("stochastic_stability", new FixedScoreEvaluator(100));

        Assert.Equal("stochastic_stability", eval.Key);
        Assert.Equal("Stochastic Stability", eval.Name);
        Assert.Equal("operational", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_AllRunsPass_ReportsHighStability()
    {
        // 5 runs all passing with similar scores → high stability → should pass
        var eval = new StochasticStabilityEval(passThreshold: 0.80);
        var runResults = Enumerable.Range(0, 5)
            .Select(_ => MakeResult(true, 0.92));
        var input = MakeInput(runResults);

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"stochastic_stability: expected Passed==true for 5 consistent passing runs, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
        // Score should be high — all runs pass, variance near 0, failure mode consistency = 1.0
        Assert.True(result.Score.Value >= 0.90,
            $"Expected stability score >= 0.90 for all-passing runs, got {result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_OscillatingResults_ReportsLowStability()
    {
        // 5 runs alternating pass/fail → low success rate, high variance → should fail
        var eval = new StochasticStabilityEval(passThreshold: 0.80);
        var runResults = new[]
        {
            MakeResult(true,  0.95),
            MakeResult(false, 0.20),
            MakeResult(true,  0.90),
            MakeResult(false, 0.15),
            MakeResult(true,  0.88),
        };
        var input = MakeInput(runResults);

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"stochastic_stability: expected Passed==false for oscillating runs, got score={result.Score.Value}");
        // 3/5 passes → success rate = 0.60; high variance; composite should be low
        Assert.True(result.Score.Value < 0.80,
            $"Expected stability score < 0.80 for oscillating runs, got {result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_FewerThanTwoRuns_ReturnsSkipped()
    {
        // Only 1 run result provided → should return skipped
        var eval = new StochasticStabilityEval(passThreshold: 0.80);
        var input = MakeInput([MakeResult(true, 0.90)]);

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);  // StochasticStabilityEval requires at least 2 run results.
    }
}
