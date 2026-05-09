// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Telemetry;

/// <summary>
/// Golden tests for <c>retry_rate</c> evaluator.
/// Key: retry_rate | Category: operational | Threshold: 0.90
/// </summary>
public class RetryRateEvalTests
{
    private static AgenticTelemetry MakeTelemetry(int retryCount, int totalCalls) =>
        new(LatencyMsP50: 300, LatencyMsP95: 800, LatencyMsP99: 2000,
            TokensUsed: 1000, EstimatedCostUsd: 0.02,
            ErrorCount: 0, TotalCalls: totalCalls, RetryCount: retryCount,
            ToolLatencyMs: new Dictionary<string, double>());

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("retry_rate", new FixedScoreEvaluator(100));

        Assert.Equal("retry_rate", eval.Key);
        Assert.Equal("Retry Rate", eval.Name);
        Assert.Equal("operational", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_NoRetries_ReportsPass()
    {
        // 0 retries / 20 calls → retry rate = 0% → score = 1.0 → pass
        var eval = new RetryRateEval(MakeTelemetry(0, 20));
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"retry_rate: expected Passed==true with 0 retries in 20 calls, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_HighRetryRate_ReportsFail()
    {
        // 15 retries / 20 calls → retry rate = 75% → score = 0.25 < threshold 0.90 → fail
        var eval = new RetryRateEval(MakeTelemetry(15, 20));
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"retry_rate: expected Passed==false with 15/20 retries, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_NoTelemetryData_ReturnsSkipped()
    {
        var eval = new RetryRateEval();
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);  // RetryRateEval skips when no telemetry data is provided.
    }
}
