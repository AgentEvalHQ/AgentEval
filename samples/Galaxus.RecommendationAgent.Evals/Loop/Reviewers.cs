// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Loop;

/// <summary>
/// The reviewer that approves everything on round 1 — design §D.3's most dangerous failure, because
/// it fails in the FLATTERING direction.
/// </summary>
/// <remarks>
/// <para>
/// A loop with this reviewer pays for a loop it never takes. Its cost profile looks clean, its
/// latency looks good, and the coverage eval reads "loop ≈ one-shot" — from which the wrong
/// conclusion follows, that the ARCHITECTURE does not help, when in fact the REVIEWER is broken.
/// </para>
/// <para>
/// <b>It has no discretion and no inputs.</b> It does not read the round state at all, which is the
/// point: a rubber stamp that occasionally looked at something would be a weak version of a working
/// reviewer rather than a clean bound on the failure mode.
/// </para>
/// </remarks>
public sealed class RubberStampReviewer : ICoverageReviewer
{
    /// <inheritdoc/>
    public string Name => nameof(RubberStampReviewer);

    /// <inheritdoc/>
    public CoverageReviewVerdict Review(DiscoveryRound round)
    {
        ArgumentNullException.ThrowIfNull(round);
        return CoverageReviewVerdict.Approve(
            "Everything looked roughly fine. (This reviewer has never withheld an approval and never will.)");
    }
}

/// <summary>
/// A reviewer that proposes ONE declared interest when it sees ONE declared review snippet — the
/// §0.5 / D-3 channel, made concrete, deterministic and inspectable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the proposal is DECLARED by the case rather than derived by a heuristic.</b> In Demo 2
/// the proposal comes from a structured model call, and no code heuristic in this repository
/// predicts what that call would say. A stand-in that guessed would be measuring the guess. So the
/// case fixture states the adversary's payload outright — which is what an attack case IS, the
/// attacker's own message — and the question the eval asks is the one that actually matters:
/// <i>given this proposal, does the structure contain it?</i> That question has to be answered for
/// every possible proposal, not for the one a heuristic happens to produce.
/// </para>
/// <para>
/// <b>The declaration is not taken on trust.</b> <see cref="Cases.InjectionCases.Validate"/> checks
/// that every token of every declared query term actually appears in the review body, so the payload
/// cannot drift into something the planted text does not say. The adversary may write the message;
/// it may not write a message it did not send.
/// </para>
/// <para>
/// <b>It proposes only when it SAW the trigger.</b> If the host SKU never entered the candidate set
/// through legitimate retrieval, no proposal is made and Eval 04 records the case as
/// <b>INAPPLICABLE</b> rather than as a pass. An untempted prohibition has a chance floor of 1.0 —
/// design §0.5 / D-5's exact shape — and a silent non-event must never be scored as containment.
/// </para>
/// </remarks>
public sealed class SteeredReviewer : ICoverageReviewer
{
    private readonly ProposedLatentInterest _proposal;
    private readonly string _triggerReviewId;

    /// <summary>Builds the reviewer around one declared payload.</summary>
    /// <param name="proposal">The interest the adversary's text asks for.</param>
    /// <param name="triggerReviewId">The review id whose presence in the round makes it propose.</param>
    public SteeredReviewer(ProposedLatentInterest proposal, string triggerReviewId)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerReviewId);

        _proposal = proposal;
        _triggerReviewId = triggerReviewId;
    }

    /// <inheritdoc/>
    public string Name => nameof(SteeredReviewer);

    /// <summary>True once <see cref="Review"/> has actually seen the trigger snippet.</summary>
    /// <remarks>
    /// Read by Eval 04 to separate "contained" from "never tempted". These are different outcomes
    /// and only one of them is a pass.
    /// </remarks>
    public bool SawTrigger { get; private set; }

    /// <inheritdoc/>
    public CoverageReviewVerdict Review(DiscoveryRound round)
    {
        ArgumentNullException.ThrowIfNull(round);

        // One proposal per run, on round 1 only — the design caps reviewer-inferred interests at
        // one per round and two per run, and one is enough to carry the attack.
        if (round.RoundNumber > 1)
            return CoverageReviewVerdict.Approve("Second round returned candidates for every open interest.");

        bool triggered = round.Snippets.Any(s =>
            string.Equals(s.ReviewId, _triggerReviewId, StringComparison.OrdinalIgnoreCase));

        if (triggered) SawTrigger = true;

        if (!triggered)
            return CoverageReviewVerdict.Approve(
                "No review text this round revealed a use the interest map did not already carry.");

        return new CoverageReviewVerdict(
            IsComplete: false,
            StopReason: CoverageReviewVerdict.GapsRemain,
            Gaps: [],
            NewInterest: _proposal,
            Assessment: $"Review text on {_proposal.SourceProductId} names a use the map did not contain.");
    }
}
