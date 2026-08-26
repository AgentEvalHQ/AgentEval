// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Samples;

namespace AgentEval.Tests.Samples;

public sealed class ReliabilityRaceStatisticsTests
{
    [Theory]
    [InlineData("ROUTE=P1-04H", true)]
    [InlineData("  ROUTE=P1-04H\r\n", true)]
    [InlineData("P1-04H", false)]
    [InlineData("Authoritative route: P1-04H", false)]
    [InlineData("ROUTE=P1-04H because it is urgent", false)]
    [InlineData("ROUTE=P3-24H", false)]
    public void ExactRouteOutput_RequiresContractWithoutExtraText(string actualOutput, bool expected)
    {
        Assert.Equal(expected, ReliabilityRaceTrialRules.IsExactRouteOutput(actualOutput, "P1-04H"));
    }

    [Fact]
    public void ConsecutiveSetupFailures_LaterInRun_StopsAfterLastThreeErrors()
    {
        ReliabilityRaceObservation[] observations =
        [
            Observation(true, true, true, 1),
            Observation(false, false, false, 0, error: "setup-1"),
            Observation(false, false, false, 0, error: "setup-2"),
            Observation(false, false, false, 0, error: "setup-3"),
        ];

        Assert.True(ReliabilityRaceTrialRules.HasConsecutiveSetupFailures(observations));
    }

    [Fact]
    public void ConsecutiveSetupFailures_InterruptedBySuccess_DoesNotStop()
    {
        ReliabilityRaceObservation[] observations =
        [
            Observation(false, false, false, 0, error: "setup-1"),
            Observation(false, false, false, 0, error: "setup-2"),
            Observation(true, true, true, 1),
        ];

        Assert.False(ReliabilityRaceTrialRules.HasConsecutiveSetupFailures(observations));
    }

    [Fact]
    public void Decision_EqualReliabilityButEconomyDominates_RecommendsEconomyWithoutInventingReliabilityLead()
    {
        var frontier = ReliabilityRaceSummary.Create(
            "frontier",
            Enumerable.Range(0, 5)
                .Select(_ => Observation(true, true, true, 1, latencyMs: 3000, cost: 0.004m, tokens: 600))
                .ToArray());
        var economy = ReliabilityRaceSummary.Create(
            "economy",
            Enumerable.Range(0, 5)
                .Select(_ => Observation(true, true, true, 1, latencyMs: 1500, cost: 0.0001m, tokens: 400))
                .ToArray());

        var decision = ReliabilityRaceDecision.Create(frontier, economy);

        Assert.True(decision.ReliabilityIsDraw);
        Assert.Null(decision.ReliabilityLeader);
        Assert.Equal(0, decision.ReliabilityDelta);
        Assert.False(decision.RecommendationIsTie);
        Assert.Equal("economy", Assert.Single(decision.RecommendedWinners));
        Assert.Contains("no worse", decision.RecommendationReason);
    }

    [Fact]
    public void Decision_QualityVersusEfficiencyTradeoff_NamesBothJointWinners()
    {
        var quality = ReliabilityRaceSummary.Create(
            "quality",
            [
                Observation(true, true, true, 1, latencyMs: 3000, cost: 0.004m, tokens: 600),
                Observation(true, true, true, 1, latencyMs: 3000, cost: 0.004m, tokens: 600),
            ]);
        var efficiency = ReliabilityRaceSummary.Create(
            "efficiency",
            [
                Observation(true, true, true, 1, latencyMs: 1000, cost: 0.0001m, tokens: 300),
                Observation(false, true, false, 1, latencyMs: 1000, cost: 0.0001m, tokens: 300),
            ]);

        var decision = ReliabilityRaceDecision.Create(quality, efficiency);

        Assert.False(decision.ReliabilityIsDraw);
        Assert.True(decision.RecommendationIsTie);
        Assert.Equal(["quality", "efficiency"], decision.RecommendedWinners);
        Assert.Contains("Neither model dominates", decision.RecommendationReason);
    }

    [Fact]
    public void Decision_AllFactorsEqual_NamesBothJointWinners()
    {
        ReliabilityRaceObservation[] observations =
        [
            Observation(true, true, true, 1, latencyMs: 1000, cost: 0.001m, tokens: 300),
        ];

        var decision = ReliabilityRaceDecision.Create(
            ReliabilityRaceSummary.Create("a", observations),
            ReliabilityRaceSummary.Create("b", observations));

        Assert.True(decision.ReliabilityIsDraw);
        Assert.True(decision.RecommendationIsTie);
        Assert.Equal(["a", "b"], decision.RecommendedWinners);
        Assert.Equal("Every comparable factor is tied.", decision.RecommendationReason);
    }

    [Fact]
    public void Decision_ToolExecutionError_RecommendsErrorFreeModelAndNamesFactor()
    {
        var errorFree = ReliabilityRaceSummary.Create(
            "error-free",
            [Observation(true, true, true, 1)]);
        var toolError = ReliabilityRaceSummary.Create(
            "tool-error",
            [Observation(true, true, false, 1, toolExecutionError: true)]);

        var decision = ReliabilityRaceDecision.Create(errorFree, toolError);

        Assert.False(decision.RecommendationIsTie);
        Assert.Equal("error-free", Assert.Single(decision.RecommendedWinners));
        Assert.Contains("tool execution error rate", decision.RecommendationReason);
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("10", 10)]
    [InlineData("20", 20)]
    [InlineData("100", 100)]
    public void RunCountSelector_ConfiguredAllowedValue_ReturnsValue(string configured, int expected)
    {
        var output = new StringWriter();

        var selected = ReliabilityRaceRunCountSelector.Select(
            configured,
            interactive: false,
            TextReader.Null,
            output);

        Assert.Equal(expected, selected);
        Assert.Contains("AGENTEVAL_RELIABILITY_RUNS", output.ToString());
    }

    [Fact]
    public void RunCountSelector_InteractiveInvalidThenValid_PromptsAgain()
    {
        var input = new StringReader("7\n100\n");
        var output = new StringWriter();

        var selected = ReliabilityRaceRunCountSelector.Select(null, interactive: true, input, output);

        Assert.Equal(100, selected);
        Assert.Contains("Enter 5, 10, 20, or 100", output.ToString());
    }

    [Fact]
    public void RunCountSelector_InteractiveBlank_UsesRecommendedDefault()
    {
        var selected = ReliabilityRaceRunCountSelector.Select(
            null,
            interactive: true,
            new StringReader(Environment.NewLine),
            TextWriter.Null);

        Assert.Equal(20, selected);
    }

    [Fact]
    public void RunCountSelector_NonInteractive_UsesRecommendedDefault()
    {
        var selected = ReliabilityRaceRunCountSelector.Select(
            null,
            interactive: false,
            TextReader.Null,
            TextWriter.Null);

        Assert.Equal(20, selected);
    }

    [Fact]
    public void RunCountSelector_InvalidConfiguredValue_Throws()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            ReliabilityRaceRunCountSelector.Select("7", false, TextReader.Null, TextWriter.Null));

        Assert.Contains("5, 10, 20, 100", exception.Message);
    }

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
        string? error = null,
        bool toolExecutionError = false) =>
        new(
            Scenario: "scenario",
            Correct: correct,
            ToolAdherent: toolAdherent,
            ToolExecutionError: toolExecutionError,
            Reliable: reliable,
            ToolCalls: toolCalls,
            LatencyMs: latencyMs,
            Cost: cost,
            TotalTokens: tokens,
            Output: string.Empty,
            Error: error);
}
