// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text;

namespace Galaxus.RecommendationAgent.Evals.Adapters;

/// <summary>
/// Gives the harness a SECOND TURN: when the agent's first turn presents nothing — the
/// instructed thin-signal behaviour is to stop and ask two clarifying questions — the harness
/// answers, from the persona's own profile and nothing else, and runs one more turn on the same
/// session. A run is silent only if it presents nothing AFTER that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists — a harness story that was being read as an agent story.</b>
/// <c>RecommendationInstructions</c> step 3 says: fewer than two independent signals and no need
/// described in this conversation ⇒ present nothing and ask exactly two questions. Jonas
/// (USR-JV-08) is four gaming purchases plus two gift lines; after the gift exclusion that is ONE
/// department, so the instruction fires and the single-turn harness recorded k = 0 on all three
/// reps of the 2026-09-04 live run — deterministically, as designed. Eval 02's GATE 1 then failed
/// "on JV-08 alone", Eval 09's clause 4 voided every verdict he touched, and Eval 01's C-08 (a
/// directly requested cuff for a monitor that is not on the account) had the same shape. None of
/// that measured the agent; it measured a harness that could not hold a conversation.
/// </para>
/// <para>
/// <b>What the reply is, and what it is not.</b> <see cref="ClarifyingAnswer.Compose"/> renders
/// the persona's profile — market, language, what they bought here and kept, what they bought
/// here as a gift, and the request they already made — through ONE constant template. It is
/// QUESTION-BLIND: it does not read the agent's questions, because reading them would let the
/// artifact under test steer its own input. It names no SKU, no category the customer has not
/// bought from, no gold token and no hint at what to do; everything in it the agent already had
/// through <c>GetUserProfile</c> / <c>GetPurchaseHistory</c>, except that it is now "described in
/// this conversation", which is the condition the instruction waits on. Under a simulated
/// personalization opt-out the history block is withheld: a customer who switched personalization
/// off does not narrate their order history to the assistant, and letting the harness do it for
/// them would hand the opt-out arm its history through the back door.
/// </para>
/// <para>
/// <b>Why the same session, not <c>InjectConversationHistory</c>.</b> <c>MAFAgentAdapter</c>
/// keeps one session for its whole life, so a second <c>InvokeAsync</c> on the same adapter is a
/// second turn of the same conversation with turn 1's tool calls and results in context — the
/// mechanism C-12's priming turn already relies on. <c>IHistoryInjectableAgent</c> would prepend
/// a user/assistant pair to the next call, bypassing the session and duplicating turn 1. The
/// second turn opens its OWN tool-budget scope (<see cref="EvalRuntime.BeginTurn"/>), because
/// that is what a second customer turn gets in production.
/// </para>
/// <para>
/// <b>When it fires.</b> Only when <see cref="AnswerRequired"/> is true — Eval 02 personas all
/// have gold; Eval 01 cases with <c>MinRecommendations ≥ 1</c> — AND turn 1 presented nothing.
/// A case whose gold permits silence (C-02, C-04, C-11 …) never gets a second turn: there, asking
/// is a correct answer and prodding would change the case. Both turns are recorded in
/// <see cref="LastOutcome"/> and the merged trace carries every tool call of both, so the graders
/// see exactly what the customer saw.
/// </para>
/// <para>
/// <b>Candidate for the library.</b> This is "answer an agent's clarifying question from a
/// fixture and keep going" — a harness policy, not a Galaxus fact. It lives here because
/// <c>src/</c> is being changed by another lane; the seam it would need in core is a hook on
/// <c>IEvaluationHarness</c> between "the agent answered" and "the trace is graded".
/// </para>
/// </remarks>
public sealed class ClarifyingTurnAdapter : IEvaluableAgent
{
    private readonly IEvaluableAgent _inner;
    private readonly CustomerProfile? _profileOverride;

    /// <summary>Wraps an evaluable agent that keeps a session across invocations.</summary>
    /// <param name="inner">The agent. Must keep its session between calls for the second turn to be a second turn.</param>
    /// <param name="answerRequired">
    /// True when the case's gold requires a presentation, so a silent first turn is answered.
    /// False when silence is a permitted answer, in which case this adapter is a pass-through.
    /// </param>
    /// <param name="profileOverride">
    /// The profile the reply is composed from, when the run has altered it — Eval 01's simulated
    /// opt-out. Null reads the seeded profile for the persona named in the prompt.
    /// </param>
    public ClarifyingTurnAdapter(IEvaluableAgent inner, bool answerRequired = true, CustomerProfile? profileOverride = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        AnswerRequired = answerRequired;
        _profileOverride = profileOverride;
    }

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <summary>Whether a silent first turn is answered at all.</summary>
    public bool AnswerRequired { get; }

    /// <summary>What happened on the most recent invocation, or null before the first.</summary>
    public ClarifyingTurnOutcome? LastOutcome { get; private set; }

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        AgentResponse first = await _inner.InvokeAsync(prompt, cancellationToken).ConfigureAwait(false);

        int presentedFirst = CountPresented(first);
        int toolCallsFirst = ToolUsageExtractor.Extract(first.RawMessages).Count;
        bool askedQuestion = first.Text?.Contains('?') == true;

        if (!AnswerRequired || presentedFirst > 0)
        {
            LastOutcome = new ClarifyingTurnOutcome(
                SecondTurnRan: false, PresentedAfterFirstTurn: presentedFirst, PresentedAfterSecondTurn: presentedFirst,
                FirstTurnAskedQuestion: askedQuestion, FirstTurnToolCalls: toolCallsFirst, SecondTurnToolCalls: 0,
                FirstTurnText: first.Text ?? string.Empty, Reply: null, SecondTurnText: null, SecondTurnThrew: false,
                Skipped: AnswerRequired ? null : "silence is a permitted answer on this case");
            return first;
        }

        string? personaId = ScriptedTrace.PersonaIdFrom(prompt);
        CustomerProfile? profile = _profileOverride ?? UserProfiles.Find(personaId);
        if (profile is null)
        {
            // No profile to answer from is a HARNESS limitation, recorded as such — never quietly
            // a silent agent.
            LastOutcome = new ClarifyingTurnOutcome(
                SecondTurnRan: false, PresentedAfterFirstTurn: 0, PresentedAfterSecondTurn: 0,
                FirstTurnAskedQuestion: askedQuestion, FirstTurnToolCalls: toolCallsFirst, SecondTurnToolCalls: 0,
                FirstTurnText: first.Text ?? string.Empty, Reply: null, SecondTurnText: null, SecondTurnThrew: false,
                Skipped: $"no authored profile for '{personaId ?? "(no id in prompt)"}' — the harness could not answer");
            return first;
        }

        string reply = ClarifyingAnswer.Compose(profile, GalaxusEvalPrompt.UtteranceFrom(prompt));

        AgentResponse second;
        try
        {
            // A fresh tool-budget scope: the second customer turn is a second turn, with the
            // budget a turn gets. The capture scope nests too and is restored on dispose.
            using (EvalRuntime.BeginTurn())
            {
                second = await _inner.InvokeAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            LastOutcome = new ClarifyingTurnOutcome(
                SecondTurnRan: true, PresentedAfterFirstTurn: 0, PresentedAfterSecondTurn: 0,
                FirstTurnAskedQuestion: askedQuestion, FirstTurnToolCalls: toolCallsFirst, SecondTurnToolCalls: 0,
                FirstTurnText: first.Text ?? string.Empty, Reply: reply, SecondTurnText: null, SecondTurnThrew: true,
                Skipped: null);
            throw;
        }

        var merged = Merge(first, second);
        int presentedTotal = CountPresented(merged);
        int toolCallsTotal = ToolUsageExtractor.Extract(merged.RawMessages).Count;

        LastOutcome = new ClarifyingTurnOutcome(
            SecondTurnRan: true, PresentedAfterFirstTurn: 0, PresentedAfterSecondTurn: presentedTotal,
            FirstTurnAskedQuestion: askedQuestion, FirstTurnToolCalls: toolCallsFirst,
            SecondTurnToolCalls: toolCallsTotal - toolCallsFirst,
            FirstTurnText: first.Text ?? string.Empty, Reply: reply, SecondTurnText: second.Text ?? string.Empty,
            SecondTurnThrew: false, Skipped: null);

        return merged;
    }

    /// <summary>
    /// The two turns as one response: every raw message of both, in order, so the extractor pairs
    /// each call with its own result and the graders read the whole conversation; the final text
    /// is the second turn's, because that is the answer the customer was left with; token usage is
    /// the SUM, because both turns were spent answering this one case.
    /// </summary>
    private static AgentResponse Merge(AgentResponse first, AgentResponse second)
    {
        var raw = new List<object>((first.RawMessages?.Count ?? 0) + (second.RawMessages?.Count ?? 0));
        if (first.RawMessages is not null) raw.AddRange(first.RawMessages);
        if (second.RawMessages is not null) raw.AddRange(second.RawMessages);

        TokenUsage? usage = first.TokenUsage is null && second.TokenUsage is null
            ? null
            : new TokenUsage
            {
                PromptTokens = (first.TokenUsage?.PromptTokens ?? 0) + (second.TokenUsage?.PromptTokens ?? 0),
                CompletionTokens = (first.TokenUsage?.CompletionTokens ?? 0) + (second.TokenUsage?.CompletionTokens ?? 0),
            };

        return new AgentResponse
        {
            Text = second.Text,
            RawMessages = raw,
            TokenUsage = usage,
            ModelId = second.ModelId ?? first.ModelId,
            FinishReason = second.FinishReason,
            AdditionalProperties = second.AdditionalProperties ?? first.AdditionalProperties,
        };
    }

    private static int CountPresented(AgentResponse response) =>
        PresentedCall.FromToolUsage(ToolUsageExtractor.Extract(response.RawMessages)).Count;
}

/// <summary>What one invocation through <see cref="ClarifyingTurnAdapter"/> did.</summary>
/// <param name="SecondTurnRan">True when the harness answered and a second turn ran (or was attempted).</param>
/// <param name="PresentedAfterFirstTurn">Presentations after turn 1.</param>
/// <param name="PresentedAfterSecondTurn">Presentations after the last turn that ran.</param>
/// <param name="FirstTurnAskedQuestion">True when turn 1's text contained a question mark.</param>
/// <param name="FirstTurnToolCalls">Tool calls in turn 1.</param>
/// <param name="SecondTurnToolCalls">Tool calls in turn 2, or 0.</param>
/// <param name="FirstTurnText">Turn 1's prose.</param>
/// <param name="Reply">The reply the harness sent, or null when no second turn ran.</param>
/// <param name="SecondTurnText">Turn 2's prose, or null.</param>
/// <param name="SecondTurnThrew">True when turn 2 threw; the exception propagated and the case is excluded, not scored.</param>
/// <param name="Skipped">Why no second turn ran when turn 1 was silent, or null.</param>
public sealed record ClarifyingTurnOutcome(
    bool SecondTurnRan,
    int PresentedAfterFirstTurn,
    int PresentedAfterSecondTurn,
    bool FirstTurnAskedQuestion,
    int FirstTurnToolCalls,
    int SecondTurnToolCalls,
    string FirstTurnText,
    string? Reply,
    string? SecondTurnText,
    bool SecondTurnThrew,
    string? Skipped)
{
    /// <summary>True when the run presented nothing even after the second turn — the only silence that is scored as silence.</summary>
    public bool SilentAfterSecondTurn => SecondTurnRan && !SecondTurnThrew && PresentedAfterSecondTurn == 0;

    /// <summary>One console line for the eval's per-cell trace.</summary>
    public string Describe() =>
        !SecondTurnRan
            ? Skipped is null
                ? $"turn 1 presented {PresentedAfterFirstTurn} — no second turn needed"
                : $"turn 1 presented 0 and NO second turn ran: {Skipped}"
            : SecondTurnThrew
                ? $"turn 1 presented 0 ({(FirstTurnAskedQuestion ? "asked" : "did not ask")}, {FirstTurnToolCalls} tool call(s)); the harness answered from the profile; turn 2 THREW"
                : $"turn 1 presented 0 ({(FirstTurnAskedQuestion ? "asked a question" : "asked nothing")}, {FirstTurnToolCalls} tool call(s)) → "
                + $"harness answered from the profile → turn 2 presented {PresentedAfterSecondTurn} ({SecondTurnToolCalls} tool call(s))";
}

/// <summary>
/// The deterministic customer reply: the persona's own profile, rendered through one constant
/// template. Question-blind by design — see <see cref="ClarifyingTurnAdapter"/>.
/// </summary>
public static class ClarifyingAnswer
{
    /// <summary>
    /// The first line of every reply. Constant, so a dry-run stub can recognise the second turn
    /// without reading anything else, and so the reply is greppable in a log.
    /// </summary>
    public const string OpeningLine =
        "To answer your questions — I will just tell you what is on my account, so you can work from that.";

    private static readonly IReadOnlyDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = "German", ["fr"] = "French", ["it"] = "Italian", ["en"] = "English",
        };

    /// <summary>
    /// Composes the reply.
    /// </summary>
    /// <remarks>
    /// Everything here is a corpus fact the agent already had access to: market and language from
    /// the user record; the purchase lines by product name, leaf category and date; which of them
    /// were gifts, read off the same three observables a customer would remember (wrapped, sent to
    /// another address, with a message) rather than off the classifier under test; and the
    /// request verbatim. It states that anything not listed was bought elsewhere — true for every
    /// authored persona, and the honest answer to "which model do you have?" for a product that is
    /// not on the account. Under the opt-out the history block is replaced by a refusal to narrate
    /// it, so the opt-out arm receives no history through this channel.
    /// </remarks>
    /// <param name="profile">The persona's profile, with the run's personalization flag.</param>
    /// <param name="utterance">The customer's own utterance from turn 1, repeated verbatim.</param>
    public static string Compose(CustomerProfile profile, string utterance)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(utterance);

        var catalogue = Catalogue.Default;
        var builder = new StringBuilder();
        builder.AppendLine(OpeningLine);
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"I shop in {profile.Market} and I read {LanguageNames.GetValueOrDefault(profile.Language, profile.Language)}.");

        if (!profile.PersonalizationEnabled)
        {
            builder.AppendLine("I have personalization switched off on purpose, so I am not going to list my order "
                             + "history here — please work from what I have told you.");
        }
        else
        {
            var kept = new List<string>();
            var gifts = new List<string>();

            foreach (var purchase in profile.Purchases.OrderBy(p => p.PurchasedOn).ThenBy(p => p.Id, StringComparer.Ordinal))
            {
                string name = catalogue.TryGet(purchase.ProductId, out var product) && product is not null
                    ? $"{product.Name} ({product.LeafCategory})"
                    : purchase.ProductId;
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"  · {name}, bought {purchase.PurchasedOn:yyyy-MM-dd}");

                bool gift = purchase.WasGiftWrapped && purchase.ShippedToAlternateAddress && purchase.HasGiftMessage;
                (gift ? gifts : kept).Add(line);
            }

            builder.AppendLine(kept.Count == 0
                ? "I have not bought anything here that I kept for myself."
                : "What I have bought here and kept for myself:");
            foreach (string line in kept) builder.AppendLine(line);

            if (gifts.Count > 0)
            {
                builder.AppendLine("Bought here but as gifts for other people, so not mine and not my interest:");
                foreach (string line in gifts) builder.AppendLine(line);
            }
        }

        builder.AppendLine("Anything else I own I bought somewhere else; it is not on this account and I do not have "
                         + "model numbers for it.");
        builder.AppendLine();
        builder.AppendLine("I do not have a particular product in mind beyond what I already said. My request is the "
                         + "one I made:");
        builder.AppendLine($"\"{utterance.Trim()}\"");
        builder.AppendLine("Please go ahead with what you can see here.");

        return builder.ToString().TrimEnd();
    }
}
