// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Samples;

namespace AgentEval.Tests.Samples;

public sealed class ReliabilityRaceStatisticsTests
{
    [Fact]
    public void Create_MixedOutcomes_SeparatesCorrectnessToolUseAndReliability()
    {
        ReliabilityRaceObservation[] observations =
        [
            Observation(correct: true, toolAdherent: true, reliable: true, toolCalls: 1),
            Observation(correct: true, toolAdherent: false, reliable: false, toolCalls: 0),
            Observation(correct: false, toolAdherent: true, reliable: false, toolCalls: 1),
            Observation(correct: true, toolAdherent: true, reliable: false, toolCalls: 2, error: "timeout"),
        ];

        var summary = ReliabilityRaceSummary.Create("model-a", observations);

        Assert.Equal((3, 4), (summary.Correct.Successes, summary.Correct.Total));
        Assert.Equal((3, 4), (summary.ToolAdherence.Successes, summary.ToolAdherence.Total));
        Assert.Equal((2, 4), (summary.ExactlyOneToolCall.Successes, summary.ExactlyOneToolCall.Total));
        Assert.Equal((1, 4), (summary.Reliable.Successes, summary.Reliable.Total));
        Assert.Equal(1, summary.ErrorCount);
    }

    [Fact]
    public void Create_PerformanceSamples_ComputesPercentilesTokensAndCostPerReliableRun()
    {
        ReliabilityRaceObservation[] observations =
        [
            Observation(true, true, true, 1, latencyMs: 100, cost: 0.01m, tokens: 80),
            Observation(true, true, true, 1, latencyMs: 200, cost: 0.01m, tokens: 100),
            Observation(false, true, false, 1, latencyMs: 300, cost: 0.01m, tokens: 120),
            Observation(true, false, false, 0, latencyMs: 400, cost: 0.01m, tokens: 140),
        ];

        var summary = ReliabilityRaceSummary.Create("model-a", observations);

        Assert.Equal(250, summary.P50LatencyMs);
        Assert.Equal(385, summary.P95LatencyMs);
        Assert.Equal(110, summary.AverageTokens);
        Assert.Equal(0.04m, summary.TotalCost);
        Assert.Equal(0.02m, summary.CostPerReliableRun);
    }

    [Fact]
    public void Create_UnmeasuredPerformance_ReportsNullInsteadOfInventingZero()
    {
        ReliabilityRaceObservation[] observations =
        [
            Observation(true, true, false, 1),
        ];

        var summary = ReliabilityRaceSummary.Create("model-a", observations);

        Assert.Null(summary.P50LatencyMs);
        Assert.Null(summary.P95LatencyMs);
        Assert.Null(summary.AverageTokens);
        Assert.Null(summary.TotalCost);
        Assert.Null(summary.CostPerReliableRun);
    }

    private static ReliabilityRaceObservation Observation(
        bool correct,
        bool toolAdherent,
        bool reliable,
        int toolCalls,
        double? latencyMs = null,
        decimal? cost = null,
        int? tokens = null,
        string? error = null) =>
        new(
            Scenario: "scenario",
            Correct: correct,
            ToolAdherent: toolAdherent,
            Reliable: reliable,
            ToolCalls: toolCalls,
            LatencyMs: latencyMs,
            Cost: cost,
            TotalTokens: tokens,
            Output: string.Empty,
            Error: error);
}
