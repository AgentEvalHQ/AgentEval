// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;
using System.Text.RegularExpressions;

namespace Galaxus.RecommendationAgent.Evals.Cases;

/// <summary>
/// One hard constraint a stated need places on a product, checked in CODE against the catalogue
/// record. The gold of Eval 02b is a conjunction of these and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Non-circular by construction.</b> Eval 02's latent gold is derived from the same
/// <c>context:</c> / <c>trip:</c> / <c>weight:</c> / <c>skill:</c> tags the retrieval index
/// embeds — which is why a two-line tag join scores 1.000 on it. Nothing here reads that
/// vocabulary. Every constraint below is a structured catalogue fact: price, stock, seller,
/// category path, a spec value, a declared market, ownership, or a <c>compat:</c> tag —
/// and <c>compat:</c> is the one tag family <c>EmbeddingDocument.UseTagPrefixes</c> deliberately
/// leaves OUT of the index (it is a hard filter in the tool layer, never a vector signal).
/// </para>
/// <para>
/// <b>Records, so two constraints with the same parameters are equal.</b> A case's
/// "distinct constraint count" — the ≥ 3 the fixture requires — is a set count over these values.
/// </para>
/// </remarks>
public abstract record ProductConstraint
{
    /// <summary>One line naming the constraint as the customer stated it and as the code checks it.</summary>
    public abstract string Describe();

    /// <summary>True when <paramref name="product"/> satisfies this constraint for <paramref name="customer"/>.</summary>
    /// <param name="product">A catalogue product.</param>
    /// <param name="customer">The customer who stated the need — read only for ownership and compatibility.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    public abstract bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue);
}

/// <summary>Catalogue price at or under a stated ceiling. The price is the catalogue's, never a model-stated one.</summary>
/// <param name="Ceiling">Budget in CHF, inclusive.</param>
public sealed record MaxPriceChf(decimal Ceiling) : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() =>
        $"price ≤ CHF {Ceiling.ToString("0.##", CultureInfo.InvariantCulture)} (catalogue PriceChf)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        product.PriceChf <= Ceiling;
}

/// <summary>
/// Stock on hand right now. This is how a DEADLINE is codified: the catalogue carries no
/// delivery-days field, so "must arrive before the trip" can only be checked as "is in stock
/// today", and the report says so beside the constraint.
/// </summary>
/// <param name="Because">The customer's own deadline wording, printed beside the check.</param>
public sealed record InStockNow(string Because) : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() => $"in stock now (StockUnits > 0) — the catalogue's only proxy for \"{Because}\"";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        product.StockUnits > 0;
}

/// <summary>Sold by Galaxus itself, not a marketplace seller.</summary>
public sealed record NotMarketplace : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() => "sold by Galaxus itself (MarketplaceSeller is null)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        !product.IsMarketplaceOffer;
}

/// <summary>Not a consumable — no beans, tablets, cartridges, descaler.</summary>
public sealed record NotConsumable : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() => "not a consumable (IsConsumable is false)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        !product.IsConsumable;
}

/// <summary>Nothing the customer already owns, by SKU, on any line of their history.</summary>
public sealed record NotAlreadyOwned : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() => "not already owned (no purchase line carries this SKU)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        !customer.Owns(product.Id);
}

/// <summary>Ships to a named market — the customer's, which is not always Switzerland.</summary>
/// <param name="Market">Two-letter market code.</param>
public sealed record AvailableInMarket(string Market) : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() => $"available in market {Market} (AvailableMarkets)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        product.IsAvailableIn(Market);
}

/// <summary>
/// The category path starts with one of the given prefixes, segment-wise and case-insensitively.
/// <c>"Home Espresso"</c> matches the whole department; <c>"Photography > Lenses"</c> one group.
/// </summary>
/// <param name="Prefixes">One or more <c>" > "</c>-joined prefixes; ANY may match.</param>
public sealed record CategoryUnderAny(IReadOnlyList<string> Prefixes) : ProductConstraint
{
    /// <summary>Convenience constructor.</summary>
    /// <param name="prefixes">One or more <c>" > "</c>-joined prefixes.</param>
    public CategoryUnderAny(params string[] prefixes) : this((IReadOnlyList<string>)prefixes) { }

    /// <inheritdoc/>
    public override string Describe() => $"category under {string.Join(" OR ", Prefixes.Select(p => $"'{p}'"))} (CategoryPath prefix)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue)
    {
        foreach (string prefix in Prefixes)
            if (MatchesPrefix(product.CategoryPath, prefix)) return true;
        return false;
    }

    /// <summary>Segment-wise prefix test. Local, so the bar does not borrow the retriever's helper.</summary>
    /// <param name="path">A product's category path.</param>
    /// <param name="prefix">A <c>" > "</c>-joined prefix.</param>
    public static bool MatchesPrefix(IReadOnlyList<string> path, string prefix)
    {
        var wanted = prefix.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length == 0 || wanted.Length > path.Count) return false;
        for (int i = 0; i < wanted.Length; i++)
            if (!string.Equals(wanted[i], path[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <inheritdoc/>
    public bool Equals(CategoryUnderAny? other) =>
        other is not null && Prefixes.SequenceEqual(other.Prefixes, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() => string.Join("|", Prefixes).ToUpperInvariant().GetHashCode(StringComparison.Ordinal);
}

/// <summary>The leaf category is one of the named leaves.</summary>
/// <param name="Leaves">Leaf names; ANY may match.</param>
public sealed record LeafIn(IReadOnlyList<string> Leaves) : ProductConstraint
{
    /// <summary>Convenience constructor.</summary>
    /// <param name="leaves">Leaf names.</param>
    public LeafIn(params string[] leaves) : this((IReadOnlyList<string>)leaves) { }

    /// <inheritdoc/>
    public override string Describe() => $"leaf category in {{{string.Join(", ", Leaves)}}}";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        Leaves.Contains(product.LeafCategory, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public bool Equals(LeafIn? other) =>
        other is not null && Leaves.SequenceEqual(other.Leaves, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() => string.Join("|", Leaves).ToUpperInvariant().GetHashCode(StringComparison.Ordinal);
}

/// <summary>An EXCLUSION: no segment of the category path may be one of the named segments.</summary>
/// <param name="Segments">Segment names to exclude, e.g. <c>"Lighting"</c>, <c>"Tyres"</c>.</param>
public sealed record ExcludeCategorySegment(IReadOnlyList<string> Segments) : ProductConstraint
{
    /// <summary>Convenience constructor.</summary>
    /// <param name="segments">Segment names to exclude.</param>
    public ExcludeCategorySegment(params string[] segments) : this((IReadOnlyList<string>)segments) { }

    /// <inheritdoc/>
    public override string Describe() => $"NOT in {string.Join(" / ", Segments)} (no CategoryPath segment matches)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue)
    {
        foreach (string segment in product.CategoryPath)
            if (Segments.Contains(segment, StringComparer.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <inheritdoc/>
    public bool Equals(ExcludeCategorySegment? other) =>
        other is not null && Segments.SequenceEqual(other.Segments, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override int GetHashCode() => string.Join("|", Segments).ToUpperInvariant().GetHashCode(StringComparison.Ordinal);
}

/// <summary>
/// Physically compatible with something the customer OWNS — "a lens for my Alpha 7 IV",
/// "something for the 58 mm group".
/// </summary>
/// <remarks>
/// <para>
/// The rule is the one <c>GalaxusTools.FindComplements</c> enforces, RE-IMPLEMENTED here rather
/// than called: a candidate is compatible when it declares no <c>compat:</c> tag at all (a
/// universal accessory) or shares at least one <c>compat:</c> value with the anchor, and never
/// from the anchor's own leaf category (another machine is not an accessory). Two implementations
/// of one rule, on purpose — the artifact under test must not supply the bar it is graded against,
/// and the two agreeing is part of what a green run shows.
/// </para>
/// <para>
/// <c>compat:</c> tags are catalogue STRUCTURE, not retrieval vocabulary: <c>EmbeddingDocument</c>
/// excludes them from the index by design, so a constraint on them cannot be answered by the
/// tag join that answers Eval 02.
/// </para>
/// </remarks>
/// <param name="OwnedSku">The anchor the customer owns. Validated against their history.</param>
public sealed record CompatibleWithOwned(string OwnedSku) : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() =>
        $"compatible with owned {OwnedSku} (shares a compat: value, or declares none; never the anchor's own leaf)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue)
    {
        var anchor = catalogue.Require(OwnedSku);

        if (string.Equals(product.LeafCategory, anchor.LeafCategory, StringComparison.OrdinalIgnoreCase)) return false;

        var candidateCompat = CompatTokens(product);
        if (candidateCompat.Count == 0) return true;

        var anchorCompat = CompatTokens(anchor);
        if (anchorCompat.Count == 0) return false;

        return candidateCompat.Overlaps(anchorCompat);
    }

    /// <summary>The normalised <c>compat:</c> values a product declares. Empty means universal.</summary>
    /// <param name="product">A catalogue product.</param>
    public static HashSet<string> CompatTokens(Product product)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string tag in product.Tags)
        {
            if (!tag.StartsWith("compat:", StringComparison.OrdinalIgnoreCase)) continue;
            string token = Product.NormalizeAttributeToken(tag["compat:".Length..]);
            if (token.Length > 0) set.Add(token);
        }
        return set;
    }
}

/// <summary>A spec the product MUST carry, whose value contains the needle (case-insensitive). No spec ⇒ not satisfied.</summary>
/// <param name="Key">The leaf-schema spec key, e.g. <c>"Players"</c>.</param>
/// <param name="Needle">Substring the value must contain.</param>
public sealed record SpecContains(string Key, string Needle) : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() => $"spec '{Key}' contains \"{Needle}\" (a product without the spec does NOT satisfy)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        product.Specs.TryGetValue(Key, out var value) && value.Contains(Needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An EXCLUSION on a spec value: if the product carries the spec, its value must NOT contain the
/// needle. A product without the spec makes no such claim and passes.
/// </summary>
/// <param name="Key">The spec key, e.g. <c>"Connection"</c>.</param>
/// <param name="Needle">Substring that disqualifies, e.g. <c>"Bluetooth"</c>.</param>
public sealed record SpecExcludes(string Key, string Needle) : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() => $"spec '{Key}' must NOT contain \"{Needle}\" (a product without the spec passes)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        !product.Specs.TryGetValue(Key, out var value) || !value.Contains(Needle, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Weight at or under a ceiling, parsed from the <c>Weight</c> spec ("353 g", "1.13 kg",
/// "290 g per pair"). A product with no weight spec cannot be verified and does NOT satisfy.
/// </summary>
/// <param name="Grams">Ceiling in grams, inclusive.</param>
public sealed partial record MaxWeightGrams(int Grams) : ProductConstraint
{
    /// <inheritdoc/>
    public override string Describe() => $"weight ≤ {Grams} g (parsed from the 'Weight' spec; no spec ⇒ not satisfied)";

    /// <inheritdoc/>
    public override bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue) =>
        TryParseGrams(product, out double grams) && grams <= Grams;

    /// <summary>Parses the first "&lt;number&gt; g|kg" in the product's Weight spec.</summary>
    /// <param name="product">A catalogue product.</param>
    /// <param name="grams">The parsed weight in grams.</param>
    public static bool TryParseGrams(Product product, out double grams)
    {
        grams = double.NaN;
        if (!product.Specs.TryGetValue("Weight", out var raw)) return false;

        var match = WeightPattern().Match(raw);
        if (!match.Success) return false;

        double value = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        grams = match.Groups[2].Value.Equals("kg", StringComparison.OrdinalIgnoreCase) ? value * 1000.0 : value;
        return true;
    }

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(kg|g)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WeightPattern();
}

/// <summary>
/// One slot of a need: an AND of constraints. A case with two slots is a cross-category
/// assembly ("speakers AND stands"); a presented item satisfies the case when it satisfies ALL
/// constraints of ANY slot.
/// </summary>
/// <param name="Label">What the slot is for, in the customer's words.</param>
/// <param name="Constraints">The conjunction.</param>
public sealed record ConstraintSlot(string Label, IReadOnlyList<ProductConstraint> Constraints)
{
    /// <summary>True when every constraint of this slot holds.</summary>
    /// <param name="product">A catalogue product.</param>
    /// <param name="customer">The customer.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    public bool Satisfies(Product product, CustomerProfile customer, Catalogue catalogue)
    {
        foreach (var constraint in Constraints)
            if (!constraint.Satisfies(product, customer, catalogue)) return false;
        return true;
    }
}

/// <summary>
/// One Eval 02b case: a persona, a natural-language need with several hard constraints, and the
/// code that checks them. The gold is <see cref="Slots"/>; the utterance is what the arms see.
/// </summary>
/// <param name="Id">Case id, <c>SN-01</c>…</param>
/// <param name="PersonaId">The customer speaking.</param>
/// <param name="Name">Display name.</param>
/// <param name="Utterance">The customer's own words — Swiss-shopper register, no SKU, no id.</param>
/// <param name="Slots">The codified constraints. ANY slot fully satisfied ⇒ the item satisfies the need.</param>
/// <param name="Note">Why this case is shaped this way — printed, never hidden.</param>
public sealed record StatedNeedCase(
    string Id,
    string PersonaId,
    string Name,
    string Utterance,
    IReadOnlyList<ConstraintSlot> Slots,
    string Note)
{
    /// <summary>The framed prompt sent to every arm — the same constant frame Eval 02 uses.</summary>
    public string Prompt => GalaxusEvalPrompt.For(PersonaId, Utterance);

    /// <summary>Distinct constraints across every slot. The fixture requires at least three.</summary>
    public int DistinctConstraintCount =>
        Slots.SelectMany(s => s.Constraints).Distinct().Count();

    /// <summary>True when the product satisfies every constraint of at least one slot.</summary>
    /// <param name="product">A catalogue product.</param>
    /// <param name="customer">The customer.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    public bool IsSatisfiedBy(Product product, CustomerProfile customer, Catalogue catalogue) =>
        FirstSatisfiedSlot(product, customer, catalogue) >= 0;

    /// <summary>Index of the first slot the product fully satisfies, or -1.</summary>
    /// <param name="product">A catalogue product.</param>
    /// <param name="customer">The customer.</param>
    /// <param name="catalogue">The catalogue façade.</param>
    public int FirstSatisfiedSlot(Product product, CustomerProfile customer, Catalogue catalogue)
    {
        for (int i = 0; i < Slots.Count; i++)
            if (Slots[i].Satisfies(product, customer, catalogue)) return i;
        return -1;
    }
}
