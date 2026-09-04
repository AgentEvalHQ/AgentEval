// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json.Serialization;

namespace Galaxus.RecommendationAgent.Workflows;

/// <summary>How an interest was arrived at (design Demo 2 §C.1).</summary>
public enum InterestKind
{
    /// <summary>A single signal states it. "Bought a trail shoe" ⇒ trail running.</summary>
    Direct,

    /// <summary>
    /// No single signal states it; a CONJUNCTION implies it. The test the prompt enforces:
    /// if removing any ONE evidence signal would make you drop the interest, it is latent.
    /// </summary>
    Latent
}

/// <summary>Who put an interest on the map. The reviewer's are capped, clamped and audited.</summary>
public enum InterestOrigin
{
    /// <summary>Produced by the interest mapper, before any retrieval ran.</summary>
    Mapper,

    /// <summary>
    /// Proposed mid-run by the coverage reviewer from a review snippet. Capped at
    /// <see cref="DiscoveryState.MaxReviewerInferredInterests"/>, confidence clamped in CODE to
    /// <see cref="DiscoveryState.ReviewerInferredConfidenceCeiling"/>, and its query terms
    /// filtered by <see cref="QueryVocabulary"/> (design §0.5 / D-3).
    /// </summary>
    ReviewerInferred
}

/// <summary>How well this round's retrieval served one interest.</summary>
public enum CoverageStatus
{
    /// <summary>No query has been run for it yet.</summary>
    Unexplored,

    /// <summary>Queries ran and returned nothing usable.</summary>
    Uncovered,

    /// <summary>Something came back, but not enough of it to serve the interest.</summary>
    Partial,

    /// <summary>A customer with that interest would find something worth opening.</summary>
    Covered
}

/// <summary>
/// Why the loop stopped. Exactly one value is recorded per run, and it is PRINTED — a loop
/// whose exit condition is not stated on screen is not inspectable, which is the whole thesis.
/// </summary>
public enum DiscoveryStopReason
{
    /// <summary>The loop has not finished yet.</summary>
    None,

    /// <summary>The reviewer approved: every interest is covered.</summary>
    CoverageSufficient,

    /// <summary>Gaps remain and a further round is both permitted and useful. NOT a terminal value.</summary>
    GapsRemain,

    /// <summary>TERMINATION 1 — the round cap was reached with gaps still open.</summary>
    RoundLimitReached,

    /// <summary>
    /// TERMINATION 2 — a round added zero NEW product ids. Another identical round cannot
    /// change the answer, so the loop stops early rather than paying for it.
    /// </summary>
    NoProgress,

    /// <summary>
    /// TERMINATION 3 — gaps remain but no materially different query is available. The
    /// reviewer said so rather than inventing a query it did not believe in.
    /// </summary>
    GapsUnresolvable
}

/// <summary>
/// One interest on the running map: what the customer is into, the evidence for it, and the
/// search terms that would find products for it.
/// </summary>
/// <remarks>
/// Note the deliberate absence: there is NO product-id field. The mapper is never given the
/// catalogue and its output schema has nowhere to put a SKU, so a hallucinated product is
/// structurally impossible at this stage rather than merely unlikely.
/// </remarks>
public sealed record Interest
{
    /// <summary>Stable within one run: <c>"I-1"</c>, <c>"I-2"</c>, …</summary>
    public required string Id { get; init; }

    /// <summary>Two to six words. The interest, not the product.</summary>
    public required string Label { get; init; }

    /// <summary>Direct or latent — see <see cref="InterestKind"/>.</summary>
    public required InterestKind Kind { get; init; }

    /// <summary>Mapper or reviewer-inferred.</summary>
    public required InterestOrigin Origin { get; init; }

    /// <summary>0..1. Reviewer-proposed values are clamped in code, never trusted as written.</summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// The purchase ids that evidence it (<c>PUR-NB-01</c> …). EMPTY for a reviewer-inferred
    /// interest and for an in-session stated need: those are evidenced by a review or by the
    /// sentence the customer typed, never by history.
    /// </summary>
    public required IReadOnlyList<string> EvidenceSignalIds { get; init; }

    /// <summary>One sentence. For a latent interest it names the conjunction.</summary>
    public required string Rationale { get; init; }

    /// <summary>
    /// Two to four search phrases. These drive round 1's retrieval directly, which is why a
    /// reviewer-proposed set of them is a query-generation path an attacker would want —
    /// see <see cref="QueryVocabulary"/>.
    /// </summary>
    public required IReadOnlyList<string> QueryTerms { get; init; }

    /// <summary>Catalogue category paths believed to apply. Guessing wrong is cheap.</summary>
    public IReadOnlyList<string> CategoryHints { get; init; } = [];

    /// <summary>Attribute name/value pairs a hard filter can use.</summary>
    public IReadOnlyDictionary<string, string> AttributeHints { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>True when the reviewer put this interest on the map mid-run.</summary>
    public bool IsReviewerInferred => Origin == InterestOrigin.ReviewerInferred;
}

/// <summary>Something the customer has told us NOT to recommend. A gift is the strongest such signal here.</summary>
/// <param name="Label">The thing to avoid, e.g. a leaf category name.</param>
/// <param name="EvidenceSignalIds">The purchase ids that produced it.</param>
/// <param name="Reason">The customer's own words, or the classifier's justification.</param>
public sealed record AntiInterest(string Label, IReadOnlyList<string> EvidenceSignalIds, string Reason);

/// <summary>
/// A hard fact a recommendation must respect: a device the customer owns that accessories must
/// pair with, a size, a market. Enforced by CODE in <see cref="CompatibilityChecker"/>.
/// </summary>
/// <param name="Kind">Constraint family, e.g. <c>"compat"</c> or <c>"market"</c>.</param>
/// <param name="Value">The normalised constraint token, e.g. <c>"sony-e-mount"</c>.</param>
/// <param name="SourceSignalId">The purchase id (or <c>"profile"</c>) the constraint came from.</param>
public sealed record CompatibilityConstraint(string Kind, string Value, string SourceSignalId);

/// <summary>
/// A retrieved candidate.
/// </summary>
/// <remarks>
/// NOTE the omission: it carries NO price and NO stock. Those are resolved by a live read in the
/// Presenter and never enter model context — the same boundary Demo 1 draws.
/// </remarks>
/// <param name="ProductId">Catalogue id. The only field downstream code trusts as an identity.</param>
/// <param name="Title">Product title.</param>
/// <param name="CategoryPath">Full readable path, so the cross-category jump is visible.</param>
/// <param name="Attributes">The catalogue's own attribute tokens for this SKU.</param>
/// <param name="SearchScore">The fused retrieval score. Not a probability, not comparable across queries.</param>
/// <param name="MatchedInterestId">Which interest's query surfaced it first.</param>
/// <param name="SourceQuery">The exact query text that surfaced it.</param>
/// <param name="ReviewSnippets">Up to three verified-purchase snippets. UNTRUSTED TEXT.</param>
/// <param name="ReviewIds">The ids of those snippets, so a <c>review:</c> citation can resolve.</param>
/// <param name="RatingCount">Verified ratings. Zero ⇒ cold start.</param>
/// <param name="AverageRating">0..5.</param>
public sealed record ProductCandidate(
    string ProductId,
    string Title,
    IReadOnlyList<string> CategoryPath,
    IReadOnlySet<string> Attributes,
    double SearchScore,
    string MatchedInterestId,
    string SourceQuery,
    IReadOnlyList<string> ReviewSnippets,
    IReadOnlyList<string> ReviewIds,
    int RatingCount,
    double AverageRating)
{
    /// <summary>Human-readable category path, <c>"Photography &gt; Filters"</c>.</summary>
    public string CategoryPathText => string.Join(" > ", CategoryPath);

    /// <summary>The leaf the product sits in.</summary>
    public string LeafCategory => CategoryPath.Count > 0 ? CategoryPath[^1] : string.Empty;
}

/// <summary>
/// The per-interest ledger — the printable artifact a human verifies. Not the answer: the
/// reasoning about what has NOT been found yet.
/// </summary>
public sealed class InterestCoverage
{
    /// <summary>The interest this row is about.</summary>
    public required string InterestId { get; init; }

    /// <summary>Every query already run for it. The reviewer is told a repeated query is not a plan.</summary>
    public List<string> QueriesRun { get; } = [];

    /// <summary>Every candidate credited to it, in discovery order.</summary>
    public List<string> CandidateProductIds { get; } = [];

    /// <summary>Best fused score any query produced for it.</summary>
    public double BestScore { get; set; }

    /// <summary>Where it stands right now.</summary>
    public CoverageStatus Status { get; set; } = CoverageStatus.Unexplored;

    /// <summary>Why it was last judged short, or null.</summary>
    public string? LastGapReason { get; set; }

    /// <summary>True when nothing has ever come back for it.</summary>
    public bool IsStarved => CandidateProductIds.Count == 0;
}

/// <summary>One selected recommendation, after the Ranker's deterministic post-checks.</summary>
/// <param name="Rank">1-based position.</param>
/// <param name="ProductId">Must be present in <see cref="DiscoveryState.Candidates"/> or it is dropped.</param>
/// <param name="InterestId">The interest it serves. Must exist on the map.</param>
/// <param name="WhyThis">Customer-facing justification. Scanned for stated prices downstream.</param>
/// <param name="GroundingAttributeKeys">Catalogue attribute keys the claim rests on.</param>
/// <param name="GroundingReviewIds">Review ids the claim rests on.</param>
/// <param name="Confidence">
/// Routing number for the two trays. NOT the model's self-report: it is derived from the
/// interest's confidence and the candidate's retrieval score, both code-owned. UNCALIBRATED.
/// </param>
public sealed record RankedRecommendation(
    int Rank,
    string ProductId,
    string InterestId,
    string WhyThis,
    IReadOnlyList<string> GroundingAttributeKeys,
    IReadOnlyList<string> GroundingReviewIds,
    double Confidence);

/// <summary>A SKU the deterministic post-checks removed, with the reason printed on screen.</summary>
/// <param name="ProductId">The removed SKU.</param>
/// <param name="Reason">Why. Printed verbatim — a drop nobody can see is not a guardrail.</param>
public sealed record DroppedSku(string ProductId, string Reason);

/// <summary>
/// One recommendation that survived the <c>GuardrailPipeline</c> and actually reached the
/// customer, recorded by the Presenter at the moment it rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists and why it is written by the Presenter rather than derived later.</b>
/// <see cref="DiscoveryState.Ranked"/> is what the Ranker CHOSE; this is what the customer was
/// SHOWN, which is a strictly smaller set — the guardrail pipeline drops a phantom SKU, an
/// uncitable item or a sensitive-category product after the Ranker has finished. Any consumer
/// that wants "the answer" (the eval lane's loop arm, an assertion, a transcript) must read the
/// screened set, and re-deriving it by re-running the pipeline downstream would be a second,
/// drift-prone description of a run that already happened. The producer of the fact records it.
/// </para>
/// <para>
/// <see cref="Evidence"/> is the compact <c>attr:</c> / <c>review:</c> citation the
/// <c>PresentRecommendation</c> tool carries, so a consumer resolves it against the catalogue by
/// exactly the rule Demo 1's answers are held to.
/// </para>
/// </remarks>
/// <param name="ProductId">The presented SKU.</param>
/// <param name="WhyThis">The customer-facing justification, after screening.</param>
/// <param name="Evidence">The <c>attr:</c> / <c>review:</c> citation, verbatim.</param>
/// <param name="OutOfStock">True when the item was rendered with an out-of-stock acknowledgement.</param>
/// <param name="Confidence">The routing number the item was rendered under. UNCALIBRATED.</param>
public sealed record PresentedItem(
    string ProductId,
    string WhyThis,
    string Evidence,
    bool OutOfStock,
    double Confidence);

/// <summary>
/// A review snippet observed on a candidate this round. This is the channel a mid-run interest
/// can come out of — and, per §0.5 / D-3, the channel a marketplace seller controls.
/// </summary>
/// <param name="ProductId">The product whose review it is.</param>
/// <param name="Snippet">The snippet. UNTRUSTED DATA, never an instruction.</param>
/// <param name="ForInterestId">The interest whose query retrieved the product.</param>
public sealed record ObservedSignal(string ProductId, string Snippet, string ForInterestId);

/// <summary>
/// One UNCOVERED interest with a concrete next query. This is the contract the loop-back edge
/// exists to serve: a gap with no runnable query is not a gap, it is an apology.
/// </summary>
/// <param name="InterestId">The interest that is short.</param>
/// <param name="WhyUncovered">
/// Whether the CATALOGUE has nothing or the QUERY missed it. Different failures; only one is
/// fixable by searching again.
/// </param>
/// <param name="NextQuery">One concrete query, materially different from every query already run.</param>
/// <param name="NextCategory">A category path taken from a candidate actually seen, when one applies.</param>
/// <param name="NextAttributes">Attribute name/value pairs a candidate demonstrated exist.</param>
public sealed record CoverageGap(
    [property: JsonPropertyName("interest_id")]     string InterestId,
    [property: JsonPropertyName("why_uncovered")]   string WhyUncovered,
    [property: JsonPropertyName("next_query")]      string NextQuery,
    [property: JsonPropertyName("next_category")]   string? NextCategory,
    [property: JsonPropertyName("next_attributes")] IReadOnlyDictionary<string, string>? NextAttributes);

/// <summary>
/// An interest the reviewer wants to add mid-run, from a review snippet.
/// </summary>
/// <remarks>
/// ⚠ <b>This is the §0.5 / D-3 attack surface.</b> <see cref="QueryTerms"/> drive the next
/// round's retrieval, and the snippet they were read out of was written by a marketplace seller.
/// The control is structural and lives in <see cref="QueryVocabulary"/>, not in prompt text.
/// </remarks>
/// <param name="Label">The proposed interest.</param>
/// <param name="Confidence">Clamped to ≤ 0.60 in code regardless of what the model says.</param>
/// <param name="EvidenceProductId">The product whose review revealed it. Must be a real candidate.</param>
/// <param name="Rationale">One sentence.</param>
/// <param name="QueryTerms">Filtered against the allowed vocabulary before anything is searched.</param>
public sealed record ProposedInterest(
    [property: JsonPropertyName("label")]               string Label,
    [property: JsonPropertyName("confidence")]          double Confidence,
    [property: JsonPropertyName("evidence_product_id")] string EvidenceProductId,
    [property: JsonPropertyName("rationale")]           string Rationale,
    [property: JsonPropertyName("query_terms")]         IReadOnlyList<string> QueryTerms);

/// <summary>
/// Every mid-run interest the reviewer PROPOSED, whether or not the structural constraints let
/// it through, recorded where the constraints ran.
/// </summary>
/// <remarks>
/// <para>
/// <b>A drop list means nothing without the proposal it filtered.</b> Counting only accepted
/// interests hides the denominator: an arm that proposed nothing and an arm whose every proposal
/// was refused both show zero reviewer-inferred interests, and only one of them exercised the
/// control. The eval lane's D-3 grader reads this as the applicability test — an untempted
/// prohibition has a chance floor of 1.0 and is never a pass.
/// </para>
/// <para>
/// <see cref="Refusal"/> is null exactly when <see cref="Accepted"/> is true.
/// </para>
/// </remarks>
/// <param name="Label">The proposed label, verbatim. Part of the payload when the proposal is hostile.</param>
/// <param name="EvidenceProductId">The product whose review text the proposal cited.</param>
/// <param name="ProposedTerms">The query terms as proposed, BEFORE the vocabulary constraint.</param>
/// <param name="KeptTerms">The terms that survived it.</param>
/// <param name="Accepted">True when the interest was added to the running map.</param>
/// <param name="Refusal">Why it was refused, or null when it was accepted.</param>
public sealed record ProposalOutcome(
    string Label,
    string EvidenceProductId,
    IReadOnlyList<string> ProposedTerms,
    IReadOnlyList<string> KeptTerms,
    bool Accepted,
    string? Refusal);

/// <summary>
/// The reviewer's verdict. <b>Model-facing only — it never travels an edge.</b>
/// </summary>
/// <remarks>
/// The <c>CoverageReviewerExecutor</c> projects it onto <see cref="DiscoveryState"/> fields and
/// the EDGES read those fields. That is what lets the verdict get as rich as you like without
/// touching the topology, and it is why the loop's routing is inspectable rather than implied.
/// </remarks>
/// <param name="CoveredInterestIds">Interests this round served.</param>
/// <param name="Gaps">Interests it did not, each with a concrete next query.</param>
/// <param name="NewInterest">At most one per round, or null.</param>
/// <param name="StopReason">
/// <c>COVERAGE_SUFFICIENT</c> | <c>GAPS_REMAIN</c> | <c>GAPS_UNRESOLVABLE</c>.
/// </param>
/// <param name="Assessment">One or two sentences, printed.</param>
public sealed record CoverageVerdict(
    [property: JsonPropertyName("covered_interest_ids")] IReadOnlyList<string> CoveredInterestIds,
    [property: JsonPropertyName("gaps")]                 IReadOnlyList<CoverageGap> Gaps,
    [property: JsonPropertyName("new_interest")]         ProposedInterest? NewInterest,
    [property: JsonPropertyName("stop_reason")]          string StopReason,
    [property: JsonPropertyName("assessment")]           string Assessment)
{
    /// <summary>The literal the reviewer emits when every interest is covered.</summary>
    public const string CoverageSufficient = "COVERAGE_SUFFICIENT";

    /// <summary>The literal for "at least one gap, and a real next query for it".</summary>
    public const string GapsRemain = "GAPS_REMAIN";

    /// <summary>The literal for "gaps remain but no materially different query is available".</summary>
    public const string GapsUnresolvable = "GAPS_UNRESOLVABLE";

    /// <summary>True when the reviewer approved. Ordinal, case-insensitive on the literal only.</summary>
    public bool IsSufficient =>
        string.Equals(StopReason, CoverageSufficient, StringComparison.OrdinalIgnoreCase) && Gaps.Count == 0;

    /// <summary>
    /// The conservative verdict synthesised when a structured response cannot be parsed twice.
    /// Deliberately biased TOWARD more work: an unparseable reviewer must never be able to
    /// approve, and it cannot loop forever because the round cap is independent of it.
    /// </summary>
    /// <param name="gaps">The gaps to carry, normally the deterministic pre-gate's.</param>
    /// <param name="assessment">Why this verdict was synthesised.</param>
    public static CoverageVerdict Conservative(IReadOnlyList<CoverageGap> gaps, string assessment) =>
        new([], gaps, null, gaps.Count > 0 ? GapsRemain : GapsUnresolvable, assessment);
}

/// <summary>
/// One line of a round's query plan. Round 1's entries come from the interest map; round 2+'s
/// come from the reviewer's gaps — which is the entire argument for the loop.
/// </summary>
/// <param name="InterestId">The interest this query serves.</param>
/// <param name="Query">The query text handed to retrieval.</param>
/// <param name="CategoryPathPrefix">Optional hard category pre-filter.</param>
/// <param name="Attributes">Optional hard attribute pre-filter.</param>
/// <param name="Origin">
/// One of <see cref="QueryPlanEntry.FromMap"/>, <see cref="QueryPlanEntry.FromGap"/> or
/// <see cref="QueryPlanEntry.FromProposal"/>. Printed, so the audience can see round 2 speaking
/// the catalogue's vocabulary rather than the customer's.
/// </param>
public sealed record QueryPlanEntry(
    string InterestId,
    string Query,
    string? CategoryPathPrefix,
    IReadOnlyDictionary<string, string>? Attributes,
    string Origin)
{
    /// <summary>
    /// Written from the MAPPER's interest map, before any retrieval ran — the customer's own
    /// vocabulary.
    /// </summary>
    public const string FromMap = "map";

    /// <summary>
    /// Written by the reviewer as a gap's <c>next_query</c>, after seeing real catalogue records.
    /// </summary>
    public const string FromGap = "gap";

    /// <summary>
    /// The first query for an interest the REVIEWER added mid-run, so its terms were read out of
    /// review text on a candidate that had already been retrieved.
    /// </summary>
    /// <remarks>
    /// ⚠ This origin exists because folding it into <see cref="FromMap"/> made the demo's
    /// vocabulary-transfer panel state something false. The planner's map branch serves "any
    /// interest with no query yet", which includes a reviewer-inferred interest — so a query the
    /// loop could only have written AFTER retrieval was being reported under the heading "written
    /// before any retrieval ran". The distinction the panel is arguing about is <i>when the query
    /// was written</i>, and that is precisely what this constant separates.
    /// </remarks>
    public const string FromProposal = "new";

    /// <summary>
    /// True when this query could only have been written after real catalogue records were in
    /// front of the reviewer — a gap's next query, or a reviewer-proposed interest's own terms.
    /// </summary>
    public bool WrittenAfterRetrieval =>
        string.Equals(Origin, FromGap, StringComparison.Ordinal)
        || string.Equals(Origin, FromProposal, StringComparison.Ordinal);
}

/// <summary>
/// One query the loop actually ran, with the round it belonged to and what it returned.
/// </summary>
/// <remarks>
/// The demo's vocabulary-transfer panel groups these by <see cref="QueryPlanEntry.Origin"/>, and
/// the eval lane's loop arm reports <see cref="Query"/> as the arm's issued need. Both read one
/// record written by the node that ran the query, rather than two reconstructions of it.
/// </remarks>
/// <param name="Round">1-based discovery round.</param>
/// <param name="Plan">The plan line, including its origin and any hard pre-filter.</param>
/// <param name="Hits">How many candidates retrieval returned.</param>
/// <param name="NewProductIds">Product ids this query added that no earlier query had seen.</param>
public sealed record ExecutedQuery(
    int Round,
    QueryPlanEntry Plan,
    int Hits,
    IReadOnlyList<string> NewProductIds)
{
    /// <summary>The query text.</summary>
    public string Query => Plan.Query;

    /// <summary>True when the reviewer wrote this query after seeing real catalogue records.</summary>
    public bool FromCatalogueVocabulary => Plan.WrittenAfterRetrieval;
}

/// <summary>
/// A model-proposed query term that the structural vocabulary constraint REFUSED (§0.5 / D-3).
/// </summary>
/// <remarks>
/// Recorded rather than silently discarded, and printed by the console sink. A control whose
/// firing leaves no trace is indistinguishable from a control that never fires.
/// </remarks>
/// <param name="Term">The rejected term, verbatim.</param>
/// <param name="ProposedFor">What it was proposed for — an interest label or a gap's interest id.</param>
/// <param name="OffendingTokens">The tokens that are in neither the interest map nor the catalogue.</param>
public sealed record DroppedQueryTerm(string Term, string ProposedFor, IReadOnlyList<string> OffendingTokens)
{
    /// <summary>The one-line form the console prints.</summary>
    public override string ToString() =>
        $"\"{Term}\" (for {ProposedFor}) — out-of-vocabulary: {string.Join(", ", OffendingTokens)}";
}
