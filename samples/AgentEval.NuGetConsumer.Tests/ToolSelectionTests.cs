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
/// Verifies the travel agent routes to the correct tools based on user intent.
/// All tests run the real agent through the MAFEvaluationHarness.
/// </summary>
public class ToolSelectionTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SearchRequest_ShouldCallSearchFlights_AndNotBook()
    {
        TestSetup.EnsureConfigured();

        var agent = new MAFAgentAdapter(AgentFactory.CreateTravelAIAgent(useMock: false));
        var harness = new MAFEvaluationHarness(verbose: false);

        var testCase = new TestCase
        {
            Name = "Tool Selection - Search Only",
            Input = "Use SearchFlights to find flights to London for April 1st, 2026. Just show me the options.",
            ExpectedTools = ["SearchFlights"]
        };

        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            options: new EvaluationOptions
            {
                TrackTools = true,
                ModelName = Config.Model
            });

        Assert.NotNull(result.ToolUsage);

        result.ToolUsage.Should()
            .HaveCalledTool("SearchFlights", because: "user asked to search for flights")
            .And()
            .NeverCallTool("BookFlight", because: "user only asked to search, not book")
            .NeverCallTool("CancelBooking", because: "no booking exists to cancel")
            .NeverCallTool("SendConfirmation", because: "no booking was made");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BookingRequest_ShouldFollowSearchBookConfirmOrder()
    {
        TestSetup.EnsureConfigured();

        var agent = new MAFAgentAdapter(AgentFactory.CreateTravelAIAgent(useMock: false));
        var harness = new MAFEvaluationHarness(verbose: false);

        var testCase = new TestCase
        {
            Name = "Tool Selection - Full Booking Order",
            Input = "Search for flights to Paris for May 10, 2026, book the cheapest one for 2 passengers, and send a confirmation email. Do not ask me the email, the tool already knows it.",
            ExpectedTools = ["SearchFlights", "BookFlight", "SendConfirmation"]
        };

        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            options: new EvaluationOptions
            {
                TrackTools = true,
                ModelName = Config.Model
            });

        Assert.NotNull(result.ToolUsage);
        Assert.True(result.ToolUsage.Count >= 3,
            $"Expected at least 3 tool calls (search, book, confirm), got {result.ToolUsage.Count}");

        result.ToolUsage.Should()
            .HaveCalledTool("SearchFlights", because: "must search before booking")
                .WithArgument("destination", "Paris")
            .And()
            .HaveCalledTool("BookFlight", because: "user requested booking the cheapest")
                .AfterTool("SearchFlights", because: "must search before booking")
            .And()
            .HaveCalledTool("SendConfirmation", because: "user requested confirmation email")
                .AfterTool("BookFlight", because: "must book before sending confirmation")
            .And()
            .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
            .HaveNoErrors();
    }
}
