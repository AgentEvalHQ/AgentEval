// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Telemetry;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Agentic.Telemetry;

/// <summary>
/// T82: a run whose cost was not tracked carries <see cref="AgenticTelemetry.EstimatedCostUsd"/> null, and
/// <see cref="CostEval"/> does not measure it. "Not tracked" used to be 0, which scored 1.0 against any budget: every
/// untracked run passed its cost check. A reported 0 stays a measured zero.
/// </summary>
public sealed class CostNotTrackedTests
{
    private static AgenticTelemetry Telemetry(double? costUsd) =>
        new(LatencyMsP50: 300, LatencyMsP95: 800, LatencyMsP99: 2000,
            TokensUsed: 1000, EstimatedCostUsd: costUsd,
            ErrorCount: 0, TotalCalls: 10, RetryCount: 0,
            ToolLatencyMs: new Dictionary<string, double>());

    [Fact]
    public async Task UntrackedCost_IsNotMeasured_NotWithinBudget()
    {
        var eval = new CostEval(Telemetry(null), budgetUsd: 1.00);

        var result = await eval.EvaluateAsync(new EvalInput(Query: "test"));

        // A skip scores 0 too: the label and the census bucket say it was not measured.
        Assert.Equal("skipped", result.Score.Label);
        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotMeasured, result.Score.CensusBucket());
        Assert.Contains("not tracked", result.Details.Summary, StringComparison.Ordinal);
        Assert.Null(result.Details.Dimensions);   // no cost_usd of 0 reaches a reader
    }

    [Fact]
    public async Task UntrackedCost_InMetadata_IsNotMeasured_OverAConstructorCost()
    {
        // Per-input metadata wins over the constructor's telemetry, a null cost included: the run in hand was not tracked.
        var eval = new CostEval(Telemetry(0.10), budgetUsd: 1.00);
        var input = new EvalInput(Query: "test", Metadata: new Dictionary<string, object>
        {
            [LatencyEval.MetadataKey] = Telemetry(null),
        });

        var result = await eval.EvaluateAsync(input);

        Assert.Equal("skipped", result.Score.Label);
    }

    [Fact]
    public async Task ReportedZeroCost_IsAMeasuredZero_WithinBudget()
    {
        // Guard: a run that reported $0 (cached, a free tier) is a measurement, and $0 is within any positive budget.
        var eval = new CostEval(Telemetry(0.0), budgetUsd: 1.00);

        var result = await eval.EvaluateAsync(new EvalInput(Query: "test"));

        Assert.Equal("pass", result.Score.Label);
        Assert.True(result.Score.Passed);
        Assert.Equal(1.0, result.Score.Value);
        Assert.Equal(0.0, result.Details.Dimensions!["cost_usd"]);
    }
}
