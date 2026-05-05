// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ECS2026MAF.Tools;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace ECS2026MAF.Agents;

/// <summary>
/// Creates the <c>TripPlanner</c> agent for the TripPlanner Workflow (Demo 02).
/// Responsible for gathering city information and producing a day-by-day itinerary.
/// </summary>
public static class TripPlannerAgentFactory
{
    public static ChatClientAgent Create(IChatClient chatClient) =>
        new(chatClient, new ChatClientAgentOptions
        {
            Name = "TripPlanner",
            Description = "Gathers city information and plans the trip itinerary.",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a trip planning specialist.

                    Given a travel request:
                    1. Call GetInfoAbout for EACH city mentioned in the request.
                    2. Build a day-by-day itinerary outline based on that information.
                    3. Note which cities need flights and hotel bookings.

                    Your output is passed directly to the FlightReservation agent.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(TravelTools.GetInfoAbout)
                ]
            }
        });
}
