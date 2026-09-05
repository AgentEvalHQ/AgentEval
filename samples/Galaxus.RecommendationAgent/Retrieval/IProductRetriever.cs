// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// THE retrieval seam (design §G): the semantic tools of <c>Tools/GalaxusTools.cs</c> call this
/// and nothing else, so the local brute-force index can be swapped for a real vector store
/// (Microsoft.Extensions.VectorData / Azure AI Search) without touching a single tool.
/// </summary>
/// <remarks>
/// <para>
/// One method, deliberately. <c>FindSimilarProducts</c> and <c>FindComplements</c> (§C.2) are
/// NOT extra seam methods — they are ordinary <see cref="RetrievalQuery"/> values built by
/// <see cref="RetrievalQuery.SimilarTo"/> and <see cref="RetrievalQuery.ComplementsOf"/>.
/// That keeps the compatibility gate where the design insists it lives: in the caller's code,
/// as a hard predicate (<see cref="RetrievalQuery.HardFilter"/>), never as a soft vector signal.
/// </para>
/// <para>
/// <b>Contract for any implementation.</b> Filters are applied as a PRE-filter, before the
/// top-k cut (§D.2). Post-filtering after top-k silently returns fewer than k results and
/// quietly degrades recall on exactly the constrained queries this demo exists to showcase —
/// which would fail in the flattering direction, because the console would still print a
/// short, confident list.
/// </para>
/// </remarks>
public interface IProductRetriever
{
    /// <summary>Short identifier for the console and the diagnostics block, e.g. <c>"hybrid(concept+lexical)"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// False when the dense leg could not be built at all (no embedding source, empty index).
    /// A retriever with <c>DenseAvailable == false</c> answers every query in degraded,
    /// lexical-only mode and says so in <see cref="RetrievalDiagnostics.Degraded"/>.
    /// </summary>
    bool DenseAvailable { get; }

    /// <summary>How many products are searchable.</summary>
    int ProductCount { get; }

    /// <summary>
    /// Retrieve ranked candidates for a need. Candidates are <i>recall-oriented suggestions,
    /// not facts</i> (§C.1) — the caller confirms anything it intends to state through the
    /// structured tools.
    /// </summary>
    /// <param name="query">The need plus its hard pre-filters.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>At most <see cref="RetrievalQuery.EffectiveTopK"/> hits, best first, plus the honesty block.</returns>
    ValueTask<RetrievalResult> SearchAsync(RetrievalQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// A retrieval request: the need in natural language, plus every hard constraint that must be
/// applied BEFORE the top-k cut.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Need"/> carries the semantics; everything else is a gate. Price, stock, market
/// and category are gates rather than vector signals on purpose — embeddings are computed once
/// and prices change hourly (§C.1), so anything time-varying is enforced in code at query time.
/// </para>
/// <para>
/// <see cref="AnchorProductId"/> switches the dense leg from "embed <see cref="Need"/>" to
/// "use the anchor's stored vector". That is what makes <c>FindSimilarProducts</c> a
/// true nearest-neighbour query rather than a paraphrase of the anchor's name.
/// </para>
/// </remarks>
public sealed record RetrievalQuery
{
    /// <summary>Smallest accepted <see cref="TopK"/>.</summary>
    public const int MinTopK = 1;

    /// <summary>Default <see cref="TopK"/> — the §D.3 table's "final topK".</summary>
    public const int DefaultTopK = 8;

    /// <summary>Hard ceiling on <see cref="TopK"/> — the §D.3 table's "(max 12)".</summary>
    public const int MaxTopK = 12;

    /// <summary>Market assumed when the caller does not say. Galaxus's home market.</summary>
    public const string DefaultMarket = "CH";

    private static readonly IReadOnlyCollection<string> NoIds = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The customer's situation, in one or two full sentences. Longer and more specific beats keywords.</summary>
    public required string Need { get; init; }

    /// <summary>
    /// Optional category path prefix, e.g. <c>"Photography"</c> or <c>"Photography &gt; Lenses"</c>.
    /// Null searches everything — which is the whole point of the cross-category demo, so callers
    /// should leave it null unless the customer named a category.
    /// </summary>
    public string? CategoryPathPrefix { get; init; }

    /// <summary>Optional price ceiling in CHF, applied against the catalogue price (never a model-stated one).</summary>
    public decimal? MaxPriceChf { get; init; }

    /// <summary>When true (default), only products with stock on hand are returned.</summary>
    public bool InStockOnly { get; init; } = true;

    /// <summary>Two-letter market code the customer buys in. Products not listed in it are gated out.</summary>
    public string Market { get; init; } = DefaultMarket;

    /// <summary>Requested candidate count. Clamped by <see cref="EffectiveTopK"/>; never trusted raw.</summary>
    public int TopK { get; init; } = DefaultTopK;

    /// <summary>Product ids that must not come back — the anchor of a similarity query, items already presented.</summary>
    public IReadOnlyCollection<string> ExcludeProductIds { get; init; } = NoIds;

    /// <summary>
    /// An extra hard predicate, evaluated with the other gates BEFORE top-k. This is where the
    /// deterministic compatibility gate lives (§C.2): a 54 mm portafilter can never be returned
    /// for a 58 mm machine, regardless of what the model asks for.
    /// </summary>
    public Func<Product, bool>? HardFilter { get; init; }

    /// <summary>
    /// When set, the dense leg searches from this product's stored vector instead of embedding
    /// <see cref="Need"/>. Ignored when the id is not in the index (the leg falls back to
    /// embedding <see cref="Need"/>, which is still meaningful).
    /// </summary>
    public string? AnchorProductId { get; init; }

    /// <summary><see cref="TopK"/> clamped into <see cref="MinTopK"/>..<see cref="MaxTopK"/>.</summary>
    public int EffectiveTopK => Math.Clamp(TopK, MinTopK, MaxTopK);

    /// <summary>Shorthand for a plain semantic search with every default.</summary>
    /// <param name="need">The need, in full sentences.</param>
    public static RetrievalQuery For(string need) => new() { Need = need };

    /// <summary>
    /// Builds the query behind <c>FindSimilarProducts</c> (§C.2): same job, different trade-offs.
    /// Anchors the dense leg on the product's own vector, excludes the anchor, and excludes other
    /// variants of the same model via <see cref="IsSameModelVariant"/> — "use to offer alternatives,
    /// not to pad a list".
    /// </summary>
    /// <param name="anchor">The product to find neighbours of.</param>
    /// <param name="topK">How many neighbours. Clamped to 1..12.</param>
    /// <param name="market">Market gate.</param>
    /// <param name="inStockOnly">Whether to require stock.</param>
    public static RetrievalQuery SimilarTo(Product anchor, int topK = 5, string market = DefaultMarket, bool inStockOnly = true)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return new RetrievalQuery
        {
            Need = EmbeddingDocument.ForProduct(anchor),
            AnchorProductId = anchor.Id,
            TopK = topK,
            Market = market,
            InStockOnly = inStockOnly,
            ExcludeProductIds = new HashSet<string>(StringComparer.Ordinal) { anchor.Id },
            HardFilter = candidate => !IsSameModelVariant(anchor, candidate),
        };
    }

    /// <summary>
    /// Builds the query behind <c>FindComplements</c> (§C.2): an accessory query document composed
    /// from the anchor's <c>Use:</c> line plus the caller's extra need, gated by a HARD compatibility
    /// predicate supplied by the caller.
    /// </summary>
    /// <remarks>
    /// The compatibility predicate is a parameter, not an inferred signal, because §C.2 is explicit:
    /// "the compatibility gate is code". Passing <c>null</c> is legal but means nothing is gated —
    /// callers wiring the real tool must pass the <c>compat:</c> tag predicate.
    /// </remarks>
    /// <param name="anchor">The product being accessorised.</param>
    /// <param name="need">Optional extra steer, e.g. "long-exposure water at dawn".</param>
    /// <param name="compatible">The deterministic compatibility gate, evaluated per candidate.</param>
    /// <param name="topK">How many complements. Clamped to 1..12.</param>
    /// <param name="market">Market gate.</param>
    public static RetrievalQuery ComplementsOf(
        Product anchor,
        string? need,
        Func<Product, bool>? compatible,
        int topK = 5,
        string market = DefaultMarket)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return new RetrievalQuery
        {
            Need = EmbeddingDocument.ForAccessoryQuery(anchor, need),
            TopK = topK,
            Market = market,
            InStockOnly = true,
            ExcludeProductIds = new HashSet<string>(StringComparer.Ordinal) { anchor.Id },
            HardFilter = compatible,
        };
    }

    /// <summary>
    /// THE pre-filter. Every leg calls this same predicate, so the dense and lexical legs can never
    /// disagree about which products were eligible — a disagreement would make RRF fuse two
    /// differently-populated candidate pools and quietly bias the result.
    /// </summary>
    /// <param name="product">Candidate.</param>
    /// <returns>True when the product passes every hard gate on this query.</returns>
    public bool Matches(Product product)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (ExcludeProductIds.Count > 0 && ExcludeProductIds.Contains(product.Id)) return false;
        if (InStockOnly && !product.InStock) return false;
        if (!string.IsNullOrWhiteSpace(Market) && !product.IsAvailableIn(Market)) return false;
        if (MaxPriceChf is { } ceiling && product.PriceChf > ceiling) return false;
        if (!MatchesCategoryPrefix(product, CategoryPathPrefix)) return false;
        if (HardFilter is { } extra && !extra(product)) return false;

        return true;
    }

    /// <summary>The pre-filter as a delegate, for <c>ProductVectorIndex.Search</c> and <c>LexicalIndex.Search</c>.</summary>
    public Func<Product, bool> ToPredicate() => Matches;

    /// <summary>
    /// Segment-wise, case-insensitive category prefix test. Segment-wise on purpose: a raw
    /// <c>StartsWith</c> would make the prefix "Photo" match "Photography", and "Home Audio"
    /// match "Home Audio Accessories" in a way the caller did not ask for.
    /// </summary>
    /// <param name="product">Candidate.</param>
    /// <param name="prefix">Path prefix such as <c>"Photography &gt; Lenses"</c>, or null/blank for "everything".</param>
    public static bool MatchesCategoryPrefix(Product product, string? prefix)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (string.IsNullOrWhiteSpace(prefix)) return true;

        var wanted = prefix.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length == 0) return true;
        if (wanted.Length > product.CategoryPath.Count) return false;

        for (int i = 0; i < wanted.Length; i++)
        {
            if (!string.Equals(wanted[i], product.CategoryPath[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when two products look like variants of one model — same brand, and one product's
    /// squashed name contains the other's model-number token. Used so "similar products" offers
    /// genuine alternatives rather than the 512 GB version of what the customer is already reading.
    /// </summary>
    /// <param name="anchor">The anchor product.</param>
    /// <param name="candidate">The candidate neighbour.</param>
    public static bool IsSameModelVariant(Product anchor, Product candidate)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(candidate);

        if (string.Equals(anchor.Id, candidate.Id, StringComparison.Ordinal)) return true;
        if (!string.Equals(anchor.Brand, candidate.Brand, StringComparison.OrdinalIgnoreCase)) return false;

        var anchorSquashed    = LexicalIndex.Squash(anchor.Name);
        var candidateSquashed = LexicalIndex.Squash(candidate.Name);
        if (anchorSquashed.Length == 0 || candidateSquashed.Length == 0) return false;

        foreach (var token in LexicalIndex.Tokenize(anchor.Name))
        {
            if (!LexicalIndex.LooksLikeModelNumber(token)) continue;
            var squashedToken = LexicalIndex.Squash(token);
            if (squashedToken.Length >= 3 && candidateSquashed.Contains(squashedToken, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

/// <summary>
/// One ranked candidate. Mirrors the <c>hits[]</c> element of the
/// <c>SearchProductsByMeaning</c> payload (§C.2) exactly.
/// </summary>
/// <param name="ProductId">Catalogue id — the only field downstream code is allowed to trust as an identity.</param>
/// <param name="Name">Product title, for the console.</param>
/// <param name="Brand">Brand, for the console.</param>
/// <param name="CategoryPath">Full path, so the caller can see the cross-category jump.</param>
/// <param name="Score">The FUSED score (RRF). Not a probability, not a cosine, not comparable across queries.</param>
/// <param name="MatchedOn">The highest-contributing line of the embedding document, e.g. <c>"Use: trip:multi-day, weight:packable"</c>.</param>
/// <remarks>
/// There is deliberately NO price and NO stock field here. Price and stock never travel through
/// the semantic leg (§C.1) — embeddings are index-time, prices are call-time, and
/// <c>CheckStockAndPrice</c> is the only authority.
/// </remarks>
public sealed record RetrievalHit(
    string ProductId,
    string Name,
    string Brand,
    IReadOnlyList<string> CategoryPath,
    double Score,
    string MatchedOn)
{
    /// <summary>1-based rank in the dense leg, or null when the dense leg did not return it.</summary>
    public int? DenseRank { get; init; }

    /// <summary>1-based rank in the lexical leg, or null when the lexical leg did not return it.</summary>
    public int? LexicalRank { get; init; }

    /// <summary>Raw cosine from the dense leg (0 when absent). Diagnostic only — RRF ignores it.</summary>
    public double DenseScore { get; init; }

    /// <summary>Raw weighted-overlap score from the lexical leg (0 when absent). Diagnostic only.</summary>
    public double LexicalScore { get; init; }

    /// <summary>True when BOTH legs returned this product — the strongest available agreement signal.</summary>
    public bool FoundByBothLegs => DenseRank is not null && LexicalRank is not null;

    /// <summary>Human-readable category path, <c>"Photography &gt; Filters"</c>.</summary>
    public string CategoryPathText => string.Join(" > ", CategoryPath);
}

/// <summary>
/// The honesty block returned with every search — the <c>retrieval</c> object of §C.2's payload.
/// It exists so a degraded run cannot look like a healthy one.
/// </summary>
public sealed record RetrievalDiagnostics
{
    /// <summary>True when the dense (vector) leg actually ran and contributed ranks.</summary>
    public bool Dense { get; init; }

    /// <summary>True when the lexical leg ran. It always does — it needs no model and no key.</summary>
    public bool Lexical { get; init; } = true;

    /// <summary>Fusion identifier, <c>"rrf-k60"</c>, or <c>"lexical-only"</c> when the dense leg was off.</summary>
    public string Fusion { get; init; } = HybridRetriever.FusionName;

    /// <summary>
    /// THE flag from §D.4. True means the dense leg could not run, so cross-category matches
    /// will be missed. The console prints a banner when this is true; it is never silent.
    /// </summary>
    public bool Degraded { get; init; }

    /// <summary>Why the run degraded, in one sentence. Null when <see cref="Degraded"/> is false.</summary>
    public string? DegradedReason { get; init; }

    /// <summary>Candidates the dense leg contributed after the score floor.</summary>
    public int DenseCandidates { get; init; }

    /// <summary>Candidates the lexical leg contributed.</summary>
    public int LexicalCandidates { get; init; }

    /// <summary>How many dense candidates were discarded by the score floor. A large number here is a calibration signal.</summary>
    public int DenseBelowFloor { get; init; }

    /// <summary>
    /// The cosine floor applied to the dense leg. <b>UNMEASURED</b> — see
    /// <see cref="HybridRetriever.DefaultDenseScoreFloor"/>. Reported so nobody has to guess
    /// which number produced a given result set.
    /// </summary>
    public double DenseScoreFloor { get; init; }

    /// <summary>How many products survived the pre-filter and were therefore actually scored.</summary>
    public int Considered { get; init; }

    /// <summary>Which embedding source backed the dense leg, e.g. <c>"concept"</c> or <c>"azure"</c>, or <c>"none"</c>.</summary>
    public string EmbeddingSource { get; init; } = "none";

    /// <summary>The embedding model identifier the vectors came from — the thing a stale cache would disagree with.</summary>
    public string EmbeddingModel { get; init; } = "none";

    /// <summary>The <see cref="EmbeddingDocument.TemplateVersion"/> the index was built against.</summary>
    public string DocumentTemplateVersion { get; init; } = EmbeddingDocument.TemplateVersion;

    /// <summary>A lexical-only, degraded diagnostics block with a stated reason.</summary>
    /// <param name="reason">Why the dense leg could not run.</param>
    /// <param name="lexicalCandidates">How many candidates the lexical leg produced.</param>
    /// <param name="considered">How many products survived the pre-filter.</param>
    public static RetrievalDiagnostics LexicalOnly(string reason, int lexicalCandidates = 0, int considered = 0) => new()
    {
        Dense = false,
        Lexical = true,
        Fusion = "lexical-only",
        Degraded = true,
        DegradedReason = reason,
        LexicalCandidates = lexicalCandidates,
        Considered = considered,
    };
}

/// <summary>The result of one retrieval: the ranked hits plus the honesty block.</summary>
/// <param name="Hits">Best first, at most <see cref="RetrievalQuery.EffectiveTopK"/> of them.</param>
/// <param name="Retrieval">Diagnostics — serialised under the <c>retrieval</c> key in the tool payload.</param>
public sealed record RetrievalResult(IReadOnlyList<RetrievalHit> Hits, RetrievalDiagnostics Retrieval)
{
    /// <summary>How many hits came back.</summary>
    public int Count => Hits.Count;

    /// <summary>True when nothing survived. A legitimate answer, and one the abstention gate reads.</summary>
    public bool IsEmpty => Hits.Count == 0;

    // B-14(a) — TopFusedScore was here, and §F.8's post-hoc abstention arm ("the gate also fires
    // if the best fused retrieval score is below the floor") is struck with it. It was never
    // read: its declaration was its only reference, which is the third state §8.1 refuses to
    // leave standing. It is deleted rather than wired because the quantity it exposed cannot
    // carry a floor. The fused score is Reciprocal Rank Fusion — see HybridRetriever.RrfK — so
    // Hits[0].Score is a sum of 1/(60 + rank) over the legs that returned the item, and nothing
    // else. It reports HOW MANY LEGS AGREED, not how good the top hit is.
    //
    // MEASURED over all 40 derived interest labels of the 14 personas (every query Demo 1's
    // offline arm actually issues): the statistic is BIMODAL. Twelve labels score exactly
    // 1/61 = 0.016393 — the dense leg alone — and the other twenty-eight land in
    // 0.028787 .. 0.032787, a 14% spread whose top is exactly 2/61. Quality does not track it:
    // Elena's "Heart-rate monitors", which returns only two candidates, scores the same
    // 0.016393 as Nadia's headline conjunction, which returns six good ones; and Sofia's
    // "Whole beans" and Renzo's "Hiking shoes" both sit at the 2/61 ceiling. A floor anywhere in
    // that range separates one-leg queries from two-leg queries and nothing else — it would have
    // printed as an abstention reason while measuring leg agreement. The real, calibratable
    // floor is the dense one, RetrievalDiagnostics.DenseScoreFloor.

    /// <summary>
    /// The retrieved product ids, in rank order. Recomputed on each access (deliberately — a
    /// cached field would be pulled into record equality); hoist it out of loops.
    /// </summary>
    public IReadOnlyList<string> ProductIds
    {
        get
        {
            var ids = new string[Hits.Count];
            for (int i = 0; i < Hits.Count; i++) ids[i] = Hits[i].ProductId;
            return ids;
        }
    }

    /// <summary>
    /// True when the candidate set contains this product. This is the containment check the
    /// grounding story rests on: the model may only present what retrieval returned.
    /// </summary>
    /// <param name="productId">Product id to look for.</param>
    public bool ContainsProduct(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId)) return false;
        for (int i = 0; i < Hits.Count; i++)
            if (string.Equals(Hits[i].ProductId, productId, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>An empty result carrying a diagnostics block — never a bare empty list with no explanation.</summary>
    /// <param name="diagnostics">Why nothing came back.</param>
    public static RetrievalResult Empty(RetrievalDiagnostics diagnostics) => new([], diagnostics);
}
