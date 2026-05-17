// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Telemetry;

/// <summary>
/// Golden tests for <c>cost</c> evaluator.
/// Key: cost | Category: operational | Threshold: 0.80
/// </summary>
public class CostEvalTests
{
    private static AgenticTelemetry MakeTelemetry(double costUsd) =>
        new(LatencyMsP50: 300, LatencyMsP95: 800, LatencyMsP99: 2000,
            TokensUsed: 1000, EstimatedCostUsd: costUsd,
            ErrorCount: 0, TotalCalls: 10, RetryCount: 0,
            ToolLatencyMs: new Dictionary<string, double>());

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("cost", new FixedScoreEvaluator(100));

        Assert.Equal("cost", eval.Key);
        Assert.Equal("Cost", eval.Name);
        Assert.Equal("operational", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_WithinBudget_ReportsPass()
    {
        // $0.50 <= $1.00 budget → score = 1.0 → pass
        var eval = new CostEval(MakeTelemetry(0.50), budgetUsd: 1.00);
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"cost: expected Passed==true with cost=$0.50, budget=$1.00, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_OverBudget_ReportsFail()
    {
        // $2.50 > 2× budget of $1.00 → score = 0.0 → fail
        var eval = new CostEval(MakeTelemetry(2.50), budgetUsd: 1.00);
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"cost: expected Passed==false with cost=$2.50, budget=$1.00, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_NoTelemetryData_ReturnsSkipped()
    {
        var eval = new CostEval();
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);  // CostEval skips when no telemetry data is provided.
    }
}
