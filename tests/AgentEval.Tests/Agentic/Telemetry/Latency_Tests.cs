// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Telemetry;

/// <summary>
/// Golden tests for <c>latency</c> evaluator.
/// Key: latency | Category: operational | Threshold: 0.80
/// </summary>
public class LatencyEvalTests
{
    private static AgenticTelemetry MakeTelemetry(double p50, double p95, double p99) =>
        new(p50, p95, p99,
            TokensUsed: 1000, EstimatedCostUsd: 0.02,
            ErrorCount: 0, TotalCalls: 10, RetryCount: 0,
            ToolLatencyMs: new Dictionary<string, double>());

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("latency", new FixedScoreEvaluator(100));

        Assert.Equal("latency", eval.Key);
        Assert.Equal("Latency", eval.Name);
        Assert.Equal("operational", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_WithinBudget_ReportsPass()
    {
        // P99 = 5000 ms, threshold = 10000 ms → linear score = 1.0 → pass
        var eval = new LatencyEval(MakeTelemetry(350, 900, 5000));
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"latency: expected Passed==true with P99=5000ms, threshold=10000ms, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_OverBudget_ReportsFail()
    {
        // P99 = 25000 ms (>2× threshold of 10000 ms) → linear score = 0.0 → fail
        var eval = new LatencyEval(MakeTelemetry(5000, 10000, 25000));
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"latency: expected Passed==false with P99=25000ms, threshold=10000ms, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_NoTelemetryData_ReturnsSkipped()
    {
        // No telemetry in metadata and no constructor data → should return skipped.
        var eval = new LatencyEval();
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);  // LatencyEval skips when no telemetry data is provided.
    }
}
