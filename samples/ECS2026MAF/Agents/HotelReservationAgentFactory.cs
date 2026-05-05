// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ECS2026MAF.Tools;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace ECS2026MAF.Agents;

/// <summary>
/// Creates the <c>HotelReservation</c> agent for the TripPlanner Workflow (Demo 02).
/// Books hotels for each city based on the confirmed flight schedule.
/// </summary>
public static class HotelReservationAgentFactory
{
    public static ChatClientAgent Create(IChatClient chatClient) =>
        new(chatClient, new ChatClientAgentOptions
        {
            Name = "HotelReservation",
            Description = "Books hotels for each city in the trip.",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a hotel reservation specialist.

                    Given a trip plan with confirmed flights:
                    1. Call BookHotel for EACH city that needs accommodation.
                    2. Choose check-in/check-out dates aligned with the flight schedule.
                    3. Summarise each booking with hotel name, dates, and confirmation number.

                    Your output is passed directly to the Presenter agent.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(TravelTools.BookHotel)
                ]
            }
        });
}
