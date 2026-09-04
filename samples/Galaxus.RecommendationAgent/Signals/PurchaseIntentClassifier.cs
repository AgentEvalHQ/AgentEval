// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Signals;

/// <summary>
/// Decides, by deterministic rule and with no model call, WHY a customer bought a thing:
/// for themselves, as a gift, as a replenishment of a consumable, or as a like-for-like
/// replacement. Every verdict carries a human-readable <see cref="ClassifiedPurchase.Because"/>
/// that is printed on screen, so the guardrail can be WATCHED rather than merely asserted.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no <c>IsGift</c> field to read</b> (design §0.5 / A-3). <see cref="Purchase"/>
/// deliberately carries only the four OBSERVABLE signals a real order record would have —
/// gift wrap, an alternate shipping address, the presence of a gift message, and whether the
/// customer went on to review the item — and gift-ness is derived from them here. The eval
/// lane's gold set is computed through <see cref="ClassifiedPurchase.IsGift"/>, so the
/// artifact under test never supplies an input to its own verdict.
/// </para>
/// <para>
/// <b>Why this matters more than it looks.</b> Marco Iten's two most recent and most
/// expensive purchases are a games console and a game, both gift-wrapped to an alternate
/// address with a gift message and no review (§B.3). Every naive strategy — recency
/// weighting, value weighting, "similar to your last purchase", category affinity —
/// recommends a Pro Controller, and every one of them is confidently wrong, because Marco
/// does not own a console. The fix is this class, not a sentence in a prompt.
/// </para>
/// <para>
/// <b>Rule order is load-bearing</b> and is applied first-match-wins:
/// </para>
/// <list type="number">
///   <item>certain gift — at least <see cref="GiftSignalsForCertainty"/> of the four observable signals;</item>
///   <item>replenishment — a consumable, bought at least <see cref="ReplenishmentMinimumPurchases"/> times, on a stable cadence;</item>
///   <item>corroborated gift — exactly <see cref="GiftSignalsForCorroboration"/> signals, the follow-on window has fully elapsed, and no accessory followed;</item>
///   <item>ambiguous — two gift signals but the window is still open, or a consumable whose cadence is erratic ⇒ <see cref="PurchaseIntent.Unknown"/>;</item>
///   <item>replacement — a second purchase in the same leaf category after a long enough gap;</item>
///   <item>otherwise <see cref="PurchaseIntent.ForSelf"/>.</item>
/// </list>
/// <para>
/// Replenishment is checked BEFORE the corroborated-gift rule on purpose: a consumable
/// bought five times on a 92-day cadence is a replenishment even if one of those orders
/// happened to be gift-wrapped.
/// </para>
/// </remarks>
public static class PurchaseIntentClassifier
{
    // ── Weights (§A.3 fixes three of them; the other two are stated here) ─────────────

    /// <summary>Weight of a purchase the customer made for themselves. Full interest.</summary>
    public const double ForSelfWeight = 1.00;

    /// <summary>
    /// Weight of a like-for-like replacement. It CONFIRMS an existing interest rather than
    /// revealing a new one, so it counts, but for less than a first purchase.
    /// </summary>
    public const double ReplacementWeight = 0.60;

    /// <summary>
    /// Weight of an ambiguous line. Deliberately small: a purchase that MIGHT be a gift must
    /// not be able to create an interest strong enough to recommend on by itself.
    /// </summary>
    public const double UnknownWeight = 0.20;

    /// <summary>Weight of a consumable on a stable cadence (§A.3). Routed out of discovery.</summary>
    public const double ReplenishmentWeight = 0.15;

    /// <summary>Weight of a gift (§A.3). Zero — it is a signal about a DIFFERENT person.</summary>
    public const double GiftWeight = 0.00;

    // ── Thresholds ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// How many of <see cref="Purchase.GiftSignalCount"/>'s four observable signals must
    /// fire for gift-ness to be certain without corroboration. Marco's console fires all four.
    /// </summary>
    public const int GiftSignalsForCertainty = 3;

    /// <summary>
    /// How many signals make gift-ness merely SUSPECTED. At exactly this count the verdict
    /// depends on the follow-on-accessory observation below.
    /// </summary>
    public const int GiftSignalsForCorroboration = 2;

    /// <summary>
    /// The "no accessory purchased in the nine months since" window, in days. A customer who
    /// keeps a console buys something for it; a customer who gave one away does not.
    /// </summary>
    public const int GiftFollowOnWindowDays = 270;

    /// <summary>Minimum repeat count before a consumable's cadence is believable (§B.3, Sofia).</summary>
    public const int ReplenishmentMinimumPurchases = 3;

    /// <summary>
    /// Maximum coefficient of variation of the inter-purchase intervals for the cadence to
    /// count as stable (§B.3). Sofia's cartridges run at CV 0.06 and her beans at CV 0.11.
    /// </summary>
    public const double ReplenishmentMaximumIntervalCv = 0.50;

    /// <summary>
    /// Minimum ownership age before a second purchase in the same leaf category reads as a
    /// replacement rather than as two of the same thing.
    /// </summary>
    public const int ReplacementMinimumOwnershipDays = 365;

    /// <summary>The interest weight attached to an <paramref name="intent"/>.</summary>
    /// <param name="intent">The derived intent.</param>
    public static double WeightFor(PurchaseIntent intent) => intent switch
    {
        PurchaseIntent.ForSelf       => ForSelfWeight,
        PurchaseIntent.Replacement   => ReplacementWeight,
        PurchaseIntent.Unknown       => UnknownWeight,
        PurchaseIntent.Replenishment => ReplenishmentWeight,
        PurchaseIntent.Gift          => GiftWeight,
        _                            => UnknownWeight
    };

    /// <summary>
    /// Classifies an entire purchase history. Lines whose <see cref="Purchase.ProductId"/>
    /// does not resolve in <paramref name="productsBySku"/> are SKIPPED — a classification
    /// needs the product record — and are reported by
    /// <see cref="UnresolvedProductIds(IEnumerable{Purchase}, IReadOnlyDictionary{string, Product})"/>
    /// so that a seed/history mismatch is visible instead of silently shrinking the history.
    /// </summary>
    /// <param name="history">Every order line belonging to one customer.</param>
    /// <param name="productsBySku">The catalogue, keyed by <see cref="Product.Id"/>.</param>
    /// <param name="asOf">The demo clock's "today". Drives every elapsed-time rule.</param>
    /// <returns>One classification per resolvable line, in <see cref="Purchase.PurchasedOn"/> order then id order.</returns>
    public static IReadOnlyList<ClassifiedPurchase> ClassifyAll(
        IEnumerable<Purchase> history,
        IReadOnlyDictionary<string, Product> productsBySku,
        DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(productsBySku);

        var ordered = history
            .OrderBy(p => p.PurchasedOn)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var results = new List<ClassifiedPurchase>(ordered.Count);
        foreach (var purchase in ordered)
        {
            if (!productsBySku.TryGetValue(purchase.ProductId, out var product)) continue;
            results.Add(Classify(purchase, product, ordered, productsBySku, asOf));
        }

        return results;
    }

    /// <summary>
    /// Classifies one order line against the customer's whole history. Public so a single
    /// line can be re-derived (and its <c>Because</c> re-printed) without rebuilding the map.
    /// </summary>
    /// <param name="purchase">The line to classify.</param>
    /// <param name="product">The resolved catalogue record for <paramref name="purchase"/>.</param>
    /// <param name="ownerHistory">Every line belonging to the same customer, including this one.</param>
    /// <param name="productsBySku">The catalogue, keyed by <see cref="Product.Id"/>.</param>
    /// <param name="asOf">The demo clock's "today".</param>
    public static ClassifiedPurchase Classify(
        Purchase purchase,
        Product product,
        IReadOnlyList<Purchase> ownerHistory,
        IReadOnlyDictionary<string, Product> productsBySku,
        DateOnly asOf)
    {
        ArgumentNullException.ThrowIfNull(purchase);
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(ownerHistory);
        ArgumentNullException.ThrowIfNull(productsBySku);

        int giftSignals   = purchase.GiftSignalCount;
        int elapsedDays   = purchase.DaysSince(asOf);
        bool windowClosed = elapsedDays >= GiftFollowOnWindowDays;
        int followOns     = CountFollowOnAccessories(purchase, product, ownerHistory, productsBySku);

        // ── Rule 1 — certain gift ────────────────────────────────────────────────────
        if (giftSignals >= GiftSignalsForCertainty)
        {
            return Classified(purchase, product, PurchaseIntent.Gift,
                DescribeGiftSignals(purchase) + "; " + DescribeFollowOn(followOns, elapsedDays, windowClosed));
        }

        // ── Rule 2 — replenishment (before the weaker gift rule, on purpose) ─────────
        var cadence = MeasureCadence(purchase, product, ownerHistory);
        if (cadence.Applies && cadence.IsStable)
        {
            return Classified(purchase, product, PurchaseIntent.Replenishment,
                string.Create(CultureInfo.InvariantCulture,
                    $"{cadence.PurchaseCount} purchases of a consumable on a stable cadence — mean {cadence.MeanIntervalDays:0} d, CV {cadence.Cv:0.00} (below {ReplenishmentMaximumIntervalCv:0.00}); routed to the replenishment lane, never to discovery"));
        }

        // ── Rule 3 — corroborated gift ──────────────────────────────────────────────
        if (giftSignals >= GiftSignalsForCorroboration && windowClosed && followOns == 0)
        {
            return Classified(purchase, product, PurchaseIntent.Gift,
                DescribeGiftSignals(purchase) + "; " + DescribeFollowOn(followOns, elapsedDays, windowClosed));
        }

        // ── Rule 4a — ambiguous gift ────────────────────────────────────────────────
        if (giftSignals >= GiftSignalsForCorroboration)
        {
            return Classified(purchase, product, PurchaseIntent.Unknown,
                string.Create(CultureInfo.InvariantCulture,
                    $"ambiguous: {DescribeGiftSignals(purchase)}, but {DescribeFollowOn(followOns, elapsedDays, windowClosed)} — held at reduced weight rather than counted as the customer's own interest"));
        }

        // ── Rule 4b — a consumable that repeats but not on a cadence ────────────────
        if (cadence.Applies && !cadence.IsStable)
        {
            return Classified(purchase, product, PurchaseIntent.Unknown,
                string.Create(CultureInfo.InvariantCulture,
                    $"ambiguous: {cadence.PurchaseCount} purchases of a consumable, but the interval is erratic (mean {cadence.MeanIntervalDays:0} d, CV {cadence.Cv:0.00} at or above {ReplenishmentMaximumIntervalCv:0.00}) — no cadence can be predicted from it"));
        }

        // ── Rule 5 — like-for-like replacement ──────────────────────────────────────
        var replaced = FindReplacedPurchase(purchase, product, ownerHistory, productsBySku);
        if (replaced is not null)
        {
            int months = Math.Max(1, replaced.Value.GapDays / 30);
            return Classified(purchase, product, PurchaseIntent.Replacement,
                string.Create(CultureInfo.InvariantCulture,
                    $"a second {product.LeafCategory} {months} months after {replaced.Value.EarlierPurchaseId}; a like-for-like replacement confirms an existing interest, it does not reveal a new one"));
        }

        // ── Rule 6 — the customer's own ─────────────────────────────────────────────
        return Classified(purchase, product, PurchaseIntent.ForSelf, DescribeForSelf(purchase, giftSignals));
    }

    /// <summary>
    /// The <see cref="Purchase.ProductId"/> values in <paramref name="history"/> that do NOT
    /// resolve in <paramref name="productsBySku"/>. An empty list is the expected state; a
    /// non-empty one means the persona seed and the catalogue seed have drifted apart, and it
    /// is surfaced rather than swallowed so the history cannot silently shrink.
    /// </summary>
    /// <param name="history">Order lines.</param>
    /// <param name="productsBySku">The catalogue, keyed by <see cref="Product.Id"/>.</param>
    public static IReadOnlyList<string> UnresolvedProductIds(
        IEnumerable<Purchase> history,
        IReadOnlyDictionary<string, Product> productsBySku)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(productsBySku);

        var missing = new List<string>();
        foreach (var purchase in history)
            if (!productsBySku.ContainsKey(purchase.ProductId) &&
                !missing.Contains(purchase.ProductId, StringComparer.Ordinal))
                missing.Add(purchase.ProductId);

        missing.Sort(StringComparer.Ordinal);
        return missing;
    }

    // ── internals ────────────────────────────────────────────────────────────────────

    private static ClassifiedPurchase Classified(Purchase purchase, Product product, PurchaseIntent intent, string because) =>
        new(purchase, product, intent, WeightFor(intent), because);

    /// <summary>
    /// The four observable signals, named in the order §B.3 prints them. Only the ones that
    /// actually fired are listed — the string is evidence, not a template.
    /// </summary>
    private static string DescribeGiftSignals(Purchase purchase)
    {
        var clauses = new List<string>(4);
        if (purchase.WasGiftWrapped)             clauses.Add("gift-wrapped");
        if (purchase.ShippedToAlternateAddress)  clauses.Add("shipped to an alternate address");
        if (purchase.HasGiftMessage)             clauses.Add("gift message present");
        if (!purchase.HasOwnReview)              clauses.Add("no review authored");
        return clauses.Count == 0 ? "no gift signal fired" : string.Join("; ", clauses);
    }

    private static string DescribeFollowOn(int followOns, int elapsedDays, bool windowClosed)
    {
        if (!windowClosed)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"the {GiftFollowOnWindowDays / 30}-month follow-on window is still open ({elapsedDays} of {GiftFollowOnWindowDays} days elapsed)");
        }

        return followOns == 0
            ? string.Create(CultureInfo.InvariantCulture,
                $"no accessory purchased in the {GiftFollowOnWindowDays / 30} months since")
            : string.Create(CultureInfo.InvariantCulture,
                $"{followOns} accessory purchase(s) followed within {GiftFollowOnWindowDays / 30} months");
    }

    private static string DescribeForSelf(Purchase purchase, int giftSignals)
    {
        var head = string.Create(CultureInfo.InvariantCulture,
            $"{giftSignals} of 4 observable gift signals fired, below the threshold of {GiftSignalsForCorroboration}");
        return purchase.HasOwnReview
            ? head + "; the customer authored a review, which is a stronger ownership signal than the order line alone"
            : head + "; treated as the customer's own interest";
    }

    /// <summary>
    /// Counts later purchases, inside the follow-on window, in the SAME root category, that
    /// are not themselves gift-signalled. A same-day companion (Marco's console and game
    /// arrived together, both gift-wrapped) is deliberately not a follow-on: the comparison
    /// is strictly later, and the companion is excluded anyway for carrying its own gift signals.
    /// </summary>
    private static int CountFollowOnAccessories(
        Purchase purchase,
        Product product,
        IReadOnlyList<Purchase> ownerHistory,
        IReadOnlyDictionary<string, Product> productsBySku)
    {
        int count = 0;
        foreach (var other in ownerHistory)
        {
            if (string.Equals(other.Id, purchase.Id, StringComparison.Ordinal)) continue;
            if (other.PurchasedOn <= purchase.PurchasedOn) continue;
            if (other.PurchasedOn.DayNumber - purchase.PurchasedOn.DayNumber > GiftFollowOnWindowDays) continue;
            if (other.GiftSignalCount >= GiftSignalsForCorroboration) continue;
            if (!productsBySku.TryGetValue(other.ProductId, out var otherProduct)) continue;
            if (!string.Equals(otherProduct.RootCategory, product.RootCategory, StringComparison.OrdinalIgnoreCase)) continue;
            count++;
        }

        return count;
    }

    private readonly record struct Cadence(bool Applies, bool IsStable, int PurchaseCount, double MeanIntervalDays, double Cv);

    /// <summary>
    /// Coefficient of variation of the intervals between repeat purchases of the SAME SKU.
    /// Applies only to consumables with at least <see cref="ReplenishmentMinimumPurchases"/>
    /// lines, which is what makes "she bought the cartridges five times" a cadence rather
    /// than a coincidence.
    /// </summary>
    private static Cadence MeasureCadence(Purchase purchase, Product product, IReadOnlyList<Purchase> ownerHistory)
    {
        if (!product.IsConsumable) return new Cadence(false, false, 0, 0, 0);

        var dates = new List<DateOnly>();
        foreach (var other in ownerHistory)
            if (string.Equals(other.ProductId, purchase.ProductId, StringComparison.Ordinal))
                dates.Add(other.PurchasedOn);

        if (dates.Count < ReplenishmentMinimumPurchases) return new Cadence(false, false, dates.Count, 0, 0);

        dates.Sort();
        var intervals = new double[dates.Count - 1];
        for (int i = 1; i < dates.Count; i++) intervals[i - 1] = dates[i].DayNumber - dates[i - 1].DayNumber;

        double mean = intervals.Average();
        if (mean <= 0) return new Cadence(true, false, dates.Count, 0, double.PositiveInfinity);

        double variance = intervals.Sum(v => (v - mean) * (v - mean)) / intervals.Length;
        double cv = Math.Sqrt(variance) / mean;

        return new Cadence(true, cv < ReplenishmentMaximumIntervalCv, dates.Count, mean, cv);
    }

    private readonly record struct Replaced(string EarlierPurchaseId, int GapDays);

    /// <summary>
    /// A replacement is a later purchase in the same LEAF category, of a non-consumable,
    /// at least <see cref="ReplacementMinimumOwnershipDays"/> after the earlier one. The gap
    /// is what separates "the old one wore out" from "she bought two of them".
    /// </summary>
    private static Replaced? FindReplacedPurchase(
        Purchase purchase,
        Product product,
        IReadOnlyList<Purchase> ownerHistory,
        IReadOnlyDictionary<string, Product> productsBySku)
    {
        if (product.IsConsumable) return null;

        Replaced? best = null;
        foreach (var other in ownerHistory)
        {
            if (string.Equals(other.Id, purchase.Id, StringComparison.Ordinal)) continue;
            if (other.PurchasedOn >= purchase.PurchasedOn) continue;
            if (!productsBySku.TryGetValue(other.ProductId, out var otherProduct)) continue;
            if (otherProduct.IsConsumable) continue;
            if (!string.Equals(otherProduct.LeafCategory, product.LeafCategory, StringComparison.OrdinalIgnoreCase)) continue;

            int gap = purchase.PurchasedOn.DayNumber - other.PurchasedOn.DayNumber;
            if (gap < ReplacementMinimumOwnershipDays) continue;

            if (best is null || gap < best.Value.GapDays) best = new Replaced(other.Id, gap);
        }

        return best;
    }
}
