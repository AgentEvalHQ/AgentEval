// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text.Json.Serialization;

namespace Galaxus.RecommendationAgent.Domain;

/// <summary>
/// The complete answer for one customer turn: the derived interest map, the primary and
/// secondary recommendation trays, the replenishment lane, and — when the abstention gate
/// fired — the questions asked instead of a guess.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ READ THIS BEFORE WIRING ANYTHING TO IT. Per design §0.5 / D-1 this record is
/// <b>no longer parsed out of the assistant's final text</b>. The system prompt's
/// "return only this JSON object" contract is DELETED. The one sanctioned channel for a
/// recommendation is the <c>PresentRecommendation(sku, reason, evidence, outOfStock, userEvidence)</c>
/// TOOL CALL — see <see cref="PresentedRecommendation"/> — and this set is ASSEMBLED by
/// the tool layer from those calls, from the code-derived <see cref="InterestMap"/>, and
/// from the replenishment lane.
/// </para>
/// <para>
/// Why it matters: every eval check reads tool-call arguments, never prose. That turns
/// each check into a dictionary lookup instead of a regex over free text, which is the one
/// thing the deterministic eval strategy rests on. A recommendation written only in prose
/// is a defect by definition, not a near-miss.
/// </para>
/// <para>
/// The JSON property names below are retained because the guardrail ledger and the
/// <c>--log</c> transcript serialise this record for post-mortem diffing.
/// </para>
/// </remarks>
/// <param name="InterestMap">The code-derived signals, echoed so the answer is self-describing.</param>
/// <param name="Recommendations">Primary tray — confidence ≥ 0.70 after banding (§F.7).</param>
/// <param name="AlsoConsider">Secondary tray — confidence 0.45–0.69, plus out-of-stock demotions (§F.4).</param>
/// <param name="Replenishment">Consumables due for a repeat buy. NEVER surfaced as discovery.</param>
/// <param name="ClarifyingQuestions">Specific, answerable questions. Non-empty whenever <paramref name="Abstained"/> is true.</param>
/// <param name="Abstained">True when the gate fired and nothing was recommended.</param>
/// <param name="AbstainReason">Why, in one sentence. Null when <paramref name="Abstained"/> is false.</param>
public sealed record RecommendationSet(
    [property: JsonPropertyName("interest_map")]         IReadOnlyList<InterestSignalDto> InterestMap,
    [property: JsonPropertyName("recommendations")]      IReadOnlyList<RecommendationDto> Recommendations,
    [property: JsonPropertyName("also_consider")]        IReadOnlyList<RecommendationDto> AlsoConsider,
    [property: JsonPropertyName("replenishment")]        IReadOnlyList<ReplenishmentDto> Replenishment,
    [property: JsonPropertyName("clarifying_questions")] IReadOnlyList<string> ClarifyingQuestions,
    [property: JsonPropertyName("abstained")]            bool Abstained,
    [property: JsonPropertyName("abstain_reason")]       string? AbstainReason)
{
    /// <summary>
    /// Everything actually shown to the customer as a recommendation: primary tray then
    /// secondary tray, in that order. The replenishment lane is NOT included — it is not a
    /// discovery, and counting it as one is the "recommending the cartridges she has bought
    /// five times" failure (§B.3, Sofia).
    /// </summary>
    [JsonIgnore]
    public IEnumerable<RecommendationDto> AllPresented => Recommendations.Concat(AlsoConsider);

    /// <summary>Count of <see cref="AllPresented"/>. The denominator of the guardrail ledger's clean-rate.</summary>
    [JsonIgnore]
    public int PresentedCount => Recommendations.Count + AlsoConsider.Count;

    /// <summary>True when nothing at all was recommended — whether by abstention or because every item was dropped.</summary>
    [JsonIgnore]
    public bool IsEmpty => PresentedCount == 0 && Replenishment.Count == 0;

    /// <summary>An answer with nothing in it. Starting point for the assembler.</summary>
    public static RecommendationSet Empty { get; } =
        new([], [], [], [], [], Abstained: false, AbstainReason: null);

    /// <summary>
    /// The abstention answer (§F.8): no recommendations, a stated reason, and the questions
    /// asked instead of a guess.
    /// </summary>
    /// <remarks>
    /// An abstention is NOT automatically a pass. The eval must score an abstention on a
    /// case that HAD a right answer as a miss, or the gate becomes a way to score well by
    /// saying nothing — which is a broken instrument, not a cautious agent.
    /// </remarks>
    /// <param name="reason">Why the agent declined to guess.</param>
    /// <param name="questions">Two specific, answerable clarifying questions.</param>
    /// <param name="interestMap">The (thin) map that triggered the gate, echoed for the ledger.</param>
    public static RecommendationSet Abstain(
        string reason,
        IReadOnlyList<string> questions,
        IReadOnlyList<InterestSignalDto>? interestMap = null) =>
        new(interestMap ?? [], [], [], [], questions, Abstained: true, AbstainReason: reason);
}

/// <summary>
/// One recommendation, after the guardrail pipeline.
/// </summary>
/// <remarks>
/// Note the deliberate omission: NO price and NO stock field. The model is structurally
/// unable to state a price. Price and availability are attached at render time from
/// <c>CheckStockAndPrice</c> (§F.4), and any currency pattern found in
/// <paramref name="WhyThis"/> drops the item with <c>dropped(stated_price)</c>. This
/// mirrors Galaxus's own boundary — their shipped community AI is explicitly forbidden
/// from answering price questions.
/// </remarks>
/// <param name="ProductId">Must resolve in the catalogue, or the item is REMOVED, not down-ranked (§F.2).</param>
/// <param name="WhyThis">Two sentences, addressed to the customer, naming the trade-off. Scanned for prices.</param>
/// <param name="Evidence">The two-sided evidence. An item that cannot carry both sides is dropped (§F.3).</param>
/// <param name="Confidence">0..1, self-reported. CALIBRATION UNKNOWN — this is a routing heuristic, not a probability (§F.7).</param>
public sealed record RecommendationDto(
    [property: JsonPropertyName("product_id")]  string ProductId,
    [property: JsonPropertyName("why_this")]    string WhyThis,
    [property: JsonPropertyName("evidence")]    EvidenceDto Evidence,
    [property: JsonPropertyName("confidence")]  double Confidence);

/// <summary>
/// Two-sided by construction: one side points at the USER, the other at the PRODUCT.
/// Both sides are verified against the catalogue before render (§F.3). A recommendation
/// that cannot produce both sides is DROPPED, not down-ranked.
/// </summary>
/// <remarks>
/// The shape of this check matters: the artifact under test does not get to supply the bar
/// it is measured against. A model that invents a flattering spec value fails the check
/// HARDER, not softer.
/// </remarks>
/// <param name="UserSignalLabel">Must match a label present in the code-derived <see cref="InterestMap"/>.</param>
/// <param name="UserPurchaseIds">Non-empty, and every id must belong to THIS customer.</param>
/// <param name="ProductAttributeKey">Must exist in the product's <see cref="Product.Specs"/> or <see cref="Product.Tags"/>.</param>
/// <param name="ProductAttributeValue">Must EQUAL the catalogue value (ordinal, whitespace-normalised).</param>
/// <param name="ReviewId">Optional. When present, must exist and belong to that product.</param>
public sealed record EvidenceDto(
    [property: JsonPropertyName("user_signal_label")]       string UserSignalLabel,
    [property: JsonPropertyName("user_purchase_ids")]       IReadOnlyList<string> UserPurchaseIds,
    [property: JsonPropertyName("product_attribute_key")]   string ProductAttributeKey,
    [property: JsonPropertyName("product_attribute_value")] string ProductAttributeValue,
    [property: JsonPropertyName("review_id")]               string? ReviewId)
{
    /// <summary>
    /// The compact <c>attr:</c> / <c>review:</c> citation this evidence corresponds to —
    /// the form the <c>PresentRecommendation</c> tool's <c>evidence</c> argument carries,
    /// and the form the eval resolves (R-5). A review id, when present, wins: it is the
    /// stronger citation.
    /// </summary>
    [JsonIgnore]
    public EvidenceRef Citation =>
        ReviewId is { Length: > 0 } rid
            ? EvidenceRef.Review(rid)
            : EvidenceRef.Attribute(Product.NormalizeAttributeToken(ProductAttributeKey));
}

/// <summary>An item due for a repeat buy. Lives in its own tray and is never presented as a discovery.</summary>
/// <param name="ProductId">The consumable.</param>
/// <param name="DaysSinceLastPurchase">Elapsed days since the most recent order line.</param>
/// <param name="TypicalReplenishDays">The learned cadence from <see cref="Product.TypicalReplenishDays"/>.</param>
/// <param name="Because">The cadence justification, printed verbatim.</param>
public sealed record ReplenishmentDto(
    [property: JsonPropertyName("product_id")]                string ProductId,
    [property: JsonPropertyName("days_since_last_purchase")]  int DaysSinceLastPurchase,
    [property: JsonPropertyName("typical_replenish_days")]    int TypicalReplenishDays,
    [property: JsonPropertyName("because")]                   string Because)
{
    /// <summary>Days remaining until the item is due; negative when it is already overdue.</summary>
    [JsonIgnore]
    public int DaysUntilDue => TypicalReplenishDays - DaysSinceLastPurchase;

    /// <summary>True when the cadence says the customer has already run out.</summary>
    [JsonIgnore]
    public bool IsOverdue => DaysUntilDue < 0;
}

/// <summary>The wire form of an <see cref="InterestSignal"/>, without the internal evidence-kind tag.</summary>
/// <param name="Label">The natural-language interest.</param>
/// <param name="Strength">0..1.</param>
/// <param name="EvidencePurchaseIds">The purchases that produced it.</param>
public sealed record InterestSignalDto(
    [property: JsonPropertyName("label")]                  string Label,
    [property: JsonPropertyName("strength")]               double Strength,
    [property: JsonPropertyName("evidence_purchase_ids")]  IReadOnlyList<string> EvidencePurchaseIds)
{
    /// <summary>Projects a code-derived signal onto the wire shape.</summary>
    /// <param name="signal">The derived signal.</param>
    public static InterestSignalDto From(InterestSignal signal) =>
        new(signal.Label, signal.Strength, signal.EvidencePurchaseIds);
}

/// <summary>
/// The raw arguments of ONE <c>PresentRecommendation</c> tool call — the only sanctioned
/// recommendation channel (design §0.5 / D-1, eval contract R-4).
/// </summary>
/// <remarks>
/// <para>
/// Both lanes bind to this record: the tool layer constructs one per call and the eval
/// materialises one per <c>ToolCallRecord</c>. Because the tool is the channel, the
/// checks are dictionary lookups — <c>D1 PhantomSku</c> is
/// <c>!Catalogue.BySku.ContainsKey(Sku)</c>, <c>D2 StockClaim</c> is
/// <c>product.StockUnits == 0 &amp;&amp; !OutOfStock</c>, <c>D5 UnresolvableEvidence</c> is
/// <see cref="EvidenceRef.TryParse"/> followed by a set lookup. None of them touches prose.
/// </para>
/// </remarks>
/// <param name="Sku">The presented <see cref="Product.Id"/>.</param>
/// <param name="Reason">Customer-facing justification. Scanned for stated prices (§F.4).</param>
/// <param name="Evidence">A citation of the form <c>attr:&lt;token&gt;</c> or <c>review:&lt;id&gt;</c>.</param>
/// <param name="OutOfStock">Must be true when the SKU has zero stock, or defect class D2 fires.</param>
public sealed record PresentedRecommendation(
    string Sku,
    string Reason,
    string Evidence,
    bool OutOfStock = false)
{
    /// <summary>The parsed citation, or null when <see cref="Evidence"/> does not parse (which is itself a D5 defect).</summary>
    public EvidenceRef? Citation => EvidenceRef.TryParse(Evidence, out var r) ? r : null;
}

/// <summary>
/// The FROZEN argument names of the <c>PresentRecommendation</c> tool. The eval reads
/// arguments by name, so these strings are a contract, not an implementation detail —
/// renaming a parameter without changing the const here is exactly how the two lanes drifted
/// apart the first time (§0.5 / D-1).
/// </summary>
public static class PresentRecommendationArguments
{
    /// <summary>Argument name for the presented SKU.</summary>
    public const string Sku = "sku";

    /// <summary>Argument name for the customer-facing justification.</summary>
    public const string Reason = "reason";

    /// <summary>Argument name for the <c>attr:</c> / <c>review:</c> citation.</summary>
    public const string Evidence = "evidence";

    /// <summary>Argument name for the out-of-stock acknowledgement.</summary>
    public const string OutOfStock = "outOfStock";

    /// <summary>
    /// Argument name for the OPTIONAL user-side evidence — the customer signal the item is for,
    /// and the purchase ids that evidence it (§8.1 / B-5).
    /// </summary>
    /// <remarks>
    /// The fifth argument, and the only optional one. It was added to the tool without a constant
    /// here, which is precisely the drift this class exists to prevent: the tool defined
    /// <c>userEvidence</c>, the eval had no name to read it by, and the two lanes were one rename
    /// away from the §0.5 / D-1 failure with nothing to catch it. Control C-12 asserts the schema
    /// handed to the model names all five of these constants.
    /// </remarks>
    public const string UserEvidence = "userEvidence";
}

/// <summary>Which kind of catalogue fact an <see cref="EvidenceRef"/> points at.</summary>
public enum EvidenceRefKind
{
    /// <summary><c>attr:&lt;token&gt;</c> — resolves into <see cref="Product.Attributes"/>.</summary>
    Attribute,

    /// <summary><c>review:&lt;id&gt;</c> — resolves into <see cref="Product.ReviewIds"/>.</summary>
    Review
}

/// <summary>
/// A parsed evidence citation. The eval contract (R-5) requires every presented
/// recommendation to carry one of exactly two forms, and requires it to RESOLVE against
/// the catalogue record.
/// </summary>
/// <remarks>
/// <para>
/// This parser lives in <c>Domain</c> — shared by the agent and the eval project — on
/// purpose. The citation format is precisely the kind of contract that drifted between the
/// two lanes before, and a format owned by one side and re-implemented by the other is a
/// defect waiting to happen.
/// </para>
/// <para>
/// Resolution is NOT performed here: <see cref="Resolves"/> takes the product, so the bar
/// always comes from the catalogue and never from the artifact under test.
/// </para>
/// </remarks>
/// <param name="Kind">Attribute or review.</param>
/// <param name="Token">The normalised attribute token, or the review id verbatim.</param>
public readonly record struct EvidenceRef(EvidenceRefKind Kind, string Token)
{
    /// <summary>The <c>attr:</c> prefix, including the colon.</summary>
    public const string AttributePrefix = "attr:";

    /// <summary>The <c>review:</c> prefix, including the colon.</summary>
    public const string ReviewPrefix = "review:";

    /// <summary>Builds an attribute citation, normalising the token.</summary>
    /// <param name="token">A spec key, spec value, key=value pair, tag, or tag suffix.</param>
    public static EvidenceRef Attribute(string token) =>
        new(EvidenceRefKind.Attribute, Product.NormalizeAttributeToken(token));

    /// <summary>Builds a review citation. Review ids are compared verbatim (ordinal), never normalised.</summary>
    /// <param name="reviewId">A <see cref="Galaxus.RecommendationAgent.Domain.Review.Id"/> value.</param>
    public static EvidenceRef Review(string reviewId) =>
        new(EvidenceRefKind.Review, reviewId.Trim());

    /// <summary>
    /// Parses <c>attr:&lt;token&gt;</c> or <c>review:&lt;id&gt;</c>. Anything else fails —
    /// including a bare token with no prefix, and including an empty payload after the
    /// prefix. A parse failure IS defect class D5; it is not silently coerced.
    /// </summary>
    /// <param name="raw">The <c>evidence</c> tool argument, as written by the model.</param>
    /// <param name="reference">The parsed citation on success.</param>
    public static bool TryParse(string? raw, out EvidenceRef reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var text = raw.Trim();

        if (text.StartsWith(AttributePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var token = Product.NormalizeAttributeToken(text[AttributePrefix.Length..]);
            if (token.Length == 0) return false;
            reference = new EvidenceRef(EvidenceRefKind.Attribute, token);
            return true;
        }

        if (text.StartsWith(ReviewPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = text[ReviewPrefix.Length..].Trim();
            if (id.Length == 0) return false;
            reference = new EvidenceRef(EvidenceRefKind.Review, id);
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when this citation resolves against <paramref name="product"/>'s own catalogue
    /// record: an attribute token must be in <see cref="Product.Attributes"/>, a review id
    /// must be in <see cref="Product.ReviewIds"/>. Plausible prose cannot pass.
    /// </summary>
    /// <param name="product">The catalogue record for the presented SKU.</param>
    public bool Resolves(Product product) => Kind switch
    {
        EvidenceRefKind.Attribute => product.Attributes.Contains(Token),
        EvidenceRefKind.Review    => product.ReviewIds.Contains(Token),
        _                         => false
    };

    /// <summary>Round-trips to the wire form: <c>attr:&lt;token&gt;</c> or <c>review:&lt;id&gt;</c>.</summary>
    public override string ToString() =>
        Kind == EvidenceRefKind.Attribute ? AttributePrefix + Token : ReviewPrefix + Token;
}
