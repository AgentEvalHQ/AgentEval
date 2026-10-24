// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Efficiency;
using AgentEval.Evals.Agentic.Telemetry;
using Xunit;

namespace AgentEval.Tests.Agentic.Efficiency;

/// <summary>
/// T82: <see cref="CostQualityEfficiencyEval"/> does not measure a run whose cost was not tracked
/// (<see cref="AgenticTelemetry.EstimatedCostUsd"/> null). "Not tracked" used to be 0, floored to $0.001: a 0.9 run
/// scored 900 per dollar, the best efficiency there is, and passed.
/// </summary>
public sealed class CostQualityEfficiencyNotTrackedTests
{
    private static EvalInput Input(double? costUsd) => new(
        Query: "test",
        Response: "result",
        Metadata: new Dictionary<string, object>
        {
            [CostQualityEfficiencyEval.TelemetryMetadataKey] = new AgenticTelemetry(
                LatencyMsP50: 250, LatencyMsP95: 450, LatencyMsP99: 500,
                TokensUsed: 150,
                EstimatedCostUsd: costUsd,
                ErrorCount: 0,
                TotalCalls: 1,
                RetryCount: 0,
                ToolLatencyMs: new Dictionary<string, double>()),
            [CostQualityEfficiencyEval.ScenarioScoreMetadataKey] = 0.9,
        });

    [Fact]
    public async Task UntrackedCost_IsNotMeasured_NotTheCheapestRunThereIs()
    {
        var result = await new CostQualityEfficiencyEval().EvaluateAsync(Input(null));

        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Null(result.Details.Dimensions);   // no estimated_cost_usd of 0 reaches a reader
    }

    [Fact]
    public async Task TrackedCost_IsMeasured()
    {
        // Guard: a tracked cost is still scored. 0.9 at $0.10 → 9 per dollar → clamp(9 / 5) = 1.0 → pass.
        var result = await new CostQualityEfficiencyEval().EvaluateAsync(Input(0.10));

        Assert.Equal("pass", result.Score.Label);
        Assert.Equal(0.10, result.Details.Dimensions!["estimated_cost_usd"], precision: 10);
    }
}
