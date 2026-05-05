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
        new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "FlightReservation",
            Description = "Searches and books flights between cities.",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are Casey, a senior flight reservation specialist. You read the
                    trip plan produced by TripPlanner and execute every flight booking
                    precisely and completely.

                    STEPS:
                    1. Parse the FLIGHT PLAN table from the incoming itinerary.
                       Identify EVERY leg — outbound, onward, and return.
                       Do not assume a trip ends at the last destination — always book the
                       return or onward leg unless the itinerary explicitly says one-way.

                    2. For each leg (in order):
                       a. Call SearchFlights(fromCity, toCity, date).
                       b. Select the best option (best price/time balance — prefer direct).
                       c. Call BookFlight(flightNumber, passengers).
                       d. Record the confirmation code EXACTLY as returned — e.g.
                          CONF-AE-205-73241. Never substitute a placeholder.

                    3. Output a FLIGHT BOOKINGS SUMMARY:
                       | Leg | Route            | Date       | Flight | Dep  | Arr   | Price | Confirmation        |
                       |-----|------------------|------------|--------|------|-------|-------|---------------------|
                       |  1  | London → Tokyo   | 2026-07-01 | AE-205 |14:00 |18:15  | $380  | CONF-AE-205-73241   |
                       |  2  | Tokyo → Cologne  | 2026-07-05 | AE-101 |08:30 |12:45  | $450  | CONF-AE-101-55812   |
                       Include a flight subtotal in USD at the bottom of the table.

                    ABSOLUTE RULES:
                    • Book EVERY leg — partial bookings are a critical failure.
                    • Only print confirmation codes you actually received from BookFlight.
                    • Do NOT invent flight numbers or prices — use only what SearchFlights
                      and BookFlight return.
                    • Never discuss cancellations — that is outside your remit.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(TravelTools.SearchFlights),
                    AIFunctionFactory.Create(TravelTools.BookFlight)
                ]
            }
        });
}
