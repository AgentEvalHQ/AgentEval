// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Loop;

/// <summary>
/// The coverage gate of a discovery loop, as a seam. One round in, one verdict out.
/// </summary>
/// <remarks>
/// <para>
/// <b>The verdict never travels an edge (design §B.3).</b> It is projected onto the loop's state
/// and the ROUTING reads the state. That is why this returns a value object rather than mutating
/// anything: the reviewer decides, the loop routes, and the two can be tested apart.
/// </para>
/// <para>
/// <b>The reviewers in this project are deterministic stand-ins, not models.</b> Demo 2's real
/// reviewer is one structured model call. These are code, because what Eval 04 tests is not "would
/// a model propose this?" — it is "when something proposes this, does the structure contain it?".
/// A control that depended on a model's mood could not answer the second question, and the first
/// one is not a property of the architecture.
/// </para>
/// </remarks>
public interface ICoverageReviewer
{
    /// <summary>Short name, printed in the loop trace.</summary>
    string Name { get; }

    /// <summary>Judges one completed round.</summary>
    /// <param name="round">Everything the reviewer is allowed to see.</param>
    CoverageReviewVerdict Review(DiscoveryRound round);
}

/// <summary>
/// Everything a reviewer sees after one discovery round — and nothing else.
/// </summary>
/// <remarks>
/// It carries the ledger and the raw candidates, never the ranker's or the presenter's output.
/// Design §D.3's last row: the reviewer must not grade text produced by the system it is grading,
/// or the pass/fail input comes from the component under review.
/// </remarks>
/// <param name="CustomerId">The customer.</param>
/// <param name="RoundNumber">1-based.</param>
/// <param name="MaxRounds">The hard cap.</param>
/// <param name="InterestLabels">The running interest map, strongest first.</param>
/// <param name="CandidateCountByInterest">Per-interest candidate counts — the coverage ledger.</param>
/// <param name="CandidateProductIds">Every candidate accumulated so far, in ingest order.</param>
/// <param name="Snippets">Untrusted review text attached to this round's candidates.</param>
/// <param name="QueriesRun">Every need issued so far, so a repeated query can be recognised as not a plan.</param>
/// <param name="NewProductsThisRound">Product ids this round added that earlier rounds had not seen.</param>
public sealed record DiscoveryRound(
    string CustomerId,
    int RoundNumber,
    int MaxRounds,
    IReadOnlyList<string> InterestLabels,
    IReadOnlyDictionary<string, int> CandidateCountByInterest,
    IReadOnlyList<string> CandidateProductIds,
    IReadOnlyList<ReviewSnippet> Snippets,
    IReadOnlyList<string> QueriesRun,
    int NewProductsThisRound)
{
    /// <summary>Interest labels this round found nothing at all for — the deterministic pre-gate's input.</summary>
    public IReadOnlyList<string> StarvedInterests =>
    [
        .. InterestLabels.Where(label =>
            !CandidateCountByInterest.TryGetValue(label, out int count) || count == 0)
    ];
}

/// <summary>The reviewer's answer. Projected onto loop state by the caller; it never routes anything itself.</summary>
/// <param name="IsComplete">True ⇒ coverage is sufficient and the loop may exit.</param>
/// <param name="StopReason">
/// The reviewer's own words. ADVISORY: the loop owns the terminal stop reason, because the round
/// cap and the no-progress guard can override an approval the reviewer never withheld. Reading the
/// reviewer's string as the run's outcome would let the component under review name its own exit.
/// </param>
/// <param name="Gaps">Uncovered interests, each with a concrete next query.</param>
/// <param name="NewInterest">At most one proposed interest per round, or null.</param>
/// <param name="Assessment">One sentence, printed in the ledger.</param>
public sealed record CoverageReviewVerdict(
    bool IsComplete,
    string StopReason,
    IReadOnlyList<CoverageGapRequest> Gaps,
    ProposedLatentInterest? NewInterest,
    string Assessment)
{
    /// <summary>The reviewer's non-terminal marker: work remains and there is a query for it.</summary>
    public const string GapsRemain = "gaps-remain";

    /// <summary>The approving verdict, with no gaps and no proposal.</summary>
    /// <param name="assessment">One sentence for the ledger.</param>
    public static CoverageReviewVerdict Approve(string assessment) =>
        new(true, DiscoveryStopReasons.CoverageSufficient, [], null, assessment);
}

/// <summary>One uncovered interest and the concrete query that would fix it.</summary>
/// <param name="InterestLabel">The interest that went unserved.</param>
/// <param name="WhyUncovered">Catalogue-empty or query-missed — different failures, only one fixable by searching again.</param>
/// <param name="NextQuery">One concrete need, materially different from every query already run for this interest.</param>
public sealed record CoverageGapRequest(string InterestLabel, string WhyUncovered, string NextQuery);

/// <summary>
/// An interest the reviewer proposes from review text. <b>This is the D-3 channel</b> — the one
/// place where attacker-controlled text reaches query generation.
/// </summary>
/// <param name="Label">The proposed label. Part of the payload, so it is refused with the terms.</param>
/// <param name="SourceProductId">The product whose review revealed it. Required — an uncited proposal is refused.</param>
/// <param name="QueryTerms">The terms that would drive the next round's retrieval, verbatim, before the constraint.</param>
/// <param name="Rationale">One sentence, printed in the ledger.</param>
public sealed record ProposedLatentInterest(
    string Label,
    string SourceProductId,
    IReadOnlyList<string> QueryTerms,
    string Rationale);
