// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Domain;

/// <summary>
/// Why a customer bought a thing. Derived deterministically by
/// <c>PurchaseIntentClassifier</c> from observable signals on <see cref="Purchase"/> —
/// the model READS this classification, it never MAKES it.
/// </summary>
public enum PurchaseIntent
{
    /// <summary>Bought for themselves. Full interest weight.</summary>
    ForSelf,

    /// <summary>Bought for someone else. Weight 0.0 — it is a signal about a different person.</summary>
    Gift,

    /// <summary>A repeat buy of a consumable on a stable cadence. Weight 0.15, routed out of discovery.</summary>
    Replenishment,

    /// <summary>A like-for-like replacement of something that wore out or broke.</summary>
    Replacement,

    /// <summary>The rules did not fire cleanly. Treated conservatively downstream.</summary>
    Unknown
}

/// <summary>A classification the model READS but does not MAKE. Carries its own justification.</summary>
/// <param name="Purchase">The order line being classified.</param>
/// <param name="Product">The resolved catalogue record for <see cref="Purchase.ProductId"/>.</param>
/// <param name="Intent">The derived intent.</param>
/// <param name="InterestWeight">Gift ⇒ 0.0; Replenishment ⇒ 0.15; ForSelf ⇒ 1.0.</param>
/// <param name="Because">
/// The human-readable justification, printed on screen — e.g. "gift-wrapped, alternate
/// address, no review, no follow-on accessory in 9 months". This string is what makes the
/// guardrail watchable rather than merely asserted, and it is the best twenty seconds of
/// the demo (§B.3, Marco).
/// </param>
public sealed record ClassifiedPurchase(
    Purchase Purchase,
    Product Product,
    PurchaseIntent Intent,
    double InterestWeight,
    string Because)
{
    /// <summary>
    /// The DERIVED gift verdict. Per §0.5 / A-3 the eval lane's gold set is computed
    /// through this property — never through a labelled field on <see cref="Purchase"/>,
    /// which deliberately has none.
    /// </summary>
    public bool IsGift => Intent == PurchaseIntent.Gift;

    /// <summary>True when the line is routed to the replenishment lane instead of discovery.</summary>
    public bool IsReplenishment => Intent == PurchaseIntent.Replenishment;

    /// <summary>True when the line contributes any weight at all to the interest map.</summary>
    public bool CountsTowardInterests => InterestWeight > 0.0;

    /// <summary>Convenience passthrough to <see cref="Domain.Purchase.Id"/> — the evidence token cited in a recommendation.</summary>
    public string PurchaseId => Purchase.Id;
}

/// <summary>
/// One derived interest. Natural-language label plus the purchase ids that evidence it;
/// the agent turns the label into a search need, and everything on either side of that
/// step is code.
/// </summary>
/// <param name="Label">e.g. "multi-day landscape photography on foot".</param>
/// <param name="Strength">0..1.</param>
/// <param name="EvidencePurchaseIds">The <see cref="Purchase.Id"/> values that produced this signal. Never empty.</param>
/// <param name="EvidenceKind">One of the <see cref="InterestEvidenceKinds"/> constants.</param>
public sealed record InterestSignal(
    string Label,
    double Strength,
    IReadOnlyList<string> EvidencePurchaseIds,
    string EvidenceKind)
{
    /// <summary>True when the signal clears <see cref="InterestMap.IndependentSignalThreshold"/>.</summary>
    public bool IsIndependent => Strength >= InterestMap.IndependentSignalThreshold;
}

/// <summary>
/// The frozen vocabulary for <see cref="InterestSignal.EvidenceKind"/>. Constants rather
/// than an enum because the kind is serialised into tool JSON and printed verbatim, and a
/// silently-renamed enum member is exactly the drift that produced §0.5 / D-1.
/// </summary>
public static class InterestEvidenceKinds
{
    /// <summary>Several purchases share a use-context; the conjunction is the signal.</summary>
    public const string CoPurchaseContext = "co-purchase-context";

    /// <summary>Repeated buying within one category branch.</summary>
    public const string CategoryDepth = "category-depth";

    /// <summary>The customer wrote a review, which is a stronger ownership signal than the order line alone.</summary>
    public const string ReviewAuthored = "review-authored";

    /// <summary>The customer said it in this conversation. The ONLY kind available when personalization is off.</summary>
    public const string StatedInSession = "stated-in-session";

    /// <summary>
    /// A required companion class is absent from the whole history — "owns whole beans and
    /// a storage canister but no grinder" (§B.3, Sofia). A collaborative filter cannot
    /// express the thing you are MISSING; it only knows what similar users bought.
    /// </summary>
    public const string CapabilityGap = "capability-gap";

    /// <summary>Every kind, in declaration order. Used to validate authored and generated signals.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        CoPurchaseContext,
        CategoryDepth,
        ReviewAuthored,
        StatedInSession,
        CapabilityGap
    ];

    /// <summary>True when <paramref name="kind"/> is one of <see cref="All"/> (ordinal).</summary>
    /// <param name="kind">A candidate <see cref="InterestSignal.EvidenceKind"/> value.</param>
    public static bool IsKnown(string? kind) =>
        kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// The code-derived view of one customer: what they are interested in, what was excluded
/// because it was bought for someone else, and what belongs in the replenishment lane
/// rather than in discovery.
/// </summary>
/// <param name="UserId">The customer this map describes.</param>
/// <param name="Signals">Derived interests, strongest first by convention.</param>
/// <param name="ExcludedBecauseGift">
/// <see cref="Purchase.Id"/> values the classifier ruled out as gifts. Printed on screen —
/// this is the guardrail the audience gets to WATCH fire.
/// </param>
/// <param name="RoutedToReplenishment"><see cref="Purchase.Id"/> values routed to the replenishment lane.</param>
/// <param name="PersonalizationEnabled">
/// False ⇒ the map was built from in-session statements alone, because the history never
/// reached the state (§F.6). Not "minimised in the prompt" — absent from it.
/// </param>
public sealed record InterestMap(
    string UserId,
    IReadOnlyList<InterestSignal> Signals,
    IReadOnlyList<string> ExcludedBecauseGift,
    IReadOnlyList<string> RoutedToReplenishment,
    bool PersonalizationEnabled)
{
    /// <summary>
    /// The strength a signal must reach to count toward the abstention gate. Exposed as a
    /// named constant so the gate, the renderer and the eval all read the same number
    /// instead of three copies of <c>0.35</c>.
    /// </summary>
    public const double IndependentSignalThreshold = 0.35;

    /// <summary>
    /// The minimum number of independent signals required before the agent may search at
    /// all (§F.8). Below it, and with no in-session stated need, the run abstains BEFORE
    /// the first search — a cheap structural check belongs before the model, not inside it.
    /// </summary>
    public const int MinimumSignalsToProceed = 2;

    /// <summary>Abstention gate input (§F.8). Gift-weighted signals do not count.</summary>
    public int IndependentSignalCount => Signals.Count(s => s.Strength >= IndependentSignalThreshold);

    /// <summary>True when the map alone carries enough evidence to justify searching.</summary>
    public bool HasEnoughSignal => IndependentSignalCount >= MinimumSignalsToProceed;

    /// <summary>
    /// True when the map ruled at least one purchase out as a gift — the condition under
    /// which the renderer prints the ⛔ exclusion line.
    /// </summary>
    public bool HasGiftExclusions => ExcludedBecauseGift.Count > 0;

    /// <summary>
    /// True when <paramref name="label"/> matches a signal actually present in this map.
    /// The user side of the two-sided evidence check (§F.3): a recommendation may only
    /// cite an interest the CODE derived, never one the model invented.
    /// </summary>
    /// <param name="label">The <c>user_signal_label</c> the model wrote.</param>
    public bool HasSignalLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var wanted = label.Trim();
        foreach (var s in Signals)
            if (string.Equals(s.Label.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Returns the signal whose label matches, or null. Same comparison as <see cref="HasSignalLabel"/>.</summary>
    /// <param name="label">The <c>user_signal_label</c> the model wrote.</param>
    public InterestSignal? FindSignal(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var wanted = label.Trim();
        foreach (var s in Signals)
            if (string.Equals(s.Label.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    /// <summary>An empty map for a customer with no usable history — the abstention path's input.</summary>
    /// <param name="userId">The customer.</param>
    /// <param name="personalizationEnabled">Whether history was even permitted to be read.</param>
    public static InterestMap Empty(string userId, bool personalizationEnabled) =>
        new(userId, [], [], [], personalizationEnabled);
}
