// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Efficiency;
using AgentEval.Evals.Agentic.Telemetry;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Efficiency;

/// <summary>
/// Per-evaluator tests for <c>cost_quality_efficiency</c>.
/// Key: cost_quality_efficiency | Category: efficiency | CostTier: TRIVIAL
/// Pure-code evaluator — no judge required.
/// </summary>
public class CostQualityEfficiencyEvalTests
{
    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("cost_quality_efficiency", new FixedScoreEvaluator(100));

        Assert.Equal("cost_quality_efficiency", eval.Key);
        Assert.Equal("Cost-Quality Efficiency", eval.Name);
        Assert.Equal("efficiency", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_HighScore_ReportsPass()
    {
        // Score 1.0, cost $0.01 → score-per-dollar = 100 → normalised = clamp(100/5, 0, 1) = 1.0 → pass.
        var eval = new CostQualityEfficiencyEval();
        var telemetry = new AgenticTelemetry(
            LatencyMsP50: 250, LatencyMsP95: 450, LatencyMsP99: 500,
            TokensUsed: 150,
            EstimatedCostUsd: 0.01,
            ErrorCount: 0,
            TotalCalls: 1,
            RetryCount: 0,
            ToolLatencyMs: new Dictionary<string, double>());

        var input = new EvalInput(
            Query: "test",
            Response: "result",
            Metadata: new Dictionary<string, object>
            {
                [CostQualityEfficiencyEval.TelemetryMetadataKey] = telemetry,
                [CostQualityEfficiencyEval.ScenarioScoreMetadataKey] = 1.0,
            });

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"cost_quality_efficiency: expected Passed==true, got score={result.Score.Value}");
        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(1.0, result.Score.Value, precision: 3);
    }

    [Fact]
    public async Task EvaluateAsync_LowScore_ReportsFail()
    {
        // Score 0.1, cost $1.00 → score-per-dollar = 0.1 → normalised = 0.1/5 = 0.02 → fail (< 0.50 threshold).
        var eval = new CostQualityEfficiencyEval();
        var telemetry = new AgenticTelemetry(
            LatencyMsP50: 15000, LatencyMsP95: 28000, LatencyMsP99: 30000,
            TokensUsed: 60000,
            EstimatedCostUsd: 1.00,
            ErrorCount: 2,
            TotalCalls: 20,
            RetryCount: 3,
            ToolLatencyMs: new Dictionary<string, double>());

        var input = new EvalInput(
            Query: "test",
            Response: "result",
            Metadata: new Dictionary<string, object>
            {
                [CostQualityEfficiencyEval.TelemetryMetadataKey] = telemetry,
                [CostQualityEfficiencyEval.ScenarioScoreMetadataKey] = 0.1,
            });

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"cost_quality_efficiency: expected Passed==false, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_MissingRequiredInput_ReturnsSkipped()
    {
        var eval = new CostQualityEfficiencyEval();
        var input = new EvalInput(Query: "test", Response: "result");
        // Neither agentic_telemetry nor scenario_score

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }

    [Fact]
    public async Task EvaluateAsync_ScoreFormula_MatchesExpected()
    {
        // score=0.85, cost=$0.10 → scorePerDollar=8.5, normalised=8.5/5=1.7→clamp=1.0
        var eval = new CostQualityEfficiencyEval();
        var telemetry = new AgenticTelemetry(
            LatencyMsP50: 1000, LatencyMsP95: 1800, LatencyMsP99: 2000,
            TokensUsed: 1200,
            EstimatedCostUsd: 0.10,
            ErrorCount: 0,
            TotalCalls: 2,
            RetryCount: 0,
            ToolLatencyMs: new Dictionary<string, double>());

        var input = new EvalInput(
            Query: "test",
            Response: "result",
            Metadata: new Dictionary<string, object>
            {
                [CostQualityEfficiencyEval.TelemetryMetadataKey] = telemetry,
                [CostQualityEfficiencyEval.ScenarioScoreMetadataKey] = 0.85,
            });

        var result = await eval.EvaluateAsync(input);

        // Expected: scorePerDollar = 0.85/0.10 = 8.5, finalScore = clamp(8.5/5, 0, 1) = 1.0
        Assert.Equal(1.0, result.Score.Value, precision: 3);
        Assert.True(result.Score.Passed);
    }
}
