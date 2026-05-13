// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

namespace AgentEval.TravelDemo;

/// <summary>
/// Canonical user prompt shared by every TravelDemo / TravelDemo.Evals
/// invocation (Demo01, Demo02, Eval01, Eval02, Eval04, Eval05). Centralised
/// here so each demo and eval exercises the agents against an IDENTICAL
/// problem statement — variance in scores then reflects agent / model
/// variance, not differences in how the question was phrased.
/// </summary>
/// <remarks>
/// <para>
/// The prompt contains four scope elements (cities, dates, origin, summary
/// channel) plus an explicit autonomy directive. The autonomy directive
/// addresses a real failure mode observed against gpt-5-chat: when the model
/// reaches a natural "shall I proceed?" beat, it writes a question to the
/// chat instead of calling the <c>GetUserConfirmation</c> tool, ending the
/// run with no bookings. Telling the user prompt "proceed autonomously"
/// nudges the model toward the tool path.
/// </para>
/// <para>
/// Some demos / evals use the workflow pipeline (no <c>SendConfirmation</c>
/// tool), in which case the email line is silently ignored by the Presenter
/// stage. The trade-off is intentional: identical prompts across all six
/// invocations matters more than a perfectly-targeted ask per demo.
/// </para>
/// </remarks>
public static class TravelDemoPrompt
{
    /// <summary>
    /// The single source of truth for the TravelDemo user prompt. Both
    /// <c>AgentEval.TravelDemo</c> demos and <c>AgentEval.TravelDemo.Evals</c>
    /// reference this constant — do not duplicate the string inline.
    /// </summary>
    public const string Canonical =
        "Plan a 7-day trip visiting both Tokyo and Cologne. " +
        "I need city information, flights between them, " +
        "and hotel bookings for each city. " +
        "I live in Zurich, leaving from this city and returning to it. " +
        "Please send a trip summary to traveller@example.com. " +
        "Proceed autonomously from start to finish: do not pause to ask me " +
        "for confirmation, use your available tools to handle every step, " +
        "and deliver the completed itinerary in a single response.";
}
