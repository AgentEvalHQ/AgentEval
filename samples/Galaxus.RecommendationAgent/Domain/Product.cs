// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text;

namespace Galaxus.RecommendationAgent.Domain;

/// <summary>
/// A sellable item. Mirrors the fields a Galaxus marketplace feed is actually
/// required to carry: a valid GTIN, a leaf category with a filled attribute
/// schema, and a description with no promotional language.
/// </summary>
/// <remarks>
/// <para>
/// This is the ONE shared domain type (design §0.5 / D-1): both
/// <c>Galaxus.RecommendationAgent</c> and <c>Galaxus.RecommendationAgent.Evals</c>
/// code against this record, so the eval lane cannot drift into a second,
/// incompatible product model.
/// </para>
/// <para>
/// The eval lane's contract (§C.0 / R-1) asks for <c>Sku</c>, <c>LeafCategory</c>,
/// <c>Attributes</c> and <c>ReviewIds</c>. None of §A's field names were renamed to
/// satisfy it: <see cref="Sku"/> and <see cref="LeafCategory"/> are computed
/// projections of <see cref="Id"/> and <see cref="CategoryPath"/>,
/// <see cref="Attributes"/> is a derived token set fused from <see cref="Tags"/>
/// and <see cref="Specs"/>, and <see cref="ReviewIds"/> is an additive init-only
/// property the catalogue façade fills in from the review seed.
/// </para>
/// </remarks>
public sealed record Product
{
    /// <summary>Internal SKU, e.g. <c>"GLX-1042"</c>. Stable, ordinal-compared, never reused.</summary>
    public required string Id { get; init; }

    /// <summary>EAN-13. No GTIN ⇒ no listing (a real Galaxus feed rule).</summary>
    public required string Gtin { get; init; }

    /// <summary>Product title as it appears on the page.</summary>
    public required string Name { get; init; }

    /// <summary>Brand name. Near-miss brands are a fabrication temptation — see the near-miss eval case.</summary>
    public required string Brand { get; init; }

    /// <summary>Leaf-first-readable path, e.g. ["Photography", "Lenses", "Wide-angle zoom"].</summary>
    public required IReadOnlyList<string> CategoryPath { get; init; }

    /// <summary>Current price in Swiss francs. The MODEL may never state this — see §F.4.</summary>
    public required decimal PriceChf { get; init; }

    /// <summary>Strike-through price; null when the product was never discounted.</summary>
    public decimal? WasPriceChf { get; init; }

    /// <summary>Leaf-schema attributes. Keys are stable per category (see <see cref="Category.AttributeSchema"/>).</summary>
    public required IReadOnlyDictionary<string, string> Specs { get; init; }

    /// <summary>Factual prose, ≤ 4000 chars, no HTML, no superlatives, no prices — Galaxus feed rules.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// USE-CONTEXT tags, not category synonyms. These are the cross-category bridge
    /// (see §D.1): "context:golden-hour", "trip:multi-day", "weight:packable",
    /// "skill:enthusiast", "compat:sony-e-mount", "consumable:true".
    /// </summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>0..5, verified purchases only.</summary>
    public required double RatingAverage { get; init; }

    /// <summary>Number of verified-purchase ratings. Zero ⇒ cold start (see <see cref="IsColdStart"/>).</summary>
    public required int RatingCount { get; init; }

    /// <summary>Helpfulness-weighting input for the review digest.</summary>
    public required int HelpfulVoteTotal { get; init; }

    /// <summary>Units on hand. Zero ⇒ a presentation must carry <c>outOfStock: true</c> (defect class D2).</summary>
    public required int StockUnits { get; init; }

    /// <summary>Markets this SKU can ship to: "CH", "DE", "AT", "IT", "FR", "BE", "NL".</summary>
    public required IReadOnlyList<string> AvailableMarkets { get; init; }

    /// <summary>"A"…"G", null where the energy class does not apply.</summary>
    public string? EnergyLabel { get; init; }

    /// <summary>Repairability / recycled-material / certification claims.</summary>
    public required Sustainability Sustainability { get; init; }

    /// <summary>Year of first release. Drives the durable-churn suppression rule.</summary>
    public required int ReleaseYear { get; init; }

    /// <summary>Non-null ⇒ marketplace SKU ⇒ the COLD-START plant (§B.1).</summary>
    public string? MarketplaceSeller { get; init; }

    /// <summary>True for refurbished / second-hand listings.</summary>
    public bool IsSecondHand { get; init; }

    /// <summary>True for beans, filters, cartridges, descaler — drives the replenishment lane, never discovery.</summary>
    public bool IsConsumable { get; init; }

    /// <summary>Typical days between repurchases for a consumable; null otherwise.</summary>
    public int? TypicalReplenishDays { get; init; }

    /// <summary>
    /// Ids of the reviews written about this product. Additive over §A.1 to satisfy
    /// the eval lane's R-1/R-5: <c>evidence = "review:&lt;id&gt;"</c> is resolved by
    /// membership in this set.
    /// </summary>
    /// <remarks>
    /// <c>CatalogueSeed</c> authors products WITHOUT review ids; the <c>Catalogue</c>
    /// façade fuses them in from <c>ReviewSeed</c> with a <c>with { ReviewIds = … }</c>
    /// expression at load, so there is exactly one place where a review id is written down.
    /// </remarks>
    public IReadOnlySet<string> ReviewIds { get; init; } = EmptyStringSet;

    // ── Derived projections. All computed — none of them widen the authored surface. ──

    /// <summary>
    /// Alias for <see cref="Id"/>. The eval lane's contract (R-1) says <c>Sku</c>;
    /// §A.1 says <c>Id</c>. Both names now address the same value.
    /// </summary>
    public string Sku => Id;

    /// <summary>
    /// The last element of <see cref="CategoryPath"/> — the leaf the suppression and
    /// coverage gold sets are computed over (eval defect class D3, gold rules R1/R2/R3).
    /// </summary>
    public string LeafCategory => CategoryPath[^1];

    /// <summary>The first element of <see cref="CategoryPath"/> — the top-level department.</summary>
    public string RootCategory => CategoryPath[0];

    /// <summary>
    /// The fused, normalised attribute-token set: everything an <c>attr:&lt;token&gt;</c>
    /// evidence citation may legitimately resolve against (eval R-5, defect class D5).
    /// </summary>
    /// <remarks>
    /// <para>Built from FIVE sources, every one of them normalised by
    /// <see cref="NormalizeAttributeToken"/>:</para>
    /// <list type="number">
    ///   <item>each tag verbatim — <c>"context:golden-hour"</c>;</item>
    ///   <item>for a tag containing ':', the part AFTER the first colon — <c>"golden-hour"</c>;</item>
    ///   <item>each spec KEY — <c>"Filter thread"</c> → <c>"filter-thread"</c>;</item>
    ///   <item>each spec VALUE — <c>"82 mm"</c> → <c>"82-mm"</c>;</item>
    ///   <item>each spec KEY=VALUE pair — <c>"filter-thread=82-mm"</c>.</item>
    /// </list>
    /// <para>
    /// This makes "always cites its evidence" NON-GAMEABLE: plausible prose cannot pass,
    /// because the token has to be present in the catalogue record. A model that invents a
    /// flattering attribute fails the check harder, not softer (§F.3).
    /// </para>
    /// <para>
    /// DERIVED ON EVERY ACCESS — deliberately not cached in an instance field, because a
    /// private cache field would be pulled into the compiler-generated record equality and
    /// two identical products could then compare unequal purely because one had been read
    /// from. Hoist it out of hot loops (<c>var attrs = p.Attributes;</c>) or memoise it in
    /// the <c>Catalogue</c> façade, which owns product identity.
    /// </para>
    /// </remarks>
    public IReadOnlySet<string> Attributes
    {
        get
        {
            var set = new HashSet<string>(StringComparer.Ordinal);

            foreach (var tag in Tags)
            {
                var whole = NormalizeAttributeToken(tag);
                if (whole.Length > 0) set.Add(whole);

                var colon = tag.IndexOf(':');
                if (colon >= 0 && colon < tag.Length - 1)
                {
                    var suffix = NormalizeAttributeToken(tag[(colon + 1)..]);
                    if (suffix.Length > 0) set.Add(suffix);
                }
            }

            foreach (var (key, value) in Specs)
            {
                var k = NormalizeAttributeToken(key);
                var v = NormalizeAttributeToken(value);
                if (k.Length > 0) set.Add(k);
                if (v.Length > 0) set.Add(v);
                if (k.Length > 0 && v.Length > 0) set.Add($"{k}={v}");
            }

            return set;
        }
    }

    /// <summary>True when no verified purchase has ever rated this SKU — the case a pure interaction-based recommender is structurally blind to.</summary>
    public bool IsColdStart => RatingCount == 0;

    /// <summary>True when the SKU is sold by a marketplace seller rather than by Galaxus itself.</summary>
    public bool IsMarketplaceOffer => MarketplaceSeller is { Length: > 0 };

    /// <summary>True when stock is on hand right now.</summary>
    public bool InStock => StockUnits > 0;

    /// <summary>True when <see cref="WasPriceChf"/> is set and is strictly above the current price.</summary>
    public bool IsDiscounted => WasPriceChf is { } was && was > PriceChf;

    /// <summary>Ordinal, case-insensitive market check against <see cref="AvailableMarkets"/>.</summary>
    /// <param name="market">Two-letter market code, e.g. <c>"CH"</c>.</param>
    public bool IsAvailableIn(string market)
    {
        for (int i = 0; i < AvailableMarkets.Count; i++)
            if (string.Equals(AvailableMarkets[i], market, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Looks up a spec or a tag by key and returns its catalogue value. Tags are
    /// addressable both by their whole text and by their <c>prefix:</c> part.
    /// Used by the two-sided evidence check (§F.3), which compares the model's stated
    /// value against the value returned here.
    /// </summary>
    /// <param name="attributeKey">A spec key, a whole tag, or a tag prefix.</param>
    /// <param name="value">The catalogue value on success; null otherwise.</param>
    /// <returns>True when the key resolves in <see cref="Specs"/> or <see cref="Tags"/>.</returns>
    public bool TryGetAttributeValue(string attributeKey, out string? value)
    {
        if (Specs.TryGetValue(attributeKey, out var direct)) { value = direct; return true; }

        var wanted = NormalizeAttributeToken(attributeKey);

        foreach (var (k, v) in Specs)
            if (string.Equals(NormalizeAttributeToken(k), wanted, StringComparison.Ordinal))
            { value = v; return true; }

        foreach (var tag in Tags)
        {
            if (string.Equals(NormalizeAttributeToken(tag), wanted, StringComparison.Ordinal))
            { value = tag; return true; }

            var colon = tag.IndexOf(':');
            if (colon > 0 &&
                string.Equals(NormalizeAttributeToken(tag[..colon]), wanted, StringComparison.Ordinal))
            { value = tag[(colon + 1)..]; return true; }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// THE shared token normaliser. Both sides of every evidence check run text through
    /// this, so <c>attr:&lt;token&gt;</c> written by the agent and the token set built
    /// from the catalogue can never disagree on casing or spacing.
    /// </summary>
    /// <remarks>
    /// Rules, in order: trim; lower-case with the invariant culture; map whitespace,
    /// <c>_</c>, <c>/</c>, <c>\</c> and <c>,</c> to <c>-</c>; keep only
    /// <c>[a-z0-9]</c>, <c>-</c>, <c>.</c>, <c>:</c>, <c>+</c>, <c>=</c>; collapse
    /// runs of <c>-</c>; trim leading and trailing <c>-</c>. Deterministic and
    /// allocation-light; returns <see cref="string.Empty"/> for input that normalises away.
    /// </remarks>
    /// <param name="raw">Any spec key, spec value, tag, or agent-written evidence token.</param>
    public static string NormalizeAttributeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var span = raw.AsSpan().Trim();
        var sb   = new StringBuilder(span.Length);
        bool lastWasDash = false;

        foreach (var ch in span)
        {
            char c = char.ToLowerInvariant(ch);

            if (char.IsWhiteSpace(c) || c is '_' or '/' or '\\' or ',')
                c = '-';

            bool keep = (c >= 'a' && c <= 'z')
                     || (c >= '0' && c <= '9')
                     || c is '-' or '.' or ':' or '+' or '=';
            if (!keep) continue;

            if (c == '-')
            {
                if (lastWasDash || sb.Length == 0) continue;
                lastWasDash = true;
            }
            else
            {
                lastWasDash = false;
            }

            sb.Append(c);
        }

        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }

    /// <summary>Shared empty set for <see cref="ReviewIds"/>. Static ⇒ outside record equality.</summary>
    private static readonly IReadOnlySet<string> EmptyStringSet = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Sustainability claims carried on a listing. All three are feed-supplied facts,
/// never model-inferred.
/// </summary>
/// <param name="RepairabilityDocumented">True when the seller published a repairability score or a spare-parts commitment.</param>
/// <param name="RecycledMaterials">True when the listing declares recycled input material.</param>
/// <param name="Certification">"Bluesign", "FSC", "Fairtrade", or null when uncertified.</param>
public sealed record Sustainability(
    bool RepairabilityDocumented,
    bool RecycledMaterials,
    string? Certification);

/// <summary>
/// One node of the category tree. Leaves carry the attribute schema every product in
/// them must fill, and the flag that governs sensitive-inference suppression.
/// </summary>
/// <param name="Id">Stable category id, e.g. <c>"CAT-PHO-LENS-WIDE"</c>.</param>
/// <param name="Path">Full readable path from the root, e.g. ["Photography", "Lenses", "Wide-angle zoom"].</param>
/// <param name="ParentId">Parent category id; null for a root department.</param>
/// <param name="AttributeSchema">The attribute keys every product in this leaf MUST fill. Deliberately per-leaf.</param>
/// <param name="SensitiveInference">
/// True ⇒ never surfaced by INFERENCE; see the sensitive blocklist in §F.5. The category
/// stays browsable and searchable — it is the unsolicited inference that is blocked, not
/// the category. Swiss revDSG Art. 5(c) and GDPR Art. 9 treat INFERRING a special category
/// from behaviour as processing it.
/// </param>
public sealed record Category(
    string Id,
    IReadOnlyList<string> Path,
    string? ParentId,
    IReadOnlyList<string> AttributeSchema,
    bool SensitiveInference)
{
    /// <summary>The last element of <see cref="Path"/> — matches <see cref="Product.LeafCategory"/>.</summary>
    public string LeafName => Path[^1];

    /// <summary>The first element of <see cref="Path"/> — the top-level department.</summary>
    public string RootName => Path[0];

    /// <summary>Depth in the tree; a root department is 1.</summary>
    public int Depth => Path.Count;
}
