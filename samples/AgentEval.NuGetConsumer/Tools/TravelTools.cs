// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using System.ComponentModel;

namespace AgentEval.NuGetConsumer.Tools;

/// <summary>
/// Travel booking tools for the demo agent.
/// These are fake implementations that return predictable results for testing.
/// </summary>
public static class TravelTools
{
    [Description("Search for available flights to a destination")]
    public static string SearchFlights(
        [Description("Destination city")] string destination,
        [Description("Travel date in YYYY-MM-DD format")] string date)
    {
        // Simulate flight search with predictable results
        return $"""
            Found 5 flights to {destination} on {date}:
            1. AF1234 - Air France - €299 - 10:00 departure
            2. BA5678 - British Airways - €349 - 12:30 departure
            3. LH9012 - Lufthansa - €279 - 14:00 departure
            4. KL3456 - KLM - €319 - 16:30 departure
            5. IB7890 - Iberia - €259 - 19:00 departure
            """;
    }

    [Description("Book a specific flight for passengers")]
    public static string BookFlight(
        [Description("Flight ID to book")] string flightId,
        [Description("Number of passengers")] int passengers)
    {
        // Simulate booking with confirmation
        var bookingRef = $"BK{Random.Shared.Next(100000, 999999)}";
        return $"Booking confirmed! Reference: {bookingRef} for {passengers} passenger(s) on flight {flightId}";
    }

    [Description("Send booking confirmation email to customer")]
    public static string SendConfirmation(
        [Description("Customer email address")] string email)
    {
        return $"Confirmation email sent to {email}";
    }

    [Description("Get user confirmation for an action")]
    public static string GetUserConfirmation(
        [Description("Action requiring confirmation")] string action)
    {
        // In real mode this would wait for user input
        return $"User confirmed: {action}";
    }

    [Description("Cancel an existing booking")]
    public static string CancelBooking(
        [Description("Booking reference to cancel")] string bookingRef)
    {
        return $"Booking {bookingRef} has been cancelled. Refund will be processed within 5-7 business days.";
    }
}
