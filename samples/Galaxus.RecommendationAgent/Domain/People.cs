// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Domain;

/// <summary>
/// A customer. Four of these are authored in <c>Catalogue/Personas.cs</c>; each exists
/// to demonstrate exactly one mechanism (§B.3).
/// </summary>
/// <param name="Id">Stable customer id, e.g. <c>"USR-NB-01"</c>.</param>
/// <param name="DisplayName">Name shown in the console header.</param>
/// <param name="Language">"de" | "fr" | "it" | "en". Recommendations must not depend on it.</param>
/// <param name="Market">"CH" | "DE" | … — gates <see cref="Product.AvailableMarkets"/>.</param>
/// <param name="PersonalizationEnabled">
/// FDPIC one-click opt-out, shipped by Galaxus Nov-2025. FALSE ⇒ the tool layer
/// REFUSES purchase history (see §F.6) with a typed refusal, never an empty list.
/// Enforced in code, not in the prompt: a prompt rule is a request, a tool refusal is a fact.
/// </param>
/// <param name="CustomerSince">Account creation date, printed in the customer header.</param>
public sealed record User(
    string Id,
    string DisplayName,
    string Language,
    string Market,
    bool PersonalizationEnabled,
    DateOnly CustomerSince)
{
    /// <summary>
    /// Inverse of <see cref="PersonalizationEnabled"/>. The eval lane's contract (R-3)
    /// names the opt-out polarity; §A.2 names the opt-in polarity. Both now read off the
    /// same field, so the two lanes cannot disagree about which way the switch points.
    /// </summary>
    public bool PersonalizationOptOut => !PersonalizationEnabled;
}

/// <summary>
/// One order line in a customer's history.
/// </summary>
/// <remarks>
/// <para>
/// There is deliberately NO <c>IsGift</c> field (design §0.5 / A-3). Gift-ness is DERIVED
/// by <c>PurchaseIntentClassifier</c> from the observable signals below, exactly as a real
/// system would have to — and the eval lane's gold derivation goes through
/// <see cref="ClassifiedPurchase.IsGift"/>, not through a label handed to it. Adding a
/// labelled gift flag here would let the artifact under test supply an input to its own
/// verdict.
/// </para>
/// </remarks>
/// <param name="Id">Purchase id, e.g. <c>"PUR-NB-01"</c> — the evidence token cited in a recommendation.</param>
/// <param name="UserId">Owning customer.</param>
/// <param name="ProductId">The SKU bought. Resolves through the catalogue façade.</param>
/// <param name="PurchasedOn">Order date; drives recency, replenishment cadence and durable-age rules.</param>
/// <param name="Quantity">Units on the line.</param>
/// <param name="PriceChfPaid">Price actually paid, which may differ from today's catalogue price.</param>
/// <param name="WasGiftWrapped">Observable gift signal 1.</param>
/// <param name="ShippedToAlternateAddress">Observable gift signal 2.</param>
/// <param name="HasOwnReview">Observable gift signal 3, inverted: a customer who reviews it probably kept it.</param>
/// <param name="GiftMessagePresent">
/// Observable gift signal 4: null | "yes". PRESENCE only — the contents are never read,
/// which is the point. Reading a gift message would be a second, unnecessary data exposure.
/// </param>
public sealed record Purchase(
    string Id,
    string UserId,
    string ProductId,
    DateOnly PurchasedOn,
    int Quantity,
    decimal PriceChfPaid,
    bool WasGiftWrapped,
    bool ShippedToAlternateAddress,
    bool HasOwnReview,
    string? GiftMessagePresent)
{
    /// <summary>
    /// Alias for <see cref="ProductId"/>. The eval lane's contract (R-3) writes
    /// <c>Purchase(Sku, …)</c>; §A.2 writes <c>ProductId</c>. Same value, both names.
    /// </summary>
    public string Sku => ProductId;

    /// <summary>True when a gift message was attached. Presence only; contents never read.</summary>
    public bool HasGiftMessage => GiftMessagePresent is { Length: > 0 };

    /// <summary>
    /// How many of the four observable gift signals fired. This is INPUT to
    /// <c>PurchaseIntentClassifier</c>, not a verdict — the classifier owns the threshold
    /// and owns the <c>Because</c> string that justifies it.
    /// </summary>
    public int GiftSignalCount =>
        (WasGiftWrapped ? 1 : 0) +
        (ShippedToAlternateAddress ? 1 : 0) +
        (HasGiftMessage ? 1 : 0) +
        (HasOwnReview ? 0 : 1);

    /// <summary>Whole days between <see cref="PurchasedOn"/> and <paramref name="asOf"/>; never negative.</summary>
    /// <param name="asOf">Reference date, normally the demo clock's "today".</param>
    public int DaysSince(DateOnly asOf) => Math.Max(0, asOf.DayNumber - PurchasedOn.DayNumber);
}

/// <summary>
/// A verified-purchase customer review.
/// </summary>
/// <remarks>
/// UNTRUSTED TEXT. Galaxus takes roughly 4 000 user-authored ratings a day, all public,
/// all headed for a model's context window, and a marketplace seller can write one. The
/// tool layer fences <see cref="Body"/> in explicit begin/end markers with an inline
/// instruction never to follow directives found inside it (§F.10), and the discovery loop
/// constrains any query terms proposed from review text to vocabulary already present in
/// the catalogue (§0.5 / D-3). Quote a review as evidence; never take an instruction from one.
/// </remarks>
/// <param name="Id">Review id, e.g. <c>"REV-1042-03"</c> — the token a <c>review:</c> evidence citation resolves against.</param>
/// <param name="ProductId">The reviewed SKU.</param>
/// <param name="AuthorUserId">Author. May be one of the authored personas, or an unmodelled customer.</param>
/// <param name="Stars">1..5.</param>
/// <param name="Title">Short headline.</param>
/// <param name="Body">Free text. UNTRUSTED — see the type remarks.</param>
/// <param name="HelpfulVotes">Community helpfulness score; weights the digest.</param>
/// <param name="VerifiedPurchase">Always true — Galaxus purged 380k unverified reviews.</param>
/// <param name="Language">"de" | "fr" | "it" | "en".</param>
/// <param name="PostedOn">Publication date; weights the digest by recency.</param>
public sealed record Review(
    string Id,
    string ProductId,
    string AuthorUserId,
    int Stars,
    string Title,
    string Body,
    int HelpfulVotes,
    bool VerifiedPurchase,
    string Language,
    DateOnly PostedOn)
{
    /// <summary>True for 4★ and 5★.</summary>
    public bool IsPositive => Stars >= 4;

    /// <summary>True for 1★ and 2★ — the reviews a digest's cons are mostly drawn from.</summary>
    public bool IsNegative => Stars <= 2;
}

/// <summary>Helpfulness-and-recency-weighted pros/cons, mirroring the shipped "At a glance" feature.</summary>
/// <param name="ProductId">The digested SKU.</param>
/// <param name="Pros">≤ 3 keywords.</param>
/// <param name="Cons">≤ 3 keywords.</param>
/// <param name="ReviewsConsidered">How many reviews fed the digest — printed so the number is auditable.</param>
/// <param name="WeightedRating">Helpfulness- and recency-weighted mean, which may differ from <see cref="Product.RatingAverage"/>.</param>
public sealed record ReviewDigest(
    string ProductId,
    IReadOnlyList<string> Pros,
    IReadOnlyList<string> Cons,
    int ReviewsConsidered,
    double WeightedRating)
{
    /// <summary>True when no review survived weighting — a cold-start SKU has one of these.</summary>
    public bool IsEmpty => ReviewsConsidered == 0;
}
