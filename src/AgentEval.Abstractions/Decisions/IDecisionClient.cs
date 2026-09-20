// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Decisions;

/// <summary>
/// The <b>decision-model transport</b>: hands a piece of application state plus one or more typed
/// questions to a structured decision model and returns one typed, probabilistic answer per question.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately <b>not</b> an <c>IChatClient</c>. A decision model (TypeSafe's Jev is the first;
/// ADR-033) does not generate assistant text — it takes <c>state + questions</c> and returns
/// <c>P(yes)</c> for a yes/no question, a distribution over alternatives for a choice, or a
/// probability-weighted position on an ordered scale. Forcing that through a chat abstraction would
/// hide the very thing that makes it useful to an evaluator: the probability itself, which the
/// generative judge lane (<see cref="AgentEval.Core.IEvaluator"/>) never exposes.
/// </para>
/// <para>
/// The sanctioned bridge from this transport into the unified eval tree is
/// <c>AgentEval.Evals.DecisionEval</c>, which wraps one yes/no question as an <c>IEval</c> leaf carrying
/// <c>Provenance.Type == "atomic-decision"</c>, the model the provider says answered, and the raw
/// probability under <c>Details.Dimensions["decision.probability_yes"]</c> so a threshold sweep or a
/// calibration run can re-read it later.
/// </para>
/// <para>
/// A transport failure (authentication, rate limit, malformed body) is thrown as a
/// <see cref="DecisionClientException"/> and must <b>not</b> be turned into a score — an eval that
/// could not ask its question has not measured anything.
/// </para>
/// </remarks>
public interface IDecisionClient
{
    /// <summary>Asks every question in <paramref name="request"/> about its state, in one round trip.</summary>
    /// <param name="request">The state and the keyed questions to ask about it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One answer per question, keyed exactly as the questions were.</returns>
    /// <exception cref="DecisionClientException">The provider refused or the reply was unusable.</exception>
    Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A decision request: the state to judge and the keyed questions to ask about it.</summary>
/// <param name="State">
/// The content to evaluate — a string, or any serialisable object (a record, an anonymous object, a
/// dictionary). The transport serialises it as JSON; it is never rendered into a prompt.
/// </param>
/// <param name="Questions">
/// The questions, keyed by an id of the caller's choosing. Answers come back under the same ids.
/// Batch independent questions about one state here rather than issuing one request per question.
/// </param>
/// <param name="Model">
/// Optional model override. <see langword="null"/> uses the transport's configured default. Pin a
/// versioned id in anything reproducible; a moving alias can change what answered between runs.
/// </param>
public sealed record DecisionRequest(
    object State,
    IReadOnlyDictionary<string, DecisionQuestion> Questions,
    string? Model = null)
{
    /// <summary>The state to judge; never <see langword="null"/>.</summary>
    public object State { get; } = State ?? throw new ArgumentNullException(nameof(State));

    /// <summary>The keyed questions; at least one, and no key may be blank.</summary>
    public IReadOnlyDictionary<string, DecisionQuestion> Questions { get; } = Validate(Questions);

    private static IReadOnlyDictionary<string, DecisionQuestion> Validate(IReadOnlyDictionary<string, DecisionQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count == 0)
            throw new ArgumentException("A decision request needs at least one question.", nameof(Questions));
        foreach (var (key, question) in questions)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Question ids must not be blank.", nameof(Questions));
            if (question is null)
                throw new ArgumentException($"Question '{key}' is null.", nameof(Questions));
        }
        return questions;
    }
}

/// <summary>A decision response: one answer per question asked, plus what answered and what it cost.</summary>
/// <param name="Model">
/// The model the provider reports as having answered — the RESOLVED id (e.g. a dated build behind a
/// <c>-latest</c> alias), which is what provenance should record, not the alias that was requested.
/// </param>
/// <param name="Answers">One answer per question id in the request. A missing answer is a protocol
/// error the transport throws on; it never reaches here as an absent key.</param>
/// <param name="Usage">Token usage and, when the provider bills the call itself, the cost it reported.</param>
public sealed record DecisionResponse(
    string Model,
    IReadOnlyDictionary<string, DecisionAnswer> Answers,
    DecisionUsage? Usage)
{
    /// <summary>The model the provider reports as having answered; never blank.</summary>
    public string Model { get; } = string.IsNullOrWhiteSpace(Model)
        ? throw new ArgumentException("The response must name the model that answered.", nameof(Model))
        : Model;

    /// <summary>The answers; never <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, DecisionAnswer> Answers { get; } = Answers ?? throw new ArgumentNullException(nameof(Answers));
}

/// <summary>Token usage for one decision call, and the provider-reported cost when there is one.</summary>
/// <param name="InputTokens">Prompt-side tokens the provider reported.</param>
/// <param name="OutputTokens">Answer-side tokens the provider reported.</param>
/// <param name="Cost">
/// The cost the PROVIDER reported for this call, in its own currency (USD for every provider known
/// today), or <see langword="null"/> when it reports none. Prefer this over any list-price estimate:
/// it is what was billed, not what a table says.
/// </param>
public sealed record DecisionUsage(long InputTokens, long OutputTokens, double? Cost = null)
{
    /// <summary>Prompt-side tokens; never negative.</summary>
    public long InputTokens { get; } = InputTokens >= 0 ? InputTokens : throw new ArgumentOutOfRangeException(nameof(InputTokens), InputTokens, "Token counts cannot be negative.");

    /// <summary>Answer-side tokens; never negative.</summary>
    public long OutputTokens { get; } = OutputTokens >= 0 ? OutputTokens : throw new ArgumentOutOfRangeException(nameof(OutputTokens), OutputTokens, "Token counts cannot be negative.");

    /// <summary>Provider-reported cost; finite and non-negative when set.</summary>
    public double? Cost { get; } = Cost is null || (double.IsFinite(Cost.Value) && Cost.Value >= 0)
        ? Cost
        : throw new ArgumentOutOfRangeException(nameof(Cost), Cost, "Cost must be a finite, non-negative number when set.");
}
