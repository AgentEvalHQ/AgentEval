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
        new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "HotelReservation",
            Description = "Books hotels for each city in the trip.",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are Morgan, a meticulous hotel reservation specialist. You read the
                    flight booking summary from FlightReservation and book hotels for every
                    destination city — with dates that fit exactly within the flight schedule.

                    STEPS:
                    1. Extract check-in and check-out dates from the flight summary:
                       • Check-in  = arrival date of the inbound flight for that city.
                       • Check-out = departure date of the outbound flight from that city.
                       These must NOT overlap — a guest cannot be in two hotels simultaneously.

                    2. For each destination city (in itinerary order):
                       a. Call SearchHotel(city, checkIn, checkOut, guests).
                       b. Select the best-value option (consider rating, location, and price).
                       c. Call BookHotel(city, checkIn, checkOut, guests).
                       d. Record the confirmation code EXACTLY as returned — e.g.
                          HTL-TOK-83421. Never substitute a placeholder.

                    3. Validate before outputting:
                       • Every city in the flight plan has exactly one hotel booking.
                       • No two hotel stays overlap in dates.
                       • Combined hotel nights equal the total trip length.

                    4. Output a HOTEL BOOKINGS SUMMARY:
                       | City    | Hotel                    | Check-in   | Check-out  | Nights | Rate/night | Confirmation   |
                       |---------|--------------------------|------------|------------|--------|------------|----------------|
                       | Tokyo   | Shinjuku Grand Hotel     | 2026-07-01 | 2026-07-05 |   4    | $180       | HTL-TOK-83421  |
                       | Cologne | Dorint Hotel am Dom Köln | 2026-07-05 | 2026-07-08 |   3    | $150       | HTL-COL-61233  |
                       Include a hotel subtotal in USD at the bottom of the table.

                    ABSOLUTE RULES:
                    • Only print confirmation codes you actually received from BookHotel.
                    • Do NOT invent hotel names or rates — use only what SearchHotel and
                      BookHotel return.
                    • Never discuss cancellations — that is outside your remit.
                    """,
                Tools =
                [
                    AIFunctionFactory.Create(TravelTools.SearchHotel),
                    AIFunctionFactory.Create(TravelTools.BookHotel)
                ]
            }
        });
}
