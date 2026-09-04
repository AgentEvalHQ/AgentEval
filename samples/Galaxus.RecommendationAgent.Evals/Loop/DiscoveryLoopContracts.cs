// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Loop;

/// <summary>
/// The seam Eval 02 and Eval 04 score a discovery loop through. Declared HERE, in the eval
/// project, on purpose — see the remarks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the interface lives in the eval project and not in the demo project.</b> Demo 2's
/// workflow is being written concurrently in <c>Galaxus.RecommendationAgent</c>. The eval project
/// references the demo project, never the other way round, so an interface declared here can be
/// implemented there by an adapter without the demo project taking a dependency on the evals. The
/// alternative — waiting for the loop type to exist before the eval can name it — would mean the
/// instrument is written after the thing it measures, which is how a bar ends up shaped like its
/// artifact.
/// </para>
/// <para>
/// <b>An arm is FIRST an <see cref="IEvaluableAgent"/>.</b> Everything Eval 02 grades goes through
/// <c>PresentedCall.FromToolUsage</c> over a real tool trace, so a loop arm has to emit
/// <c>PresentRecommendation</c> calls exactly like the live agent and the scripted controls do. The
/// telemetry below is ADDITIONAL — it is what a loop can say about itself that a single-shot arm
/// cannot, and it is the only channel through which the D-3 injection eval can see a drop.
/// </para>
/// <para>
/// ⚠ <b>Telemetry is a CLAIM by the arm, never a verdict.</b> Eval 04 computes the expected drop
/// set independently, from the case fixture and the corpus, and compares. An arm that reports an
/// empty drop list does not thereby pass; it fails, because the expected set is non-empty. Nothing
/// on this interface is allowed to be the sole input to its own pass/fail.
/// </para>
/// </remarks>
public interface IDiscoveryLoopArm : IEvaluableAgent
{
    /// <summary>The hard round cap. Printed beside the rounds actually taken.</summary>
    int MaxRounds { get; }

    /// <summary>
    /// True when this arm applies the §0.5 / D-3 structural constraint to reviewer-proposed query
    /// terms. Declared rather than inferred, so an arm that does not apply it cannot be read as one
    /// that had nothing to drop.
    /// </summary>
    bool AppliesQueryVocabularyConstraint { get; }

    /// <summary>
    /// What the most recent <see cref="IEvaluableAgent.InvokeAsync"/> did, or null before the first
    /// turn. Not thread-safe and not meant to be: one arm instance serves one turn.
    /// </summary>
    DiscoveryLoopTelemetry? LastRun { get; }
}

/// <summary>The frozen stop-reason vocabulary (design §B.6).</summary>
/// <remarks>
/// Constants, not an enum, for the same reason <c>GuardrailReasons</c> is: these strings are
/// printed, snapshotted and compared, and a silently renamed enum member is the drift that produced
/// design §0.5 / D-1.
/// </remarks>
public static class DiscoveryStopReasons
{
    /// <summary>The reviewer approved: every interest is covered.</summary>
    public const string CoverageSufficient = "coverage-sufficient";

    /// <summary>The round cap was reached with gaps still open.</summary>
    public const string RoundLimitReached = "round-limit-reached";

    /// <summary>A round added zero new product ids, so another identical round cannot help.</summary>
    public const string NoProgress = "no-progress";

    /// <summary>Gaps remain but no materially different query is available.</summary>
    public const string GapsUnresolvable = "gaps-unresolvable";

    /// <summary>Every reason, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [CoverageSufficient, RoundLimitReached, NoProgress, GapsUnresolvable];

    /// <summary>True when <paramref name="reason"/> is one of <see cref="All"/> (ordinal).</summary>
    /// <param name="reason">A candidate stop reason.</param>
    public static bool IsKnown(string? reason) =>
        reason is not null && All.Contains(reason, StringComparer.Ordinal);
}

/// <summary>
/// One query term a reviewer proposed and the loop refused to run — the ledger line design
/// §0.5 / D-3 requires to be RECORDED and PRINTED rather than merely not happening.
/// </summary>
/// <param name="Term">The normalised term, as the loop would have searched for it.</param>
/// <param name="ProposedForInterest">The reviewer-proposed interest label the term belonged to.</param>
/// <param name="SourceProductId">The product whose review text the proposal came from.</param>
/// <param name="Reason">One of the constants on this type.</param>
public sealed record QueryTermDrop(
    string Term,
    string ProposedForInterest,
    string SourceProductId,
    string Reason)
{
    /// <summary>
    /// The term is not in (interest map) ∪ (catalogue category names ∪ attribute and tag tokens).
    /// This is the ONLY reason the structural control produces, and it is a property of the TERM
    /// against the corpus, never a judgement about the text it came from.
    /// </summary>
    public const string OutsideVocabulary = "outside_query_vocabulary";

    /// <summary>A compact one-line rendering for the ledger panel.</summary>
    public override string ToString() =>
        $"⛔ \"{Term}\" — {Reason} (proposed for \"{ProposedForInterest}\" from {SourceProductId})";
}

/// <summary>
/// What one loop turn did: rounds, routing, the queries it ran, the interests it accepted, and
/// every term it refused to run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is recorded by the PRODUCER of the fact.</b> The queries are recorded where
/// they are issued, the drops where the constraint runs, the candidates at ingest. A telemetry
/// record assembled at the end from a summary would be a second, drift-prone description of the
/// run; this one is the run.
/// </para>
/// </remarks>
public sealed record DiscoveryLoopTelemetry
{
    /// <summary>Which arm produced this.</summary>
    public required string ArmName { get; init; }

    /// <summary>The customer the turn was for.</summary>
    public required string CustomerId { get; init; }

    /// <summary>Discovery rounds that completed. 1 means the loop never looped.</summary>
    public required int RoundsTaken { get; init; }

    /// <summary>The hard cap in force for this turn.</summary>
    public required int MaxRounds { get; init; }

    /// <summary>True when the exit was the reviewer's approval rather than a guard.</summary>
    public required bool ApprovedByReviewer { get; init; }

    /// <summary>One of <see cref="DiscoveryStopReasons"/>.</summary>
    public required string StopReason { get; init; }

    /// <summary>Every search need issued, in order, across every round.</summary>
    public required IReadOnlyList<string> QueriesRun { get; init; }

    /// <summary>Every distinct product id that entered the candidate set, in ingest order.</summary>
    public required IReadOnlyList<string> CandidateProductIds { get; init; }

    /// <summary>New product ids added by the LAST round. Zero is the no-progress stop.</summary>
    public required int LastRoundNewProductCount { get; init; }

    /// <summary>Interest labels the reviewer PROPOSED, whether or not they were accepted.</summary>
    public required IReadOnlyList<string> ProposedInterestLabels { get; init; }

    /// <summary>
    /// Every query term the reviewer proposed for a new interest, before the constraint ran. The
    /// denominator the D-3 grader needs: a drop list means nothing without the proposal it filtered.
    /// </summary>
    public required IReadOnlyList<string> ProposedQueryTerms { get; init; }

    /// <summary>Interest labels the loop actually ACCEPTED and searched from.</summary>
    public required IReadOnlyList<string> AcceptedInterestLabels { get; init; }

    /// <summary>The refusals. Empty on an arm that applies no constraint — which is a finding, not a pass.</summary>
    public required IReadOnlyList<QueryTermDrop> DroppedQueryTerms { get; init; }

    /// <summary>Whether the structural constraint ran at all this turn.</summary>
    public required bool VocabularyConstraintApplied { get; init; }

    /// <summary>Product ids the arm presented, in presentation order.</summary>
    public required IReadOnlyList<string> PresentedProductIds { get; init; }

    /// <summary>Untrusted review snippets the reviewer was shown, newest first.</summary>
    public required IReadOnlyList<ReviewSnippet> SnippetsSeen { get; init; }

    /// <summary>True when the loop took more than one round.</summary>
    public bool Looped => RoundsTaken > 1;

    /// <summary>A one-line summary for the console.</summary>
    public string ToSummaryLine() =>
        $"rounds {RoundsTaken}/{MaxRounds} · stop {StopReason} · queries {QueriesRun.Count} · "
      + $"candidates {CandidateProductIds.Count} · presented {PresentedProductIds.Count} · "
      + $"proposed {ProposedInterestLabels.Count} · dropped terms {DroppedQueryTerms.Count}"
      + (VocabularyConstraintApplied ? "" : " · ⚠ NO VOCABULARY CONSTRAINT");
}

/// <summary>
/// One piece of untrusted, customer-authored text the reviewer was shown, with the product it was
/// attached to.
/// </summary>
/// <param name="ProductId">The SKU the text is attached to.</param>
/// <param name="ReviewId">The review id, or a fixture id for a planted case.</param>
/// <param name="Text">The body. UNTRUSTED — quoted as evidence, never obeyed as an instruction.</param>
/// <param name="IsMarketplaceSeller">True when the SKU is sold by a marketplace seller rather than by Galaxus.</param>
public sealed record ReviewSnippet(string ProductId, string ReviewId, string Text, bool IsMarketplaceSeller);
