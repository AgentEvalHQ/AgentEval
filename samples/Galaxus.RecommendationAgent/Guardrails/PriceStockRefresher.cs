// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text.RegularExpressions;
using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Guardrails;

/// <summary>
/// Price and availability, read from the catalogue at render time and stamped with the moment
/// they were read. The RENDERER prints these figures; the model never states one (§F.4).
/// </summary>
/// <param name="ProductId">The SKU these figures belong to.</param>
/// <param name="PriceChf">Current price in Swiss francs, from the catalogue.</param>
/// <param name="WasPriceChf">Strike-through price, or null.</param>
/// <param name="StockUnits">Units on hand right now.</param>
/// <param name="AvailableInMarket">Whether the SKU can ship to the customer's market.</param>
/// <param name="DeliveryEstimateDays">Working-day delivery estimate; zero when out of stock.</param>
/// <param name="AsOfUtc">When these figures were read. Printed, so the number is auditable.</param>
public sealed record PriceStockSnapshot(
    string ProductId,
    decimal PriceChf,
    decimal? WasPriceChf,
    int StockUnits,
    bool AvailableInMarket,
    int DeliveryEstimateDays,
    DateTimeOffset AsOfUtc)
{
    /// <summary>True when stock is on hand.</summary>
    public bool InStock => StockUnits > 0;

    /// <summary>True when there is a strike-through price strictly above the current one.</summary>
    public bool IsDiscounted => WasPriceChf is { } was && was > PriceChf;

    /// <summary>The verified price line the renderer prints, e.g. <c>"CHF 129.00 · 4 in stock · 2 working days"</c>.</summary>
    public string ToPriceLine() => string.Create(CultureInfo.InvariantCulture,
        $"CHF {PriceChf:0.00}{(IsDiscounted ? string.Create(CultureInfo.InvariantCulture, $" (was CHF {WasPriceChf:0.00})") : string.Empty)} · {(InStock ? string.Create(CultureInfo.InvariantCulture, $"{StockUnits} in stock · {DeliveryEstimateDays} working days") : "out of stock")}");
}

/// <summary>
/// Stage 4 of the guardrails, run LAST so it only pays to verify survivors (§F.4). Three jobs,
/// all of them about the same boundary: <b>the model is structurally unable to state a price.</b>
/// </summary>
/// <remarks>
/// <para>
/// <b>1. Model-stated prices are DISCARDED.</b> Every customer-facing reason string is scanned
/// for currency patterns, and a match drops the item with
/// <see cref="GuardrailReasons.StatedPrice"/>. It is not corrected, not annotated, not
/// down-ranked. Embeddings are computed once and prices change hourly, so a price that
/// travelled through the semantic leg is stale by construction — and this mirrors Galaxus's own
/// boundary, where the shipped community AI is explicitly forbidden from answering price
/// questions.
/// </para>
/// <para>
/// <b>2. Price and stock are re-read from the catalogue</b> into a
/// <see cref="PriceStockSnapshot"/> per surviving item, timestamped, for the renderer to print.
/// <c>RecommendationDto</c> deliberately has no price field, so there is nowhere for a model-
/// written figure to hide.
/// </para>
/// <para>
/// <b>3. Availability.</b> Out-of-stock items are DEMOTED to <c>also consider</c> with an
/// explicit note — which is exactly Galaxus's existing substitute-suggestion use case, not a
/// failure. An item that cannot ship to the customer's market is DROPPED, because unlike a
/// restock that is not a matter of waiting.
/// </para>
/// </remarks>
public static class PriceStockRefresher
{
    /// <summary>
    /// Currency and price patterns scanned for in customer-facing prose. Tuned to fire on money
    /// and not on specifications: <c>"1.09 kg"</c>, <c>"16-35 mm f/4"</c> and <c>"140 W"</c> must
    /// all pass, while <c>"CHF 129"</c>, <c>"129.-"</c>, <c>"€ 99"</c> and <c>"about 60 francs"</c>
    /// must not.
    /// </summary>
    private static readonly Regex PricePattern = new(
        @"(?:\bCHF\b)"                                   // the Swiss currency code, anywhere
        + @"|(?:\b(?:EUR|USD|GBP)\b)"                    // other ISO codes
        + @"|[€$£¥]"                                     // currency symbols
        + @"|(?:\bFr\.\s?\d)"                            // "Fr. 129"
        + @"|(?:\b\d+[.,]\-)"                            // Swiss retail shorthand "129.-"
        + @"|(?:\b\d+(?:[.,]\d{1,2})?\s*(?:francs?|franken|euros?|dollars?|pounds?)\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="text"/> states a price. Used both by the pipeline stage and by
    /// <see cref="GuardrailPipeline.Screen"/>, so the tool call and the rendered answer are held
    /// to one rule.
    /// </summary>
    /// <param name="text">A <c>why_this</c> or a <c>reason</c> tool argument.</param>
    /// <param name="matched">The offending fragment, or null.</param>
    public static bool StatesAPrice(string? text, out string? matched)
    {
        matched = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var match = PricePattern.Match(text);
        if (!match.Success) return false;

        matched = match.Value.Trim();
        return true;
    }

    /// <summary>
    /// Drops price-stating items, drops market-unavailable items, demotes out-of-stock items,
    /// and returns a verified snapshot for every survivor.
    /// </summary>
    /// <param name="set">The answer so far.</param>
    /// <param name="context">The catalogue-derived bar; supplies the market and the timestamp.</param>
    /// <param name="ledger">The ledger every drop and demotion is written to.</param>
    /// <returns>The cleaned answer, and the verified figures the renderer prints.</returns>
    public static (RecommendationSet Cleaned, IReadOnlyDictionary<string, PriceStockSnapshot> Verified) Apply(
        RecommendationSet set,
        GuardrailContext context,
        GuardrailLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ledger);

        var verified = new Dictionary<string, PriceStockSnapshot>(StringComparer.Ordinal);
        var primary = new List<RecommendationDto>();
        var secondary = new List<RecommendationDto>();

        int requested = set.PresentedCount + set.Replenishment.Count;

        Process(set.Recommendations, fromPrimaryTray: true, primary, secondary, verified, context, ledger);
        Process(set.AlsoConsider, fromPrimaryTray: false, primary, secondary, verified, context, ledger);

        foreach (var item in set.Replenishment)
        {
            if (!context.ProductsBySku.TryGetValue(item.ProductId, out var product)) continue;
            verified[item.ProductId] = Snapshot(product, context);
        }

        ledger.RecordPriceStock(requested, verified.Count);

        var cleaned = set with { Recommendations = primary, AlsoConsider = secondary };
        return (cleaned, verified);
    }

    /// <summary>Reads the authoritative price and stock for one product, stamped with the run's clock.</summary>
    /// <param name="product">The catalogue record.</param>
    /// <param name="context">Supplies the customer's market and the verification timestamp.</param>
    public static PriceStockSnapshot Snapshot(Product product, GuardrailContext context)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(context);

        return new PriceStockSnapshot(
            product.Id,
            product.PriceChf,
            product.WasPriceChf,
            product.StockUnits,
            product.IsAvailableIn(context.User.Market),
            product.StockUnits > 0 ? context.DeliveryEstimateDays : 0,
            context.AsOfUtc);
    }

    private static void Process(
        IReadOnlyList<RecommendationDto> items,
        bool fromPrimaryTray,
        List<RecommendationDto> primary,
        List<RecommendationDto> secondary,
        Dictionary<string, PriceStockSnapshot> verified,
        GuardrailContext context,
        GuardrailLedger ledger)
    {
        foreach (var item in items)
        {
            if (StatesAPrice(item.WhyThis, out var priceToken))
            {
                ledger.Drop(GuardrailStage.PriceStock, GuardrailReasons.StatedPrice, item.ProductId,
                    $"the customer-facing reason states a price (\"{priceToken}\"). Embeddings are computed once and prices change hourly, " +
                    "so a price that travelled through the model is stale by construction — the interface prints the verified figure itself");
                continue;
            }

            if (!context.ProductsBySku.TryGetValue(item.ProductId, out var product)) continue;

            var snapshot = Snapshot(product, context);

            if (!snapshot.AvailableInMarket)
            {
                ledger.Drop(GuardrailStage.PriceStock, GuardrailReasons.MarketUnavailable, item.ProductId,
                    $"cannot ship to {context.User.Market}. Unlike a restock, that is not a matter of waiting");
                continue;
            }

            verified[item.ProductId] = snapshot;

            if (snapshot.InStock)
            {
                if (fromPrimaryTray) primary.Add(item); else secondary.Add(item);
                continue;
            }

            if (fromPrimaryTray)
            {
                ledger.Demote(GuardrailStage.PriceStock, GuardrailReasons.OutOfStock, item.ProductId,
                    "out of stock — demoted to 'also consider' with an explicit note, which is the substitute-suggestion case, not a failure");
            }
            else
            {
                ledger.Note(GuardrailStage.PriceStock, GuardrailReasons.OutOfStock, item.ProductId,
                    "out of stock — already in 'also consider'; the note is printed with the card");
            }

            secondary.Add(item);
        }
    }
}
