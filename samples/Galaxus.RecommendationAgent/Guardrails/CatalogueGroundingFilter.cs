// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// Stage 2 of <see cref="GuardrailPipeline"/>. Grounds every presented product id in the two
/// things the structured data layer is authoritative about: <b>what exists</b> and
/// <b>what the customer already has</b>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Existence (§F.2).</b> Every product id must resolve in the catalogue. Non-resolving ids
/// are REMOVED, not down-ranked, and counted as <see cref="GuardrailReasons.Ungrounded"/>.
/// Combined with "the model may only pick from retrieved candidates", a hallucinated SKU stops
/// being statistically unlikely and becomes structurally impossible. This check is possible
/// only because the structured tool leg exists: without a deterministic authority to check
/// against, "no hallucinated SKUs" is a hope rather than a filter.
/// </para>
/// <para>
/// <b>Ownership (§B.3, Sofia).</b> Two traps that are ordinary in production and embarrassing
/// in a demo:
/// </para>
/// <list type="number">
///   <item>
///     <i>Discovery pollution.</i> A similarity recommender returns "you might like: the water
///     filter cartridges" to a customer who has bought them five times. That is not a
///     recommendation; it is an insult with a checkout button. Anything in
///     <see cref="GuardrailContext.OwnedProductIds"/> is dropped from the discovery trays —
///     never from the replenishment tray, which is a different lane with a different meaning.
///   </item>
///   <item>
///     <i>Durable churn.</i> "Similar to your Vitamix" returns three more blenders. She owns
///     one, thirty months old, still inside its horizon. Anything in a leaf category named by
///     <see cref="GuardrailContext.OwnedDurableLeafCategories"/> is dropped.
///   </item>
/// </list>
/// <para>
/// Both ownership arms read <b>gift-adjusted</b> ownership: Marco was shipped a games console
/// to another address with a gift message and never reviewed it, so he does not own it, and
/// neither arm fires on Gaming. That is the classifier's verdict being honoured downstream
/// rather than restated.
/// </para>
/// </remarks>
public static class CatalogueGroundingFilter
{
    /// <summary>True when <paramref name="productId"/> resolves in the catalogue.</summary>
    /// <param name="productId">The id the model wrote.</param>
    /// <param name="context">The catalogue-derived bar.</param>
    public static bool IsGrounded(string? productId, GuardrailContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !string.IsNullOrWhiteSpace(productId) && context.ProductsBySku.ContainsKey(productId);
    }

    /// <summary>
    /// Removes ungrounded, duplicated, already-owned and durable-churn items from both
    /// discovery trays, and ungrounded items from the replenishment tray.
    /// </summary>
    /// <param name="set">The answer so far.</param>
    /// <param name="context">The catalogue-derived bar.</param>
    /// <param name="ledger">The ledger every drop is written to.</param>
    public static RecommendationSet Apply(RecommendationSet set, GuardrailContext context, GuardrailLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);

        if (context.OwnedProductIds.Count == 0)
        {
            ledger.Note(GuardrailStage.CatalogueGrounding, GuardrailReasons.ArmInapplicable, "—",
                "the customer owns nothing in this context, so the already-owned and durable-churn arms had nothing " +
                "to fire against (chance floor 1.0 — not a pass). The existence arm below DID run.");
        }
        else if (context.ReplenishmentProductIds.Count == 0)
        {
            ledger.Note(GuardrailStage.CatalogueGrounding, GuardrailReasons.ArmInapplicable, "replenishment lane",
                "this customer has no purchase on a replenishment cadence, so the replenishment_not_discovery arm " +
                "had nothing to fire against (chance floor 1.0 — not a pass). The already-owned arm beside it DID run.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        var primary   = Filter(set.Recommendations, context, ledger, seen);
        var secondary = Filter(set.AlsoConsider,    context, ledger, seen);

        var replenishment = new List<ReplenishmentDto>(set.Replenishment.Count);
        foreach (var item in set.Replenishment)
        {
            if (IsGrounded(item.ProductId, context)) { replenishment.Add(item); continue; }
            ledger.Drop(GuardrailStage.CatalogueGrounding, GuardrailReasons.Ungrounded, item.ProductId,
                "replenishment item does not resolve in the catalogue");
        }

        return set with
        {
            Recommendations = primary,
            AlsoConsider = secondary,
            Replenishment = replenishment
        };
    }

    private static IReadOnlyList<RecommendationDto> Filter(
        IReadOnlyList<RecommendationDto> items,
        GuardrailContext context,
        GuardrailLedger ledger,
        HashSet<string> seen)
    {
        var kept = new List<RecommendationDto>(items.Count);

        foreach (var item in items)
        {
            if (!context.ProductsBySku.TryGetValue(item.ProductId, out var product))
            {
                ledger.Drop(GuardrailStage.CatalogueGrounding, GuardrailReasons.Ungrounded, item.ProductId,
                    "does not resolve in the catalogue — removed, not down-ranked. A product id that does not exist is a hallucination, not a near miss");
                continue;
            }

            if (!seen.Add(item.ProductId))
            {
                ledger.Drop(GuardrailStage.CatalogueGrounding, GuardrailReasons.DuplicatePresentation, item.ProductId,
                    "presented more than once in the same turn");
                continue;
            }

            // ── §8.1 B-16: the replenishment lane, checked BEFORE ownership ─────────────
            //
            // Sofia's cartridges used to leave the ledger as `already_owned`. That was true and
            // useless: it named the wrong mechanism, and the lane that actually handles them —
            // the repeat-buy tray with its cadence and due date — was never seen working on any
            // run. A consumable on a cadence is not "something she owns"; it is something she is
            // about to buy again, and the ledger now says which of the two happened.
            if (context.ReplenishmentProductIds.Contains(item.ProductId))
            {
                ledger.Drop(GuardrailStage.CatalogueGrounding, GuardrailReasons.ReplenishmentNotDiscovery, item.ProductId,
                    $"{product.Name} is on this customer's replenishment cadence and is already in the repeat-buy tray " +
                    "with its due date. Surfacing it as a discovery is the \"you might like the cartridges you have " +
                    "bought five times\" failure — a different lane, not a different ranking");
                continue;
            }

            if (context.OwnedProductIds.Contains(item.ProductId))
            {
                var detail = product.IsConsumable
                    ? $"the customer already buys {product.Name}. A consumable they buy on a cadence belongs in the replenishment lane, not in discovery"
                    : $"the customer already owns {product.Name}. Recommending it back to them is not a recommendation";

                ledger.Drop(GuardrailStage.CatalogueGrounding, GuardrailReasons.AlreadyOwned, item.ProductId, detail);
                continue;
            }

            if (context.SuppressDurableUpgrades &&
                !product.IsConsumable &&
                context.OwnedDurableLeafCategories.Contains(product.LeafCategory))
            {
                ledger.Drop(GuardrailStage.CatalogueGrounding, GuardrailReasons.DurableStillInHorizon, item.ProductId,
                    $"the customer already owns a {product.LeafCategory} still inside its typical service life — the upgrade lane is suppressed, not merely down-ranked");
                continue;
            }

            kept.Add(item);
        }

        return kept;
    }
}
