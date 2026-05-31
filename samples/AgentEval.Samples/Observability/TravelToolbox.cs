// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.ComponentModel;

namespace AgentEval.Samples;

/// <summary>
/// A small, deterministic travel toolset for the Observability comparison samples — self-contained in the
/// Samples project (no dependency on the standalone TravelDemo). The tools return canned-but-plausible strings
/// so the agent does real multi-turn tool-calling against a real model without external services or cost.
/// </summary>
public static class TravelToolbox
{
    [Description("Returns a short travel briefing (best season, currency, suggested days) for a destination city.")]
    public static string GetDestinationInfo(string city)
        => $"{city}: best visited spring/autumn; major airport available; budget 2–3 days; local currency accepted everywhere.";

    [Description("Searches available flights between two cities on a date. Returns flight options with prices.")]
    public static string SearchFlights(string from, string to, string date)
        => $"Flights {from}→{to} on {date}: [GA{Math.Abs((from + to).GetHashCode()) % 900 + 100} 09:15 $420], [GA{Math.Abs((to + from).GetHashCode()) % 900 + 100} 18:40 $390].";

    [Description("Books a specific flight. Returns a confirmation code.")]
    public static string BookFlight(string flightNumber, string date)
        => $"BOOKED {flightNumber} on {date} — confirmation FLT-{Math.Abs((flightNumber + date).GetHashCode()) % 90000 + 10000}.";

    [Description("Searches hotels in a city for a check-in/check-out range. Returns hotel options with nightly rates.")]
    public static string SearchHotel(string city, string checkIn, string checkOut)
        => $"Hotels in {city} ({checkIn}→{checkOut}): [Grand {city} Center $180/night], [{city} Riverside Inn $130/night].";

    [Description("Books a specific hotel for a date range. Returns a confirmation code.")]
    public static string BookHotel(string hotelName, string checkIn, string checkOut)
        => $"BOOKED {hotelName} ({checkIn}→{checkOut}) — confirmation HTL-{Math.Abs((hotelName + checkIn).GetHashCode()) % 90000 + 10000}.";

    /// <summary>The canonical request used by both observability comparison samples (kept identical for parity).</summary>
    public const string CanonicalRequest =
        "Plan a 4-day trip from San Francisco to Tokyo departing 2026-04-10. Research the destination, " +
        "search and book one outbound and one return flight, search and book one hotel for the stay, " +
        "then give me a short itinerary with confirmation codes and a cost total. Use sensible defaults; do not ask me questions.";
}
