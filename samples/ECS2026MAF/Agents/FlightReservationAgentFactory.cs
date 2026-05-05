// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ECS2026MAF.Tools;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace ECS2026MAF.Agents;

/// <summary>
/// Creates the <c>FlightReservation</c> agent for the TripPlanner Workflow (Demo 02).
/// Searches for flights between cities and makes bookings.
/// </summary>
public static class FlightReservationAgentFactory
{
    public static ChatClientAgent Create(IChatClient chatClient) =>
        new(chatClient, new ChatClientAgentOptions
        {
            Name = "FlightReservation",
            Description = "Searches and books flights between cities.",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a flight reservation specialist.

                    Given a trip itinerary:
                    1. Call SearchFlights for each leg of the journey.
                    2. Select the best option and call BookFlight to confirm.
                    3. Summarise booked flights with times, prices, and confirmation numbers.

                    Your output is passed directly to the HotelReservation agent.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(TravelTools.SearchFlights),
                    AIFunctionFactory.Create(TravelTools.BookFlight)
                ]
            }
        });
}
