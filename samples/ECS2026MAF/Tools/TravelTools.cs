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

    [Description("Cancel a flight reservation. Non-refundable fares receive a travel voucher; refundable fares are fully refunded. Always inform the user of the policy before cancelling.")]
    public static async Task<string> CancelFlightReservation(
        [Description("Flight booking confirmation number to cancel (e.g. CONF-AE-205-12345)")] string bookingRef)
    {
        Console.WriteLine($"   ✈️❌ CancelFlightReservation(\"{bookingRef}\")");
        await Task.Delay(50);
        return $"""
            Flight reservation {bookingRef} cancelled.
            Policy applied  : Non-refundable fare — $150 change fee deducted.
            Refund method   : Travel voucher valid 12 months (remaining balance).
            Voucher code    : VCH-{Random.Shared.Next(10000, 99999)}
            Processing time : 5–7 business days.
            """;
    }

    [Description("Cancel a hotel booking. Free cancellation if 7+ days before check-in; otherwise a 1-night penalty applies. Always confirm the policy with the user before cancelling.")]
    public static async Task<string> CancelHotelBooking(
        [Description("Hotel booking confirmation number to cancel (e.g. HTL-TOK-12345)")] string bookingRef,
        [Description("City of the hotel (used to look up the booking)")] string city)
    {
        Console.WriteLine($"   🏨❌ CancelHotelBooking(\"{bookingRef}\", city=\"{city}\")");
        await Task.Delay(50);

        // Simulate whether we are within the free-cancellation window
        var isFree = Random.Shared.Next(0, 2) == 0;   // 50/50 for demo purposes

        if (isFree)
        {
            return $"""
                Hotel booking {bookingRef} in {city} cancelled.
                Policy applied  : Free cancellation (7+ days before check-in).
                Refund          : Full amount refunded to original payment method.
                Processing time : 3–5 business days.
                """;
        }
        else
        {
            return $"""
                Hotel booking {bookingRef} in {city} cancelled.
                Policy applied  : Late cancellation — 1-night penalty charged.
                Penalty charge  : 1 night at the booked rate.
                Remaining refund: Processed to original payment method.
                Processing time : 5–7 business days.
                """;
        }
    }

    // ── Hotel tools ──────────────────────────────────────────────────────────

    [Description("Search for available hotels in a city for the given dates.")]
    public static async Task<string> SearchHotel(
        [Description("City to search hotels in")] string city,
        [Description("Check-in date (YYYY-MM-DD)")] string checkIn,
        [Description("Check-out date (YYYY-MM-DD)")] string checkOut,
        [Description("Number of guests")] int guests = 1)
    {
        Console.WriteLine($"   🔍 SearchHotel(\"{city}\", {checkIn} → {checkOut}, guests={guests})");
        await Task.Delay(100);

        var (h1, h2, h3, price1, price2, price3) = city.ToLowerInvariant() switch
        {
            "tokyo"   => ("Shinjuku Grand Hotel",           "Park Hyatt Tokyo",              "APA Hotel Shinjuku",          180, 420, 90),
            "beijing" => ("Beijing Imperial Garden Hotel",  "The Peninsula Beijing",         "Beijing City Youth Hostel",   120, 380, 45),
            "paris"   => ("Hôtel de la Lumière",            "Le Meurice",                    "Hotel Ibis Paris Centre",     200, 650, 80),
            "london"  => ("The Westminster Arms Hotel",     "The Savoy",                     "Hub by Premier Inn",          220, 800,  95),
            "zurich"  => ("Hotel Zürichberg",                "Baur au Lac",                   "MEININGER Hotel Zürich",      280, 620, 110),
            "cologne" or "köln"
                      => ("Dorint Hotel am Dom Köln",        "Excelsior Hotel Ernst",         "A&O Köln City",               150, 380,  65),
            _         => ($"{city} Central Hotel",          $"{city} Grand Suites",          $"{city} Budget Inn",          100, 250,  55)
        };

        return $"""
            Available hotels in {city} ({checkIn} → {checkOut}, {guests} guest(s)):

            1. ⭐⭐⭐   {h1,-35} | ${price1}/night | Free WiFi, Breakfast included
            2. ⭐⭐⭐⭐⭐ {h2,-35} | ${price2}/night | Spa, Concierge, City views
            3. ⭐⭐     {h3,-35} | ${price3}/night | Budget-friendly, Central location

            Recommended: {h1} (best value)
            """;
    }

    [Description("Book a specific hotel in a city for the given dates. Returns a booking confirmation.")]
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
            "zurich"  => ("Hotel Zürichberg",                 280),
            "cologne" or "köln"
                      => ("Dorint Hotel am Dom Köln",         150),
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

            "paris" => """
                Paris, France — the City of Light, art, fashion, and romance.
                🗼 Attractions : Eiffel Tower, Louvre, Musée d'Orsay, Notre-Dame, Montmartre
                🥐 Food        : Croissants, baguettes, bistro classics, Café de Flore — eat everything.
                🚇 Transport   : 16-line Métro covers the whole city. Vélib bikes for short hops.
                🌸 Best time   : Apr–Jun or Sep–Oct (mild weather, fewer crowds).
                💰 Budget      : ~$150–300/day mid-range. Hotels from $100–250/night.
                """,

            "london" => """
                London, UK — a global metropolis of history, culture, and world-class food.
                🏰 Attractions : Tower of London, British Museum, Hyde Park, The Tate, Borough Market
                🍟 Food        : Sunday roast, fish & chips, and one of the world's best multicultural food scenes.
                🚇 Transport   : The Tube (Underground) + black cabs. Get an Oyster or contactless card.
                ☀️ Best time   : May–Sep for warmth. Dec for Christmas atmosphere.
                💰 Budget      : ~$150–300/day mid-range. Hotels from $100–250/night.
                """,

            "zurich" => """
                Zurich, Switzerland — Alpine elegance meets cutting-edge design and finance.
                🏔️ Attractions : Old Town (Altstadt), Lake Zurich, Kunsthaus, Rhine Falls day trip, Uetliberg hill
                🧀 Food        : Raclette, fondue, Zürcher Geschnetzeltes, Swiss chocolate — indulge freely.
                🚋 Transport   : Impeccable trams, buses, and S-Bahn (ZVV network). Swiss Travel Pass recommended.
                🌞 Best time   : Jun–Sep (hiking season) or Dec (magical Christmas markets).
                💰 Budget      : ~$250–400/day (one of Europe's priciest cities). Hotels from $150–350/night.
                """,

            "cologne" or "köln" => """
                Cologne (Köln), Germany — Rhine city famous for its cathedral, Kölsch beer, and carnival spirit.
                ⛪ Attractions : Cologne Cathedral (Dom, UNESCO), Old Town, Museum Ludwig, Rhine promenade
                🍺 Food        : Kölsch beer (only served here!), Himmel un Ääd, Halver Hahn, hearty Rhineland cuisine.
                🚇 Transport   : S-Bahn, U-Bahn, and trams (KVB). Excellent rail hub — Berlin/Paris/Amsterdam in 4 h.
                🎄 Best time   : May–Sep or December (some of Germany's finest Christmas markets).
                💰 Budget      : ~$100–180/day mid-range. Hotels from $80–180/night.
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
