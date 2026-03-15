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
/// End-to-end test that exercises the complete travel booking pipeline.
/// Validates tool selection, ordering, arguments, response quality (LLM-as-judge), and performance
/// all in a single comprehensive evaluation.
/// </summary>
public class EndToEndBookingTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task CompleteBookingFlow_ShouldPassAllEvaluations()
    {
        TestSetup.EnsureConfigured();

        var agent = new MAFAgentAdapter(AgentFactory.CreateTravelAIAgent(useMock: false));
        var evaluatorClient = AgentFactory.CreateEvaluatorChatClient();
        var harness = new MAFEvaluationHarness(evaluatorClient, verbose: false);

        var testCase = new TestCase
        {
            Name = "E2E - Complete Travel Booking",
            Input = "Search for flights to Tokyo for March 20, 2026, book the cheapest one for 1 passenger, and send confirmation. Do not ask me the email, the tool already knows it.",
            ExpectedOutputContains = "Tokyo",
            ExpectedTools = ["SearchFlights", "BookFlight", "SendConfirmation"],
            EvaluationCriteria =
            [
                "Response confirms Tokyo as the destination",
                "Response mentions that a flight was booked",
                "Response includes a booking reference or confirmation number",
                "Response mentions a confirmation email was sent",
                "Response is helpful and professional in tone"
            ],
            PassingScore = 75,
            GroundTruth = "Flight booking confirmed to Tokyo for March 20, 2026 with confirmation email sent",
            Tags = ["e2e", "booking", "integration"]
        };

        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            options: new EvaluationOptions
            {
                TrackTools = true,
                TrackPerformance = true,
                EvaluateResponse = true,
                ModelName = Config.Model
            });

        // --- Overall evaluation ---
        Assert.True(result.Passed,
            $"E2E test failed. Score: {result.Score}/100. Details: {result.Details}");

        // --- Tool usage: correct tools in correct order ---
        Assert.NotNull(result.ToolUsage);
        result.ToolUsage.Should()
            .HaveCalledTool("SearchFlights", because: "must search before booking")
                .WithArgument("destination", "Tokyo")
            .And()
            .HaveCalledTool("BookFlight", because: "user requested booking")
                .AfterTool("SearchFlights", because: "must search before booking")
            .And()
            .HaveCalledTool("SendConfirmation", because: "user requested confirmation email")
                .AfterTool("BookFlight", because: "must book before confirming")
            .And()
            .HaveCallOrder("SearchFlights", "BookFlight", "SendConfirmation")
            .HaveNoErrors();

        // --- Response content ---
        Assert.NotNull(result.ActualOutput);
        result.ActualOutput.Should()
            .Contain("Tokyo", because: "must reference the destination")
            .HaveLengthBetween(50, 5000, because: "response should be informative but not excessive");

        // --- LLM-as-judge criteria ---
        Assert.NotNull(result.CriteriaResults);
        Assert.True(result.CriteriaResults.Count > 0, "LLM-as-judge should have evaluated criteria");
        var metCount = result.CriteriaResults.Count(c => c.Met);
        Assert.True(metCount >= 3,
            $"Expected at least 3/{result.CriteriaResults.Count} criteria met by LLM-as-judge, " +
            $"got {metCount}. Failed: {string.Join(", ", result.CriteriaResults.Where(c => !c.Met).Select(c => c.Criterion))}");

        // --- Performance ---
        Assert.NotNull(result.Performance);
        result.Performance.Should()
            .HaveTotalDurationUnder(TimeSpan.FromSeconds(60), because: "E2E booking should complete within 60s")
            .HaveTokenCountUnder(15000, because: "token budget for a booking flow");

        if (result.Performance.EstimatedCost.HasValue)
        {
            result.Performance.Should()
                .HaveEstimatedCostUnder(1.00m, because: "single booking should cost under $1");
        }
    }
}
