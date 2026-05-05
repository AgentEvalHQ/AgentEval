// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

namespace ECS2026MAF.Evals;

/// <summary>
/// Canonical evaluation criteria for the Tokyo + Cologne travel hypothesis test.
///
/// Centralising criteria here guarantees Eval01, Eval02, the stochastic evals, and
/// Eval03 (comparison) all test the SAME bar — making score comparisons valid.
/// </summary>
public static class TravelEvalCriteria
{
    // ── Shared criteria (both architectures) ──────────────────────────────

    public const string Research =
        "The response includes destination-specific content for BOTH Tokyo and Cologne: "
        + "at least one attraction, food tip, or transport tip per city — demonstrating "
        + "that city research (GetInfoAbout) was incorporated, not invented";

    public const string BothFlightLegs =
        "The response documents AT LEAST TWO separate flight bookings: one outbound leg "
        + "(e.g. origin → Tokyo or Tokyo → Cologne) AND one onward/return leg. "
        + "A single one-way booking is insufficient for a multi-city 7-day trip";

    public const string HotelCompleteness =
        "The response confirms hotel bookings in BOTH Tokyo AND Cologne, each with a "
        + "hotel name, check-in date, check-out date, and a booking confirmation reference";

    public const string DateCoherence =
        "The Tokyo hotel stay dates and the Cologne hotel stay dates do NOT overlap. "
        + "The combined hotel nights cover approximately 7 days. "
        + "The Cologne hotel check-in aligns with the arrival from Tokyo";

    public const string ConfirmationCodes =
        "The response contains at least two booking confirmation codes with a structured "
        + "format (e.g. CONF-AE-205-XXXXX or HTL-TOK-XXXXX). "
        + "Vague phrases like 'booking confirmed' without a reference code are insufficient";

    public const string CostPlausibility =
        "The response includes a cost breakdown with at least three distinct dollar amounts "
        + "(e.g. per-flight price, hotel nightly rate, trip total). "
        + "The total must be plausible for international travel — not under $500 or absent";

    public const string SevenDayItinerary =
        "The response includes a structured day-by-day itinerary spanning all 7 days, "
        + "with each day assigned to a specific city and at least one activity or highlight "
        + "— no day is blank or labelled 'TBD'";

    public const string NoCancellations =
        "The response contains NO cancellation actions, refund processing, or voiding of "
        + "any booking. The user did not request a cancellation, so none should appear";

    // ── Single-agent–only criteria ─────────────────────────────────────────

    public const string EmailDelivery =
        "The response explicitly states that a trip summary or confirmation email was "
        + "sent to traveller@example.com. Any other email address or a statement like "
        + "'email will be sent' is a failure";

    public const string EndToEndCompleteness =
        "A traveller reading only this response would have everything needed to take the "
        + "trip: confirmed flight numbers or references, confirmed hotel names and dates, "
        + "key activities per city, and a total cost estimate. Nothing critical is left "
        + "as 'to be arranged' or 'contact us for details'";

    // ── Workflow-only criteria ─────────────────────────────────────────────

    public const string CrossAgentSynthesis =
        "The final output references specific flight numbers or confirmation codes AND "
        + "hotel names or confirmation codes that originated from the FlightReservation and "
        + "HotelReservation agents — not re-invented generic placeholders";

    public const string PublicationQuality =
        "The response reads as a polished, publication-ready travel document with clear "
        + "headings, professional tone, and complete information — not a raw data dump "
        + "or unformatted bullet list of tool outputs";

    // ── Stochastic top-3 (cost-efficient, maximum hypothesis signal) ──────

    /// <summary>
    /// Three criteria that most strongly differentiate single-agent from workflow
    /// architecture. Used in Eval04 and Eval05 to keep stochastic LLM costs low
    /// while maximising the signal-to-cost ratio.
    /// </summary>
    public static readonly string[] Stochastic =
    [
        BothFlightLegs,    // Most common single-agent failure; workflow enforces structurally
        HotelCompleteness, // Second most common; requires remembering both cities
        DateCoherence      // Hardest cross-context temporal check; single-agent struggles most
    ];

    // ── Full criteria arrays ───────────────────────────────────────────────

    /// <summary>Eval01 full criteria (single agent, 10 items).</summary>
    public static readonly string[] Eval01 =
    [
        Research, BothFlightLegs, HotelCompleteness, DateCoherence,
        ConfirmationCodes, EmailDelivery, CostPlausibility,
        SevenDayItinerary, NoCancellations, EndToEndCompleteness
    ];

    /// <summary>Eval02 full criteria (workflow, 10 items).</summary>
    public static readonly string[] Eval02 =
    [
        Research, BothFlightLegs, HotelCompleteness, DateCoherence,
        ConfirmationCodes, CrossAgentSynthesis, CostPlausibility,
        SevenDayItinerary, NoCancellations, PublicationQuality
    ];
}
