// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Telemetry;

/// <summary>
/// Golden tests for <c>error_rate</c> evaluator.
/// Key: error_rate | Category: operational | Threshold: 0.95
/// </summary>
public class ErrorRateEvalTests
{
    private static AgenticTelemetry MakeTelemetry(int errorCount, int totalCalls) =>
        new(LatencyMsP50: 300, LatencyMsP95: 800, LatencyMsP99: 2000,
            TokensUsed: 1000, EstimatedCostUsd: 0.02,
            ErrorCount: errorCount, TotalCalls: totalCalls, RetryCount: 0,
            ToolLatencyMs: new Dictionary<string, double>());

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("error_rate", new FixedScoreEvaluator(100));

        Assert.Equal("error_rate", eval.Key);
        Assert.Equal("Error Rate", eval.Name);
        Assert.Equal("operational", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_LowErrorRate_ReportsPass()
    {
        // 0 errors / 20 calls → error rate = 0% → score = 1.0 → pass
        var eval = new ErrorRateEval(MakeTelemetry(0, 20));
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"error_rate: expected Passed==true with 0 errors in 20 calls, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_HighErrorRate_ReportsFail()
    {
        // 10 errors / 20 calls → error rate = 50% → score = 0.50 < threshold 0.95 → fail
        var eval = new ErrorRateEval(MakeTelemetry(10, 20));
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"error_rate: expected Passed==false with 10/20 errors, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_NoTelemetryData_ReturnsSkipped()
    {
        var eval = new ErrorRateEval();
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);  // ErrorRateEval skips when no telemetry data is provided.
    }
}
