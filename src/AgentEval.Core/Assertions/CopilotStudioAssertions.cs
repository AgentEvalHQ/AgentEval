// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using Microsoft.Extensions.AI;

namespace AgentEval.Assertions;

/// <summary>
/// Fluent assertion sugar for Copilot Studio-flavored chat responses. Extension methods over
/// <see cref="ResponseAssertions"/> (plain response text) and a new <see cref="ChatResponseAssertions"/>
/// (plain <see cref="ChatResponse"/>) — never over <c>CopilotStudioChatClient</c> itself, so this file adds
/// zero dependency from <c>AgentEval.Core</c> onto <c>AgentEval.MAF.CopilotStudio</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not connector/topic/fallback assertions too.</b> A Copilot Studio agent's topic routing, connector
/// invocations, and flow actions happen server-side; <c>CopilotStudioChatClient.AsUpdates()</c> only ever
/// surfaces <c>Type == "message"</c> activity text — everything else is silently dropped by design (the
/// documented <c>SutTier.TextOnly</c> / <c>EvidenceFidelity.Verbal</c> fidelity ceiling). Building assertions
/// for data that isn't captured would mean inventing signal, which this repo's design refuses to do elsewhere
/// — see <c>CopilotStudioRedTeamTarget.WritePostScanSummary</c>'s own "proof of what was SAID, never what was
/// DONE" disclosure. Only what the bridge genuinely surfaces is asserted on below.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// response.Text.Should().HaveRespondedWithNonEmptyMessage();
/// response.Should().HaveContinuedConversation(firstTurnConversationId);
/// </code>
/// </example>
public static class CopilotStudioAssertions
{
    /// <summary>
    /// Assert the response text is non-empty, with a Copilot Studio-specific failure message that teaches the
    /// text-only fidelity ceiling instead of a generic empty-string complaint: a CS agent's reply can
    /// legitimately be empty when it responded with a non-text activity (adaptive card, suggested action),
    /// which the bridge silently drops rather than fabricates as text.
    /// </summary>
    /// <param name="a">The response assertions instance (e.g. <c>response.Text.Should()</c>).</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ResponseAssertions HaveRespondedWithNonEmptyMessage(this ResponseAssertions a, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrWhiteSpace(a.Response))
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected a non-empty Copilot Studio reply.",
                    expected: "Non-empty response text",
                    actual: a.Response.Length == 0 ? "empty string" : "whitespace only",
                    suggestions: new[]
                    {
                        "A Copilot Studio agent's reply can be empty when it responded with a non-text activity " +
                        "(adaptive card, suggested action) — the CS bridge is text-only by design " +
                        "(CopilotStudioChatClient.AsUpdates), so those are silently dropped, not an error.",
                        "If you expect richer output, this fidelity ceiling — not a bug — is why it isn't visible here.",
                    },
                    because: because));
        }

        return a;
    }

    /// <summary>
    /// Assert this response's <see cref="ChatResponse.ConversationId"/> matches <paramref name="previousConversationId"/> —
    /// proves the server-side Copilot Studio conversation actually resumed rather than silently starting a new
    /// one. This concept has no equivalent for a stateless Azure OpenAI call.
    /// </summary>
    /// <param name="a">The chat-response assertions instance (e.g. <c>response.Should()</c>).</param>
    /// <param name="previousConversationId">The conversation id a prior turn reported.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ChatResponseAssertions HaveContinuedConversation(this ChatResponseAssertions a, string previousConversationId, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(previousConversationId);
        var actual = a.Response.ConversationId;

        if (!string.Equals(actual, previousConversationId, StringComparison.Ordinal))
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected the Copilot Studio conversation to have continued.",
                    expected: $"ConversationId == \"{previousConversationId}\"",
                    actual: actual is null ? "null (no conversation id on this response)" : $"\"{actual}\"",
                    because: because));
        }

        return a;
    }

    /// <summary>
    /// Assert this response has a non-null, non-empty <see cref="ChatResponse.ConversationId"/> — a
    /// conversation was actually established server-side.
    /// </summary>
    /// <param name="a">The chat-response assertions instance (e.g. <c>response.Should()</c>).</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ChatResponseAssertions HaveStartedNewConversation(this ChatResponseAssertions a, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (string.IsNullOrEmpty(a.Response.ConversationId))
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected a new Copilot Studio conversation to have started.",
                    expected: "Non-empty ConversationId",
                    actual: "null or empty",
                    because: because));
        }

        return a;
    }

    /// <summary>
    /// Assert this response's <see cref="ChatResponse.ConversationId"/> is non-empty AND different from
    /// <paramref name="previousConversationId"/> — proves a genuinely NEW server-side conversation started,
    /// rather than the prior one silently resuming. Deliberately a differently-named method, not a
    /// <see cref="HaveStartedNewConversation(ChatResponseAssertions, string?)"/> overload: a single positional
    /// string argument is otherwise ambiguous between "the because reason" and "the previous conversation id"
    /// — C#'s default-argument tie-break silently resolves such a call to the no-previous-id overload instead
    /// of throwing a compile error, which is a real, hard-to-notice correctness trap, not just a style choice.
    /// </summary>
    /// <param name="a">The chat-response assertions instance (e.g. <c>response.Should()</c>).</param>
    /// <param name="previousConversationId">The conversation id a prior, different conversation reported.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ChatResponseAssertions HaveStartedDifferentConversation(this ChatResponseAssertions a, string previousConversationId, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(previousConversationId);
        var actual = a.Response.ConversationId;

        if (string.IsNullOrEmpty(actual) || string.Equals(actual, previousConversationId, StringComparison.Ordinal))
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected a new Copilot Studio conversation, distinct from the previous one.",
                    expected: $"Non-empty ConversationId != \"{previousConversationId}\"",
                    actual: string.IsNullOrEmpty(actual) ? "null or empty" : $"\"{actual}\" (same as previous)",
                    because: because));
        }

        return a;
    }

    /// <summary>
    /// Assert an estimated Copilot Credit spend stayed within a budget — the fluent counterpart to
    /// <c>--max-credits</c> enforcement, reading the SAME estimate
    /// (<c>CopilotStudioChatClient.EstimatedCreditsUsed</c>) that enforcement uses, not a parallel one.
    /// Takes the estimate as a value rather than the client itself, so this stays a plain-MEAI-type
    /// assertion with no dependency on the Copilot Studio package.
    /// </summary>
    /// <param name="a">The chat-response assertions instance (e.g. <c>response.Should()</c>).</param>
    /// <param name="estimatedCreditsUsed">The estimate to check (e.g. <c>copilotStudioClient.EstimatedCreditsUsed</c>).</param>
    /// <param name="maxCredits">The budget cap.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public static ChatResponseAssertions HaveStayedWithinCreditBudget(this ChatResponseAssertions a, int estimatedCreditsUsed, int maxCredits, string? because = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (estimatedCreditsUsed > maxCredits)
        {
            AgentEvalScope.FailWith(
                ResponseAssertionException.Create(
                    "Expected estimated Copilot Credit spend to stay within budget. Credit cost is an ESTIMATE, " +
                    "not a metered value — actual spend may already be ahead of this number.",
                    expected: $"estimated credits used <= {maxCredits}",
                    actual: $"estimated credits used == {estimatedCreditsUsed}",
                    because: because));
        }

        return a;
    }
}

/// <summary>
/// Begins fluent assertions on a plain <see cref="ChatResponse"/> (e.g. one returned by any <c>IChatClient</c>,
/// including <c>CopilotStudioChatClient</c>). Deliberately generic — see <see cref="CopilotStudioAssertions"/>
/// for the Copilot Studio-flavored extension methods built on top of it.
/// </summary>
public static class ChatResponseAssertionsExtensions
{
    /// <summary>Start fluent assertions on a <see cref="ChatResponse"/>.</summary>
    public static ChatResponseAssertions Should(this ChatResponse response) => new(response);
}

/// <summary>Fluent assertion builder wrapping a plain <see cref="ChatResponse"/>.</summary>
public sealed class ChatResponseAssertions
{
    /// <summary>The wrapped response.</summary>
    public ChatResponse Response { get; }

    /// <summary>Creates a new <see cref="ChatResponseAssertions"/> over <paramref name="response"/>.</summary>
    internal ChatResponseAssertions(ChatResponse response)
    {
        Response = response ?? throw new ArgumentNullException(nameof(response));
    }
}
