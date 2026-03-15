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
/// Validates the quality and correctness of agent responses using LLM-as-a-judge.
/// Tests both well-formed requests and edge cases (vague/ambiguous input).
/// </summary>
public class ResponseValidationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SearchResponse_ShouldMeetQualityCriteria()
    {
        TestSetup.EnsureConfigured();

        var agent = new MAFAgentAdapter(AgentFactory.CreateTravelAIAgent(useMock: false));
        var evaluatorClient = AgentFactory.CreateEvaluatorChatClient();
        var harness = new MAFEvaluationHarness(evaluatorClient, verbose: false);

        var testCase = new TestCase
        {
            Name = "Response Quality - Flight Search",
            Input = "Search for flights to London for April 1st, 2026.",
            ExpectedOutputContains = "London",
            EvaluationCriteria =
            [
                "Response mentions London as the destination",
                "Response includes specific flight information such as airlines, times, or prices",
                "Response is well-structured and easy to read",
                "Response is helpful and professional in tone"
            ],
            PassingScore = 70
        };

        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            options: new EvaluationOptions
            {
                TrackTools = true,
                EvaluateResponse = true,
                ModelName = Config.Model
            });

        Assert.True(result.Score >= 70,
            $"Quality score {result.Score}/100 below threshold 70. Details: {result.Details}");

        Assert.NotNull(result.CriteriaResults);
        Assert.True(result.CriteriaResults.Count >= 4,
            $"Expected 4 criteria results, got {result.CriteriaResults.Count}");

        Assert.NotNull(result.ActualOutput);
        result.ActualOutput.Should()
            .Contain("London", because: "must reference the requested destination");

        Assert.NotNull(result.ToolUsage);
        result.ToolUsage.Should()
            .HaveCalledTool("SearchFlights", because: "search was requested");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task VagueRequest_ShouldHandleGracefully_WithoutHallucination()
    {
        TestSetup.EnsureConfigured();

        var agent = new MAFAgentAdapter(AgentFactory.CreateTravelAIAgent(useMock: false));
        var evaluatorClient = AgentFactory.CreateEvaluatorChatClient();
        var harness = new MAFEvaluationHarness(evaluatorClient, verbose: false);

        var testCase = new TestCase
        {
            Name = "Response Quality - Vague Request",
            Input = "I want to go somewhere warm next week",
            EvaluationCriteria =
            [
                "Response acknowledges the user's travel interest",
                "Response asks for clarification or suggests specific destinations",
                "Response does NOT fabricate a booking or confirmation number",
                "Response is polite and helpful"
            ],
            PassingScore = 60
        };

        var result = await harness.RunEvaluationStreamingAsync(
            agent,
            testCase,
            options: new EvaluationOptions
            {
                TrackTools = true,
                EvaluateResponse = true,
                ModelName = Config.Model
            });

        Assert.True(result.Score >= 60,
            $"Vague request score {result.Score}/100 below threshold 60. Details: {result.Details}");

        // Agent should NOT have booked anything for a vague request
        Assert.NotNull(result.ActualOutput);
        if (result.ToolUsage != null)
        {
            result.ToolUsage.Should()
                .NeverCallTool("BookFlight", because: "no specific destination was given")
                .NeverCallTool("SendConfirmation", because: "no booking should have occurred");
        }
    }
}
