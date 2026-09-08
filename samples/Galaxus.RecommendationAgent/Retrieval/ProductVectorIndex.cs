// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using Galaxus.RecommendationAgent.Domain;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The dense leg's store (design §D.2): the smallest thing that works — a brute-force cosine scan
/// over one vector per product, with the pre-filter applied BEFORE the top-k cut.
/// </summary>
/// <remarks>
/// <para>
/// <b>Brute force is the right answer at this size.</b> 72 products × 1536 floats is 442 KB and a
/// full scan is sub-millisecond; an ANN index would add a dependency, a build step and a recall
/// approximation in exchange for nothing measurable. <see cref="IProductRetriever"/> is the seam
/// where a real vector store goes when the catalogue stops fitting in a cache line.
/// </para>
/// <para>
/// <b>Vectors are unit-normalised at load</b>, so cosine similarity is a plain dot product and
/// <see cref="System.Numerics.Tensors.TensorPrimitives"/> can do it vectorised.
/// <see cref="Search"/> normalises the query too, so the invariant holds on both sides rather
/// than being assumed of the caller.
/// </para>
/// <para>
/// <b>Filters are a PRE-filter.</b> Post-filtering after top-k silently returns fewer than k
/// results and quietly degrades recall on exactly the constrained queries this demo exists to
/// showcase — and it fails in the flattering direction, because a short confident list still
/// looks like an answer.
/// </para>
/// </remarks>
public sealed class ProductVectorIndex
{
    private readonly IReadOnlyList<Product> _products;
    private readonly Dictionary<string, int> _slotByProductId;
    private readonly float[]?[] _vectors;

    private ProductVectorIndex(
        IReadOnlyList<Product> products,
        Dictionary<string, int> slotByProductId,
        float[]?[] vectors,
        int dimensions,
        IReadOnlyList<string> unembeddableProductIds,
        string embeddingSourceName,
        string embeddingModelId,
        string? unavailableReason)
    {
        _products               = products;
        _slotByProductId        = slotByProductId;
        _vectors                = vectors;
        Dimensions              = dimensions;
        UnembeddableProductIds  = unembeddableProductIds;
        EmbeddingSourceName     = embeddingSourceName;
        EmbeddingModelId        = embeddingModelId;
        UnavailableReason       = unavailableReason;
        Count                   = CountNonNull(vectors);
    }

    /// <summary>How many products have a usable vector.</summary>
    public int Count { get; }

    /// <summary>Vector length. Every vector in the index agrees on it, by construction.</summary>
    public int Dimensions { get; }

    /// <summary>True when nothing is searchable — the dense leg must then be reported as unavailable.</summary>
    public bool IsEmpty => Count == 0;

    /// <summary>Why the index is empty, when it is. Null on a healthy index.</summary>
    public string? UnavailableReason { get; }

    /// <summary>
    /// Products the embedding source could not embed. Non-empty is a real signal, not a rounding
    /// error: those products are invisible to the dense leg and can only be found lexically.
    /// </summary>
    public IReadOnlyList<string> UnembeddableProductIds { get; }

    /// <summary>Which source produced the vectors — reported in the diagnostics block.</summary>
    public string EmbeddingSourceName { get; }

    /// <summary>Which embedding model the vectors belong to. Two models means two spaces; never mix them.</summary>
    public string EmbeddingModelId { get; }

    /// <summary>The catalogue this index was built over, in catalogue order.</summary>
    public IReadOnlyList<Product> Products => _products;

    /// <summary>
    /// Embeds every product's <see cref="EmbeddingDocument"/> and builds the index.
    /// </summary>
    /// <remarks>
    /// A product whose vector comes back unavailable or all-zero is recorded in
    /// <see cref="UnembeddableProductIds"/> and skipped — never stored as zeros, because a zero
    /// vector scores 0 against everything and would look like a legitimate "no match" rather than
    /// a missing index entry.
    /// </remarks>
    /// <param name="products">The catalogue.</param>
    /// <param name="source">Embedding source.</param>
    /// <param name="onProgress">Optional progress callback: (index, total, productId).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="InvalidOperationException">A vector arrives with a length other than the source's declared dimensions.</exception>
    public static async ValueTask<ProductVectorIndex> BuildAsync(
        IReadOnlyList<Product> products,
        IEmbeddingSource source,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(source);

        var slots                 = new Dictionary<string, int>(products.Count, StringComparer.Ordinal);
        float[]?[] vectors        = new float[products.Count][];
        var unembeddable          = new List<string>();
        int dimensions            = source.Dimensions;

        for (int i = 0; i < products.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var product = products[i];
            if (product is null) continue;

            slots[product.Id] = i;
            onProgress?.Invoke(i, products.Count, product.Id);

            var document = EmbeddingDocument.ForProduct(product);
            var vector   = await source.EmbedAsync(document, cancellationToken).ConfigureAwait(false);

            if (vector.IsUnavailable() || EmbeddingVectors.IsAllZero(vector.Span))
            {
                unembeddable.Add(product.Id);
                continue;
            }

            if (dimensions <= 0) dimensions = vector.Length;

            if (vector.Length != dimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding source '{source.Name}' returned a {vector.Length}-dimensional vector for " +
                    $"product '{product.Id}' but {dimensions} was expected. A mixed-dimension index cannot " +
                    "be searched — rebuild the embedding assets against one model.");
            }

            vectors[i] = EmbeddingVectors.Normalized(vector.Span);
        }

        string? unavailable = CountNonNull(vectors) == 0
            ? $"Embedding source '{source.Name}' produced no usable vectors for {products.Count} products."
            : null;

        return new ProductVectorIndex(
            products, slots, vectors, dimensions, unembeddable,
            source.Name, source.ModelId, unavailable);
    }

    /// <summary>
    /// An index with nothing in it, carrying the reason. Used when there is no embedding source at
    /// all — the retriever then runs lexical-only and says so, rather than pretending a dense leg ran.
    /// </summary>
    /// <param name="products">The catalogue (still needed for the pre-filter).</param>
    /// <param name="reason">Why there are no vectors.</param>
    public static ProductVectorIndex Unavailable(IReadOnlyList<Product> products, string reason)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var slots = new Dictionary<string, int>(products.Count, StringComparer.Ordinal);
        for (int i = 0; i < products.Count; i++)
        {
            if (products[i] is { } product) slots[product.Id] = i;
        }

        float[]?[] empty = new float[products.Count][];
        return new ProductVectorIndex(products, slots, empty, 0, [], "none", "none", reason);
    }

    /// <summary>
    /// Brute-force cosine scan. Returns the best <paramref name="topK"/> products that pass
    /// <paramref name="filter"/>, best first.
    /// </summary>
    /// <param name="query">Query vector. Normalised internally, so the caller need not.</param>
    /// <param name="topK">Maximum results.</param>
    /// <param name="filter">The PRE-filter — applied before the cut, never after.</param>
    /// <returns>Product ids with their cosine scores in [-1, 1]. Empty when the query carries no signal.</returns>
    /// <exception cref="ArgumentException">The query length disagrees with <see cref="Dimensions"/>.</exception>
    public IReadOnlyList<(string ProductId, float Score)> Search(ReadOnlySpan<float> query, int topK, Func<Product, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (topK <= 0 || IsEmpty || query.Length == 0) return [];

        if (query.Length != Dimensions)
        {
            throw new ArgumentException(
                $"Query vector has {query.Length} dimensions but the index holds {Dimensions}. " +
                "This is a wiring fault, not a miss — an index built by one embedding model cannot " +
                "be queried by another.",
                nameof(query));
        }

        // Normalise a copy so "cosine == dot" holds for the query as well as the stored vectors.
        // A zero-norm query carries no signal at all and must return nothing rather than NaNs.
        var normalizedQuery = query.ToArray();
        if (!EmbeddingVectors.NormalizeInPlace(normalizedQuery)) return [];

        var results = new List<(string ProductId, float Score)>();

        for (int i = 0; i < _products.Count; i++)
        {
            var product = _products[i];
            if (product is null) continue;

            var vector = _vectors[i];
            if (vector is null) continue;
            if (!filter(product)) continue;

            var score = EmbeddingVectors.DotOfUnitVectors(normalizedQuery, vector);
            if (score > 0f) results.Add((product.Id, score));
        }

        results.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.ProductId, right.ProductId);
        });

        if (results.Count > topK) results.RemoveRange(topK, results.Count - topK);
        return results;
    }

    /// <summary>How many products pass a filter — the "considered" figure in the diagnostics block.</summary>
    /// <param name="filter">The pre-filter.</param>
    public int CountEligible(Func<Product, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        int eligible = 0;
        foreach (var product in _products)
        {
            if (product is not null && filter(product)) eligible++;
        }
        return eligible;
    }

    /// <summary>Fetches a stored (unit-length) vector. This is how a similarity query anchors on a product.</summary>
    /// <param name="productId">Product id.</param>
    /// <param name="vector">The stored vector, or empty.</param>
    public bool TryGetVector(string? productId, out ReadOnlyMemory<float> vector)
    {
        vector = ReadOnlyMemory<float>.Empty;
        if (string.IsNullOrWhiteSpace(productId)) return false;
        if (!_slotByProductId.TryGetValue(productId, out var slot)) return false;

        var stored = _vectors[slot];
        if (stored is null) return false;

        vector = stored;
        return true;
    }

    /// <summary>Resolves a product by id within this index.</summary>
    /// <param name="productId">Product id.</param>
    /// <param name="product">The product, or null.</param>
    public bool TryGetProduct(string? productId, out Product? product)
    {
        product = null;
        if (string.IsNullOrWhiteSpace(productId)) return false;
        if (!_slotByProductId.TryGetValue(productId, out var slot)) return false;

        product = _products[slot];
        return product is not null;
    }

    private static int CountNonNull(float[]?[] vectors)
    {
        int count = 0;
        foreach (var vector in vectors)
        {
            if (vector is not null) count++;
        }
        return count;
    }
}
