// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

using System.ComponentModel;

namespace ECS2026MAF.Tools;

/// <summary>
/// Shared travel tools used by both the TravelAgent (Demo 01) and the
/// TripPlanner Workflow (Demo 02).  All implementations are intentionally
/// fake/deterministic so demos run reliably without external APIs.
/// </summary>
public static class TravelTools
{
    // ── Flight tools ─────────────────────────────────────────────────────────

    [Description("Search for available flights between two cities on a given date.")]
    public static async Task<string> SearchFlights(
        [Description("Departure city (leave empty for single-destination searches)")] string fromCity,
        [Description("Destination city")] string toCity,
        [Description("Travel date in YYYY-MM-DD format")] string date)
    {
        Console.WriteLine($"   ✈️  SearchFlights(\"{fromCity}\" → \"{toCity}\", {date})");
        await Task.Delay(100);

        var origin = string.IsNullOrWhiteSpace(fromCity) ? "your city" : fromCity;

        return $"""
            Available flights from {origin} to {toCity} on {date}:

            1. ✈️  AE-101  |  08:30 → 12:45  |  $450  |  Direct  |  Economy
            2. ✈️  AE-205  |  14:00 → 18:15  |  $380  |  Direct  |  Economy
            3. ✈️  AE-309  |  20:30 → 00:45+1|  $320  |  Direct  |  Economy

            Recommended: AE-205 (best price/time balance)
            """;
    }

    [Description("Book a specific flight. Returns a booking confirmation number.")]
    public static async Task<string> BookFlight(
        [Description("Flight number to book (e.g. AE-205)")] string flightNumber,
        [Description("Number of passengers")] int passengers = 1)
    {
        Console.WriteLine($"   🎫 BookFlight(\"{flightNumber}\", passengers={passengers})");
        await Task.Delay(100);

        var confirmation = $"CONF-{flightNumber}-{Random.Shared.Next(10000, 99999)}";

        return $"""
            ✅ Flight {flightNumber} booked!
            Confirmation : {confirmation}
            Passengers   : {passengers}
            Status       : CONFIRMED
            """;
    }

    [Description("Send a booking confirmation email to the customer.")]
    public static async Task<string> SendConfirmation(
        [Description("Customer email address")] string email)
    {
        Console.WriteLine($"   📧 SendConfirmation(\"{email}\")");
        await Task.Delay(50);
        return $"Confirmation email sent to {email}.";
    }

    [Description("Request user confirmation before performing a sensitive action.")]
    public static async Task<string> GetUserConfirmation(
        [Description("Description of the action requiring approval")] string action)
    {
        Console.WriteLine($"   🔐 GetUserConfirmation(\"{action}\")");
        await Task.Delay(50);
        return $"User confirmed: {action}";
    }

    [Description("Cancel an existing booking by reference number.")]
    public static async Task<string> CancelBooking(
        [Description("Booking reference number to cancel")] string bookingRef)
    {
        Console.WriteLine($"   ❌ CancelBooking(\"{bookingRef}\")");
        await Task.Delay(50);
        return $"Booking {bookingRef} cancelled. Refund processed within 5–7 business days.";
    }

    // ── Hotel tools ──────────────────────────────────────────────────────────

    [Description("Book a hotel in a city for the given dates. Returns confirmation details.")]
    public static async Task<string> BookHotel(
        [Description("City to book the hotel in")] string city,
        [Description("Check-in date (YYYY-MM-DD)")] string checkIn,
        [Description("Check-out date (YYYY-MM-DD)")] string checkOut,
        [Description("Number of guests")] int guests = 1)
    {
        Console.WriteLine($"   🏨 BookHotel(\"{city}\", {checkIn} → {checkOut}, guests={guests})");
        await Task.Delay(100);

        var (hotelName, pricePerNight) = city.ToLowerInvariant() switch
        {
            "tokyo"   => ("Shinjuku Grand Hotel",            180),
            "beijing" => ("Beijing Imperial Garden Hotel",   120),
            "paris"   => ("Hôtel de la Lumière",             200),
            "london"  => ("The Westminster Arms Hotel",      220),
            _         => ($"{city} Central Hotel",           100)
        };

        var confirmation = $"HTL-{city[..Math.Min(3, city.Length)].ToUpperInvariant()}-{Random.Shared.Next(10000, 99999)}";

        return $"""
            ✅ Hotel booked!
            Hotel        : {hotelName}
            City         : {city}
            Check-in     : {checkIn}
            Check-out    : {checkOut}
            Guests       : {guests}
            Rate         : ${pricePerNight}/night
            Confirmation : {confirmation}
            Status       : CONFIRMED
            """;
    }

    // ── Destination info tool ─────────────────────────────────────────────────

    [Description("Get information about a city: attractions, food, transport, and travel tips.")]
    public static async Task<string> GetInfoAbout(
        [Description("Name of the city to look up")] string city)
    {
        Console.WriteLine($"   🌍 GetInfoAbout(\"{city}\")");
        await Task.Delay(100);

        return city.ToLowerInvariant() switch
        {
            "tokyo" => """
                Tokyo, Japan — ultra-modern metropolis blending tradition and tech.
                🏯 Attractions : Senso-ji, Shibuya Crossing, Meiji Shrine, Akihabara, Tokyo Skytree
                🍣 Food        : World-class sushi, ramen, tempura — Tsukiji Outer Market is unmissable.
                🚄 Transport   : Efficient metro + JR rail. Get a Suica card.
                🌸 Best time   : Mar–May (cherry blossoms) or Oct–Nov (autumn).
                💰 Budget      : ~$150–250/day mid-range. Hotels from $80–200/night.
                """,

            "beijing" => """
                Beijing, China — ancient capital with 3 000+ years of history.
                🏯 Attractions : Great Wall (Mutianyu), Forbidden City, Temple of Heaven, Summer Palace
                🥟 Food        : Peking duck, dumplings, hot pot, Wangfujing street food.
                🚇 Transport   : Extensive subway. DiDi app for taxis.
                🌤️ Best time   : Sep–Oct (clear skies) or Apr–May (spring).
                💰 Budget      : ~$80–150/day mid-range. Hotels from $50–150/night.
                """,

            _ => $"""
                {city} — a wonderful travel destination.
                🏛️ Attractions : Various cultural and historical sites.
                🍽️ Food        : Local cuisine well worth exploring.
                🚌 Transport   : Public transportation available.
                💰 Budget      : Varies by season and accommodation choice.
                """
        };
    }
}
