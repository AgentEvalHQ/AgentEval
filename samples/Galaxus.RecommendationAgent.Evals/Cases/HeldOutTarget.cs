// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Signals;

namespace Galaxus.RecommendationAgent.Evals.Cases;

/// <summary>
/// One leave-one-out target: a customer, the purchase line hidden from every arm, the reduced
/// history the arms see instead, and the pool the chance floor is drawn from.
/// </summary>
/// <param name="PersonaId">The customer.</param>
/// <param name="Name">Display name.</param>
/// <param name="Hidden">The line hidden from the arms — the thing to predict.</param>
/// <param name="Target">The hidden line's product.</param>
/// <param name="Visible">The customer's profile with the hidden line removed. What every arm reads.</param>
/// <param name="PoolSize">Catalogue products NOT owned on the visible history — the draw pool.</param>
/// <param name="LeafCarriersInPool">Pool products in the target's leaf category, target included.</param>
/// <param name="AlternativeMostRecent">
/// The most recent line of ANY intent, when it differs from <see cref="Hidden"/> — a replacement
/// or replenishment repeat that the first-time rule skipped. Printed so the reader sees which
/// reading of "most recent self-purchase" was used and what the other would have targeted.
/// </param>
public sealed record HeldOutTarget(
    string PersonaId,
    string Name,
    Purchase Hidden,
    Product Target,
    CustomerProfile Visible,
    int PoolSize,
    int LeafCarriersInPool,
    Purchase? AlternativeMostRecent)
{
    /// <summary>The hidden product's leaf — the second, coarser hit definition.</summary>
    public string TargetLeaf => Target.LeafCategory;

    /// <summary>False when the target has no stock: every arm that gates on stock cannot hit it. Reported, not excused.</summary>
    public bool TargetInStock => Target.InStock;

    /// <summary>
    /// The prompt every arm sees: Eval 02's canonical history question, framed for this customer.
    /// History-driven by design — next-purchase prediction is a history task.
    /// </summary>
    public string Prompt => GalaxusEvalPrompt.For(PersonaId, GalaxusEvalPrompt.CoverageCanonical);

    /// <summary>The SKUs on the visible history. What a discovery arm excludes.</summary>
    public IReadOnlySet<string> VisibleOwnedSkus =>
        Visible.Purchases.Select(p => p.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The SKU chance floor at k: <c>k / pool</c> — a uniform k-draw from the pool contains the one target.</summary>
    /// <param name="k">Presentation budget.</param>
    public double SkuFloor(int k) => PoolSize <= 0 ? double.NaN : Math.Min(1.0, k / (double)PoolSize);

    /// <summary>The LEAF chance floor at k: a uniform k-draw contains at least one product in the target's leaf.</summary>
    /// <param name="k">Presentation budget.</param>
    public double LeafFloor(int k) => ChanceFloors.AtLeastOneHit(PoolSize, LeafCarriersInPool, k);
}

/// <summary>
/// Derives the leave-one-out targets from the seeded histories by ONE stated rule — never by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule.</b> For every customer with at least <see cref="MinimumPurchases"/> order lines:
/// classify the FULL history with the shipped intent classifier, take the lines it calls
/// <c>ForSelf</c> whose SKU appears nowhere earlier in that history — a first-time purchase for
/// their own use — and hide the most recent of them. Gifts are not the customer's interest;
/// replenishment repeats belong to the replenishment lane; a like-for-like replacement is a
/// repeat of an owned SKU that every discovery arm excludes by construction. Predicting any of
/// those would measure the exclusion rule, not the recommender.
/// </para>
/// <para>
/// <b>What this corpus does and does not let the number mean.</b> Seventy-nine order lines,
/// fourteen customers, hand-authored to a structural target (three reachable latent interests
/// each) rather than sampled from a log. One target per customer, so n is thirteen at most and a
/// single hit moves the rate by 0.077. Four of the histories were authored with a REPLACEMENT as
/// their most recent line and one with a replenishment run, so the first-time rule targets an
/// EARLIER line for those five — printed beside the alternative. The number can tell a broken arm
/// from a working one (an arm that reads history at all should beat <c>k / pool</c>); it cannot
/// rank two working architectures, and no reading of it should try.
/// </para>
/// </remarks>
public static class HeldOutTargets
{
    /// <summary>Customers with fewer lines than this are not targets. Luca has one line.</summary>
    public const int MinimumPurchases = 3;

    /// <summary>Derives every target from the SEEDED profiles. Must not be called inside an override scope.</summary>
    /// <exception cref="InvalidOperationException">A hold-out override is open on this flow.</exception>
    public static IReadOnlyList<HeldOutTarget> Derive()
    {
        if (UserProfiles.IsOverridden)
            throw new InvalidOperationException(
                "HeldOutTargets.Derive was called inside a UserProfiles.BeginOverride scope; it would derive the " +
                "targets from an already-reduced history.");

        var catalogue = Catalogue.Default;
        var targets = new List<HeldOutTarget>();

        foreach (CustomerProfile profile in UserProfiles.All)
        {
            if (profile.PurchaseCount < MinimumPurchases) continue;

            Purchase? hidden = SelectHidden(profile);
            if (hidden is null) continue;

            var target = catalogue.Require(hidden.ProductId);
            var visible = profile.WithoutPurchase(hidden.Id);
            var owned = visible.Purchases.Select(p => p.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var pool = catalogue.All.Where(p => !owned.Contains(p.Id)).ToList();
            int leafCarriers = pool.Count(p => string.Equals(p.LeafCategory, target.LeafCategory, StringComparison.Ordinal));

            Purchase mostRecent = profile.Purchases.OrderBy(p => p.PurchasedOn).ThenBy(p => p.Id, StringComparer.Ordinal).Last();
            Purchase? alternative = string.Equals(mostRecent.Id, hidden.Id, StringComparison.Ordinal) ? null : mostRecent;

            targets.Add(new HeldOutTarget(
                profile.Id, profile.DisplayName, hidden, target, visible, pool.Count, leafCarriers, alternative));
        }

        return targets;
    }

    /// <summary>The line the rule hides for one customer, or null when no line qualifies.</summary>
    /// <param name="profile">The SEEDED profile.</param>
    public static Purchase? SelectHidden(CustomerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var catalogue = Catalogue.Default;
        var classified = PurchaseIntentClassifier.ClassifyAll(profile.Purchases, catalogue.BySku, Catalogue.DemoToday);

        var seenSkus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Purchase? best = null;

        foreach (var line in classified)   // oldest first
        {
            bool firstTime = seenSkus.Add(line.Product.Id);
            if (line.Intent != PurchaseIntent.ForSelf || !firstTime) continue;

            if (best is null
                || line.Purchase.PurchasedOn > best.PurchasedOn
                || (line.Purchase.PurchasedOn == best.PurchasedOn
                    && string.CompareOrdinal(line.Purchase.Id, best.Id) > 0))
            {
                best = line.Purchase;
            }
        }

        return best;
    }
}
