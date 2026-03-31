// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Xunit;
using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.NuGetConsumer;

namespace AgentEval.NuGetConsumer.Tests;

/// <summary>
/// Validates real agent performance against SLAs: latency, token usage, and cost.
/// All metrics come from actual LLM execution through the harness.
/// </summary>
public class PerformanceTests
{
    [SkipIfNotConfiguredFact]
    [Trait("Category", "Integration")]
    public async Task SearchRequest_ShouldMeetLatencyAndTokenSLA()
    {

        var agent = new MAFAgentAdapter(AgentFactory.CreateTravelAIAgent(useMock: false));
        var harness = new MAFEvaluationHarness(verbose: false);

        var testCase = new TestCase
        {
            Name = "Performance - Search Latency",
            Input = "Search for flights to Amsterdam for May 5, 2026.",
            ExpectedTools = ["SearchFlights"]
        };

        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            options: new EvaluationOptions
            {
                TrackTools = true,
                TrackPerformance = true,
                ModelName = Config.Model
            });

        Assert.NotNull(result.Performance);

        result.Performance.Should()
            .HaveTotalDurationUnder(TimeSpan.FromSeconds(30), because: "single tool search should be fast");

        if (result.Performance.TotalTokens.HasValue)
        {
            result.Performance.Should()
                .HaveTokenCountUnder(5000, because: "simple search should be token-efficient");
        }

        Assert.NotNull(result.ToolUsage);
        result.ToolUsage.Should()
            .HaveCalledTool("SearchFlights", because: "search was requested")
            .And()
            .HaveNoErrors();
    }

    [SkipIfNotConfiguredFact]
    [Trait("Category", "Integration")]
    public async Task FullBookingFlow_ShouldMeetCostBudget()
    {

        var agent = new MAFAgentAdapter(AgentFactory.CreateTravelAIAgent(useMock: false));
        var harness = new MAFEvaluationHarness(verbose: false);

        var testCase = new TestCase
        {
            Name = "Performance - Booking Cost Budget",
            Input = "Search for flights to Stockholm for August 10, 2026, book the cheapest one for 1 passenger, and send confirmation. Don't ask for email, the tool already has it.",
            ExpectedTools = ["SearchFlights", "BookFlight", "SendConfirmation"]
        };

        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            options: new EvaluationOptions
            {
                TrackTools = true,
                TrackPerformance = true,
                ModelName = Config.Model
            });

        Assert.NotNull(result.Performance);
        Assert.True(result.Performance.TotalDuration > TimeSpan.Zero,
            "Duration must be positive for a real LLM call");

        if (result.Performance.EstimatedCost.HasValue)
        {
            result.Performance.Should()
                .HaveEstimatedCostUnder(0.50m, because: "single booking flow should be under $0.50");
        }

        result.Performance.Should()
            .HaveTotalDurationUnder(TimeSpan.FromSeconds(60), because: "booking flow should complete within 60s");

        Assert.NotNull(result.ToolUsage);
        result.ToolUsage.Should().HaveNoErrors();
    }
}
