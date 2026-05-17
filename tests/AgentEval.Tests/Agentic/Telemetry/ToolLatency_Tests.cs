// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Telemetry;

/// <summary>
/// Golden tests for <c>tool_latency</c> evaluator.
/// Key: tool_latency | Category: operational | Budget: 2000ms per-tool
/// </summary>
public class ToolLatencyEvalTests
{
    private static AgenticTelemetry MakeTelemetry(Dictionary<string, double> toolLatencyMs) =>
        new(LatencyMsP50: 300, LatencyMsP95: 800, LatencyMsP99: 2000,
            TokensUsed: 1000, EstimatedCostUsd: 0.02,
            ErrorCount: 0, TotalCalls: 10, RetryCount: 0,
            ToolLatencyMs: toolLatencyMs);

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("tool_latency", new FixedScoreEvaluator(100));

        Assert.Equal("tool_latency", eval.Key);
        Assert.Equal("Tool Latency", eval.Name);
        Assert.Equal("operational", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_FastTools_ReportsPass()
    {
        // Max tool latency = 500ms <= 2000ms budget → score = 1.0 → pass
        var eval = new ToolLatencyEval(MakeTelemetry(new Dictionary<string, double>
        {
            ["search"] = 500,
            ["lookup"] = 300
        }), perToolBudgetMs: 2000);
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"tool_latency: expected Passed==true with max tool latency=500ms, budget=2000ms, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_SlowTool_ReportsFail()
    {
        // Max tool latency = 5000ms (>2× budget of 2000ms) → score = 0.0 → fail
        var eval = new ToolLatencyEval(MakeTelemetry(new Dictionary<string, double>
        {
            ["slow_tool"] = 5000,
            ["fast_tool"] = 200
        }), perToolBudgetMs: 2000);
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"tool_latency: expected Passed==false with max tool latency=5000ms, budget=2000ms, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_NoTelemetryData_ReturnsSkipped()
    {
        var eval = new ToolLatencyEval();
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);  // ToolLatencyEval skips when no telemetry data is provided.
    }
}
