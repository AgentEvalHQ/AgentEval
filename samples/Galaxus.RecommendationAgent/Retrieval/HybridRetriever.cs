// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The three-stage hybrid retriever of design §D.3: a dense leg, a lexical leg, and Reciprocal
/// Rank Fusion over the two — with hard pre-filters, a top-k cut, and an honest degraded-mode flag.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dense alone is the wrong answer for Galaxus specifically.</b> Their customers type model
/// numbers, and dense retrieval is at its weakest exactly there. Lexical alone is the wrong answer
/// too: nothing lexical connects a power bank, a headlamp and a merino layer to "shoot at dawn on
/// day three". The claim the architecture makes is <i>fuse with their existing search, do not
/// replace it</i>.
/// </para>
/// <para>
/// <b>Why RRF.</b> One leg's scores are cosines in [-1, 1]; the other's are IDF-weighted token
/// counts on an unbounded scale. RRF reads only the RANK, so it needs no calibration between them —
/// which is precisely why it is the right choice here and why neither leg's raw score is allowed
/// to leak into the fused ordering.
/// </para>
/// <para>
/// <b>Parameters, honestly labelled (§D.3).</b> Retrieve-before-fusion 24 per leg: chosen, not
/// measured. RRF constant 60: the literature default. Final top-k 8, max 12: chosen. Dense score
/// floor <see cref="DefaultDenseScoreFloor"/>: <b>TO-CALIBRATE — do not present it as measured.</b>
/// The defensible answer to "why 0.28?" is the calibration METHOD, not the number.
/// </para>
/// </remarks>
public sealed class HybridRetriever : IProductRetriever
{
    /// <summary>The RRF constant. 60 is the literature default; it is not tuned here and is not claimed to be.</summary>
    public const int RrfK = 60;

    /// <summary>Candidates pulled from each leg before fusion. Chosen, not measured.</summary>
    public const int DefaultPerLegCandidates = 24;

    /// <summary>
    /// Dense cosine floor. <b>TO-CALIBRATE — do not present this as measured.</b> The method:
    /// build a gold set from the four personas (5–8 known-good product ids each, ~26 labelled
    /// pairs), sweep the floor, pick the value maximising recall@24 subject to precision@8, and
    /// report both plus the abstention rate it induces. A sweep LOCATES; only a held-out check
    /// resolves. A floor is also a property of an embedding SPACE, so a value calibrated for
    /// <c>text-embedding-3-small</c> does not transfer to <see cref="ConceptEmbeddingSource"/> —
    /// which is why <see cref="IEmbeddingSource.SuggestedDenseScoreFloor"/> exists at all.
    /// <para>
    /// ⚠ <b>That per-space seam exists but is not USED: both sources return 0.28, and B-21 made the
    /// number load-bearing on a path where it had barely bitten before.</b> Measured 2026-09-05 over
    /// the 53 query strings the fourteen personas' interest maps actually issue, dense candidates
    /// retrieved before the floor versus discarded by it:
    /// </para>
    /// <list type="bullet">
    ///   <item>concept space — 781 kept, <b>166 cut (17.5%)</b>; 10 queries reach the dense leg with
    ///         nothing to rank (a zero vector) and the run is reported DEGRADED for those.</item>
    ///   <item><c>--real-vectors</c> — 626 kept, <b>646 cut (50.8%)</b>; 0 queries degraded, but
    ///         <b>3</b> have every dense hit fall under the floor, so the dense leg contributes
    ///         nothing for them and NOTHING reports it — the retriever is not degraded, it simply
    ///         ranked nothing.</item>
    /// </list>
    /// <para>
    /// So one un-recalibrated constant discards half the dense candidates in one space and a sixth
    /// in the other. It is NOT re-tuned here — the calibration METHOD above is still the honest
    /// answer, and picking a second number so the two spaces cut alike would be fitting the floor to
    /// the output. It is written down because a threshold that quietly became the dominant filter on
    /// a newly working path must not be discovered later. Note also what this does to Eval 03's
    /// ARM D: "the query embedded to something non-zero" is not "the dense leg ranked something",
    /// and on the real path those two differ for 3 of the queries.
    /// </para>
    /// </summary>
    public const float DefaultDenseScoreFloor = 0.28f;

    /// <summary>Fusion identifier reported in the diagnostics block.</summary>
    public const string FusionName = "rrf-k60";

    /// <summary>
    /// The exact §D.4 console banner for a degraded run. Kept next to the flag that sets it, so the
    /// renderer and the retriever can never disagree about what "degraded" means.
    /// </summary>
    public const string DegradedBannerText =
        "  ⚠️  Degraded retrieval — no embedding credentials and this query is not in the\n" +
        "      precomputed cache. Running LEXICAL-ONLY. Cross-category matches will be missed.";

    private readonly IReadOnlyList<Product> _products;

    private HybridRetriever(
        IReadOnlyList<Product> products,
        ProductVectorIndex vectorIndex,
        LexicalIndex lexicalIndex,
        IEmbeddingSource? embeddings,
        HybridRetrieverOptions options,
        string? denseUnavailableReason)
    {
        _products              = products;
        VectorIndex            = vectorIndex;
        Lexical                = lexicalIndex;
        EmbeddingSource        = embeddings;
        Options                = options;
        DenseUnavailableReason = denseUnavailableReason;
    }

    /// <summary>The dense store.</summary>
    public ProductVectorIndex VectorIndex { get; }

    /// <summary>The lexical scorer. Always present — it needs no model and no key.</summary>
    public LexicalIndex Lexical { get; }

    /// <summary>The embedding source backing the dense leg, or null when there is none.</summary>
    public IEmbeddingSource? EmbeddingSource { get; }

    /// <summary>Tunables actually in force for this instance.</summary>
    public HybridRetrieverOptions Options { get; }

    /// <inheritdoc />
    public bool DenseAvailable => EmbeddingSource is not null && !VectorIndex.IsEmpty;

    /// <summary>Why the dense leg is off, when it is. Null on a healthy retriever.</summary>
    public string? DenseUnavailableReason { get; }

    /// <inheritdoc />
    public string Name => DenseAvailable
        ? $"hybrid({EmbeddingSource!.Name}+lexical)"
        : "lexical-only";

    /// <inheritdoc />
    public int ProductCount => _products.Count;

    /// <summary>The catalogue this retriever searches.</summary>
    public IReadOnlyList<Product> Products => _products;

    /// <summary>
    /// Builds both legs. The lexical index is built synchronously and always succeeds; the dense
    /// index is built by embedding every product document, and a source that cannot produce
    /// vectors yields a retriever that is permanently, visibly degraded rather than one that
    /// silently returns nothing.
    /// </summary>
    /// <param name="products">The catalogue.</param>
    /// <param name="embeddings">Embedding source. Null builds a lexical-only retriever.</param>
    /// <param name="options">Tunables. Null uses the §D.3 defaults.</param>
    /// <param name="onProgress">Optional index-build progress: (index, total, productId).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async ValueTask<HybridRetriever> BuildAsync(
        IReadOnlyList<Product> products,
        IEmbeddingSource? embeddings,
        HybridRetrieverOptions? options = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(products);

        var effective = options ?? new HybridRetrieverOptions();
        var lexical   = LexicalIndex.Build(products);

        if (embeddings is null)
        {
            const string reason = "No embedding source was configured.";
            return new HybridRetriever(
                products, ProductVectorIndex.Unavailable(products, reason), lexical, null, effective, reason);
        }

        var index = await ProductVectorIndex
            .BuildAsync(products, embeddings, onProgress, cancellationToken)
            .ConfigureAwait(false);

        var unavailable = index.IsEmpty
            ? index.UnavailableReason ?? $"Embedding source '{embeddings.Name}' produced no usable vectors."
            : null;

        return new HybridRetriever(products, index, lexical, embeddings, effective, unavailable);
    }

    /// <summary>
    /// A retriever with no dense leg at all, carrying the reason. Used when credentials are absent
    /// and the caller has deliberately chosen not to fall back to the offline concept source.
    /// </summary>
    /// <param name="products">The catalogue.</param>
    /// <param name="degradedReason">Why there is no dense leg.</param>
    /// <param name="options">Tunables.</param>
    public static HybridRetriever LexicalOnly(
        IReadOnlyList<Product> products,
        string degradedReason,
        HybridRetrieverOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentException.ThrowIfNullOrWhiteSpace(degradedReason);

        return new HybridRetriever(
            products,
            ProductVectorIndex.Unavailable(products, degradedReason),
            LexicalIndex.Build(products),
            embeddings: null,
            options ?? new HybridRetrieverOptions(),
            degradedReason);
    }

    /// <inheritdoc />
    public async ValueTask<RetrievalResult> SearchAsync(RetrievalQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var filter     = query.ToPredicate();
        var considered = VectorIndex.CountEligible(filter);
        var topK       = query.EffectiveTopK;
        var perLeg     = Math.Max(topK, Options.PerLegCandidates);
        var floor      = Options.DenseScoreFloor ?? EmbeddingSource?.SuggestedDenseScoreFloor ?? DefaultDenseScoreFloor;

        // ── Leg 2 (always) — lexical. It runs first so that a dense failure never costs the
        //    caller its results. ────────────────────────────────────────────────────────────────
        var lexicalHits = Lexical.Search(query.Need, perLeg, filter);

        // ── Leg 1 — dense. ───────────────────────────────────────────────────────────────────
        IReadOnlyList<(string ProductId, float Score)> denseHits = [];
        int denseBelowFloor = 0;
        bool denseRan = false;
        string? degradedReason = DenseUnavailableReason;

        if (DenseAvailable)
        {
            var queryVector = await ResolveQueryVectorAsync(query, cancellationToken).ConfigureAwait(false);

            if (queryVector.IsUnavailable())
            {
                degradedReason =
                    $"Embedding source '{EmbeddingSource!.Name}' could not embed this query " +
                    "(not in the precomputed cache and no live embedding path available).";
            }
            else if (EmbeddingVectors.IsAllZero(queryVector.Span))
            {
                // ⚠ "No embedder" and "no concept signal" are different states, and
                // ConceptEmbeddingSource's own remarks promise the retriever reports them
                // differently. It did not. MEASURED: "Sony a7 IV", "zzzz qqqq" AND Marco's and
                // Sofia's real interest label all produced dense = true, degraded = false,
                // denseCandidates = 0, fusion = "rrf-k60" — a run that says the dense leg ran and
                // found nothing, when the dense leg had nothing to run ON. A zero vector has zero
                // cosine against every product, so the leg contributes no ranking whatsoever and
                // the answer is lexical-only in fact. It now says so.
                degradedReason =
                    $"Embedding source '{EmbeddingSource!.Name}' recognised nothing in this query, so its vector is " +
                    "all zeros — no concept signal. Every cosine is 0, so the dense leg cannot rank anything and this " +
                    "answer is lexical-only. Cross-category matches will be missed.";
            }
            else
            {
                denseRan = true;
                degradedReason = null;

                var raw = VectorIndex.Search(queryVector.Span, perLeg, filter);
                var kept = new List<(string ProductId, float Score)>(raw.Count);

                foreach (var hit in raw)
                {
                    if (hit.Score < floor) { denseBelowFloor++; continue; }
                    kept.Add(hit);
                }

                denseHits = kept;
            }
        }

        var fused = Fuse(denseHits, lexicalHits, topK, Options.RrfK);
        var queryTokens = LexicalIndex.Tokenize(query.Need);
        var hits = new List<RetrievalHit>(fused.Count);

        foreach (var candidate in fused)
        {
            if (!VectorIndex.TryGetProduct(candidate.ProductId, out var product) || product is null) continue;

            hits.Add(new RetrievalHit(
                product.Id,
                product.Name,
                product.Brand,
                product.CategoryPath,
                candidate.Score,
                EmbeddingDocument.BestMatchingLine(product, queryTokens))
            {
                DenseRank    = candidate.DenseRank,
                LexicalRank  = candidate.LexicalRank,
                DenseScore   = candidate.DenseScore,
                LexicalScore = candidate.LexicalScore,
            });
        }

        var diagnostics = new RetrievalDiagnostics
        {
            Dense                   = denseRan,
            Lexical                 = true,
            Fusion                  = denseRan ? FusionName : "lexical-only",
            Degraded                = !denseRan,
            DegradedReason          = denseRan ? null : degradedReason ?? "The dense leg did not run.",
            DenseCandidates         = denseHits.Count,
            LexicalCandidates       = lexicalHits.Count,
            DenseBelowFloor         = denseBelowFloor,
            DenseScoreFloor         = floor,
            Considered              = considered,
            EmbeddingSource         = EmbeddingSource?.Name ?? "none",
            EmbeddingModel          = EmbeddingSource?.ModelId ?? "none",
            DocumentTemplateVersion = EmbeddingDocument.TemplateVersion,
        };

        return new RetrievalResult(hits, diagnostics);
    }

    /// <summary>
    /// Reciprocal Rank Fusion: <c>score = Σ 1/(k + rank)</c> over the legs a candidate appears in,
    /// with 1-based ranks.
    /// </summary>
    /// <remarks>
    /// Deliberately reads only the rank. Mixing a cosine and a token count by value would require
    /// a calibration nobody has measured, and would silently re-weight the two legs whenever the
    /// embedding model changed.
    /// </remarks>
    /// <param name="denseHits">Dense leg results, best first.</param>
    /// <param name="lexicalHits">Lexical leg results, best first.</param>
    /// <param name="topK">Final cut.</param>
    /// <param name="rrfK">The RRF constant.</param>
    public static IReadOnlyList<FusedCandidate> Fuse(
        IReadOnlyList<(string ProductId, float Score)> denseHits,
        IReadOnlyList<(string ProductId, float Score)> lexicalHits,
        int topK,
        int rrfK = RrfK)
    {
        ArgumentNullException.ThrowIfNull(denseHits);
        ArgumentNullException.ThrowIfNull(lexicalHits);
        if (topK <= 0) return [];

        var byProduct = new Dictionary<string, Accumulator>(StringComparer.Ordinal);

        for (int i = 0; i < denseHits.Count; i++)
        {
            var (productId, score) = denseHits[i];
            var accumulator = Get(byProduct, productId);
            accumulator.DenseRank  = i + 1;
            accumulator.DenseScore = score;
            accumulator.Fused     += 1.0 / (rrfK + i + 1);
        }

        for (int i = 0; i < lexicalHits.Count; i++)
        {
            var (productId, score) = lexicalHits[i];
            var accumulator = Get(byProduct, productId);
            accumulator.LexicalRank  = i + 1;
            accumulator.LexicalScore = score;
            accumulator.Fused       += 1.0 / (rrfK + i + 1);
        }

        var fused = new List<FusedCandidate>(byProduct.Count);
        foreach (var (productId, accumulator) in byProduct)
        {
            fused.Add(new FusedCandidate(
                productId,
                accumulator.Fused,
                accumulator.DenseRank,
                accumulator.LexicalRank,
                accumulator.DenseScore,
                accumulator.LexicalScore));
        }

        fused.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.ProductId, right.ProductId);
        });

        if (fused.Count > topK) fused.RemoveRange(topK, fused.Count - topK);
        return fused;

        static Accumulator Get(Dictionary<string, Accumulator> map, string productId)
        {
            if (!map.TryGetValue(productId, out var accumulator))
            {
                accumulator = new Accumulator();
                map[productId] = accumulator;
            }
            return accumulator;
        }
    }

    /// <summary>
    /// The dense leg's query vector: the anchor product's stored vector when
    /// <see cref="RetrievalQuery.AnchorProductId"/> resolves, otherwise the embedding of
    /// <see cref="RetrievalQuery.Need"/>.
    /// </summary>
    private async ValueTask<ReadOnlyMemory<float>> ResolveQueryVectorAsync(
        RetrievalQuery query,
        CancellationToken cancellationToken)
    {
        if (query.AnchorProductId is { Length: > 0 } anchorId &&
            VectorIndex.TryGetVector(anchorId, out var anchorVector))
        {
            return anchorVector;
        }

        if (EmbeddingSource is null) return EmbeddingVectors.Unavailable;

        return await EmbeddingSource.EmbedAsync(query.Need, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One fused candidate: the RRF score plus the per-leg provenance that produced it.</summary>
    /// <param name="ProductId">Catalogue id.</param>
    /// <param name="Score">Fused RRF score. Comparable within one query only.</param>
    /// <param name="DenseRank">1-based dense rank, or null.</param>
    /// <param name="LexicalRank">1-based lexical rank, or null.</param>
    /// <param name="DenseScore">Raw cosine, diagnostic only.</param>
    /// <param name="LexicalScore">Raw lexical score, diagnostic only.</param>
    public readonly record struct FusedCandidate(
        string ProductId,
        double Score,
        int? DenseRank,
        int? LexicalRank,
        double DenseScore,
        double LexicalScore);

    private sealed class Accumulator
    {
        public double Fused;
        public int? DenseRank;
        public int? LexicalRank;
        public double DenseScore;
        public double LexicalScore;
    }
}

/// <summary>Tunables for <see cref="HybridRetriever"/>. Every default is the §D.3 table's value.</summary>
public sealed record HybridRetrieverOptions
{
    /// <summary>Candidates pulled from each leg before fusion. Chosen, not measured.</summary>
    public int PerLegCandidates { get; init; } = HybridRetriever.DefaultPerLegCandidates;

    /// <summary>The RRF constant. The literature default; changing it re-weights nothing between legs, only the tail.</summary>
    public int RrfK { get; init; } = HybridRetriever.RrfK;

    /// <summary>
    /// Dense cosine floor. Null defers to <see cref="IEmbeddingSource.SuggestedDenseScoreFloor"/>,
    /// which is the right default because the floor belongs to the embedding space, not the retriever.
    /// <b>Every value in play is UNMEASURED.</b>
    /// </summary>
    public float? DenseScoreFloor { get; init; }
}
