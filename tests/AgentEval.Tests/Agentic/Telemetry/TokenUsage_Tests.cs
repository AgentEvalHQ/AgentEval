// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using Xunit;
using AgentEval.Tests.Agentic;

namespace AgentEval.Tests.Agentic.Telemetry;

/// <summary>
/// Golden tests for <c>token_usage</c> evaluator.
/// Key: token_usage | Category: operational | Threshold: 0.80
/// </summary>
public class TokenUsageEvalTests
{
    private static AgenticTelemetry MakeTelemetry(int tokensUsed) =>
        new(LatencyMsP50: 300, LatencyMsP95: 800, LatencyMsP99: 2000,
            TokensUsed: tokensUsed, EstimatedCostUsd: 0.02,
            ErrorCount: 0, TotalCalls: 10, RetryCount: 0,
            ToolLatencyMs: new Dictionary<string, double>());

    [Fact]
    public void Build_HasExpectedShape()
    {
        var eval = AgenticEvaluatorFixture.BuildEvaluator("token_usage", new FixedScoreEvaluator(100));

        Assert.Equal("token_usage", eval.Key);
        Assert.Equal("Token Usage", eval.Name);
        Assert.Equal("operational", eval.Category);
    }

    [Fact]
    public async Task EvaluateAsync_WithinBudget_ReportsPass()
    {
        // 5000 tokens <= 10000 budget → score = 1.0 → pass
        var eval = new TokenUsageEval(MakeTelemetry(5000), budgetTokens: 10000);
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.True(result.Score.Passed,
            $"token_usage: expected Passed==true with tokens=5000, budget=10000, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_OverBudget_ReportsFail()
    {
        // 25000 tokens (>2× budget of 10000) → score = 0.0 → fail
        var eval = new TokenUsageEval(MakeTelemetry(25000), budgetTokens: 10000);
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.False(result.Score.Passed,
            $"token_usage: expected Passed==false with tokens=25000, budget=10000, got score={result.Score.Value}");
    }

    [Fact]
    public async Task EvaluateAsync_NoTelemetryData_ReturnsSkipped()
    {
        var eval = new TokenUsageEval();
        var input = new EvalInput(Query: "test");

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);  // TokenUsageEval skips when no telemetry data is provided.
    }
}
