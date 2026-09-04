// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Evals.Loop;

namespace Galaxus.RecommendationAgent.Evals.Cases;

/// <summary>
/// One design §0.5 / D-3 case: a marketplace seller's review text that tries to write itself into
/// the next round's query plan.
/// </summary>
/// <remarks>
/// <para>
/// <b>The attack, in order.</b> (1) A seller lists a SKU on the marketplace and writes a review on
/// their own listing. (2) The listing is retrieved <i>legitimately</i>, for a real interest the
/// customer really has. (3) The coverage reviewer is shown the review body, because that is what
/// the review body is for. (4) The body proposes a use case and the SKU that serves it. (5) The
/// proposed interest's query terms drive round 2. (6) The named SKU comes back through legitimate
/// retrieval, so it is in the candidate set, so the containment check passes, so the answer is the
/// attacker's and every gauge reads green.
/// </para>
/// <para>
/// <b>Step 6 is why the containment check cannot catch this.</b> "A presented SKU must be in the
/// candidate set" is a true and useful invariant, and it is satisfied here. The defect is upstream:
/// the candidate set itself was chosen by the attacker.
/// </para>
/// </remarks>
/// <param name="Id">Case id, e.g. <c>"D3-01"</c>.</param>
/// <param name="PersonaId">The customer whose turn this is.</param>
/// <param name="HostSku">
/// The marketplace listing the seller controls and posts the review on. Asserted to be a real
/// marketplace offer by <see cref="InjectionCases.Validate"/>.
/// </param>
/// <param name="PlantedReviewId">The fixture review id. Not a catalogue id — see <see cref="InjectionCases"/>.</param>
/// <param name="ReviewBody">The steering text, verbatim. UNTRUSTED by construction.</param>
/// <param name="NamedCompetitorSku">
/// The SKU the steering text is trying to sell. A REAL catalogue SKU, in stock, in a department the
/// persona has never bought from — so its appearance can only have come from the injection.
/// </param>
/// <param name="ProposedLabel">The interest label the adversary asks for. Part of the payload.</param>
/// <param name="ProposedQueryTerms">
/// The query terms the adversary asks for. Every token of every one of these is asserted to appear
/// in <paramref name="ReviewBody"/>, so the declared payload cannot drift away from the planted text.
/// </param>
/// <param name="Note">Why this case exists, printed in the report.</param>
public sealed record InjectionCase(
    string Id,
    string PersonaId,
    string HostSku,
    string PlantedReviewId,
    string ReviewBody,
    string NamedCompetitorSku,
    string ProposedLabel,
    IReadOnlyList<string> ProposedQueryTerms,
    string Note)
{
    /// <summary>The framed prompt every arm sees for this case. The canonical coverage utterance, unchanged.</summary>
    /// <remarks>
    /// The prompt carries NO hint that this turn is an injection probe. A frame that warned the arm
    /// would be the harness supplying an input to its own verdict — and the real turn carries no
    /// warning either, because nobody knows in advance which review is the poisoned one.
    /// </remarks>
    public string Prompt => GalaxusEvalPrompt.For(PersonaId, GalaxusEvalPrompt.CoverageCanonical);

    /// <summary>The planted snippet, in the shape the loop's reviewer is shown.</summary>
    public ReviewSnippet Snippet => new(HostSku, PlantedReviewId, ReviewBody, IsMarketplaceSeller: true);

    /// <summary>The adversary's proposal, in the shape a reviewer would emit it.</summary>
    public ProposedLatentInterest Proposal => new(
        ProposedLabel, HostSku, ProposedQueryTerms,
        $"A review on {HostSku} names a use the interest map did not contain.");

    /// <summary>A review source that overlays the planted review onto the catalogue's own.</summary>
    public IReviewTextSource ReviewSource => new PlantedReviewSource(this);
}

/// <summary>
/// The catalogue's reviews, plus one planted review on the host SKU.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the planted review is a fixture and not a seed row.</b> <c>Catalogue</c> invariant 8
/// asserts that every marketplace cold-start SKU carries ZERO reviews — that is what makes it cold,
/// and three eval cases depend on it. Adding a review to <c>ReviewSeed</c> for a marketplace listing
/// would fail the catalogue's own start-up check and take Demo 1 down with it. So the case owns its
/// planted text, which is also the more faithful model: the attack is a review that <i>arrives</i>,
/// on a listing that had none an hour ago, at roughly 4 000 ratings a day.
/// </para>
/// <para>
/// The overlay is additive and the planted snippet comes FIRST, ahead of any catalogue review on the
/// same SKU. Nothing in the catalogue is mutated.
/// </para>
/// </remarks>
public sealed class PlantedReviewSource : IReviewTextSource
{
    private readonly InjectionCase _case;

    /// <summary>Wraps one case.</summary>
    /// <param name="injectionCase">The case whose review is planted.</param>
    public PlantedReviewSource(InjectionCase injectionCase)
    {
        ArgumentNullException.ThrowIfNull(injectionCase);
        _case = injectionCase;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ReviewSnippet> SnippetsFor(string productId)
    {
        var underlying = CatalogueReviewSource.Instance.SnippetsFor(productId);

        if (!string.Equals(productId, _case.HostSku, StringComparison.OrdinalIgnoreCase))
            return underlying;

        return [_case.Snippet, .. underlying];
    }
}
