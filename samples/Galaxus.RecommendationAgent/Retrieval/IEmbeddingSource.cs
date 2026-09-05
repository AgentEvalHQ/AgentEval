// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Numerics.Tensors;

namespace Galaxus.RecommendationAgent.Retrieval;

/// <summary>
/// The embedding seam (design §D.4). One method: turn text into a vector.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unavailability is a return value, not an exception.</b> A source that cannot embed a
/// particular text returns an EMPTY memory (<c>Length == 0</c>). That is the signal
/// <see cref="HybridRetriever"/> reads to enter degraded mode: it disables the dense leg,
/// runs lexical-only, and says so. Exceptions are reserved for real faults (a bad key, a
/// dimension mismatch, a stale asset) — control flow through exceptions would make a
/// degraded run indistinguishable from a broken one.
/// </para>
/// <para>
/// <b>An all-zero vector of the right length is NOT unavailability.</b> It means "I recognised
/// nothing in this text", which <see cref="ConceptEmbeddingSource"/> can legitimately return.
/// The dense leg then contributes no ranks for that query while remaining available. The two
/// cases are different and are reported differently.
/// </para>
/// </remarks>
public interface IEmbeddingSource
{
    /// <summary>Short name for the console and diagnostics: <c>"concept"</c>, <c>"azure"</c>, <c>"precomputed"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// The model identifier stamped into generated assets and validated at load, e.g.
    /// <c>"text-embedding-3-small"</c>. Two sources with different model ids produce vectors in
    /// DIFFERENT spaces and must never be mixed into one index.
    /// </summary>
    string ModelId { get; }

    /// <summary>Vector length this source produces. Every vector in one index must agree on it.</summary>
    int Dimensions { get; }

    /// <summary>True when the source needs no network and no key — hermetic and deterministic.</summary>
    bool IsOffline { get; }

    /// <summary>
    /// The dense cosine floor this source suggests for <see cref="HybridRetriever"/>.
    /// <b>DERIVED PER SPACE 2026-09-05</b> — see <see cref="CalibratedThresholds"/>. It lives on the
    /// source rather than on the retriever because a floor is a property of an embedding SPACE: a
    /// threshold picked for <c>text-embedding-3-small</c> cosines says nothing about concept-vector
    /// cosines. Calibrating one and reusing it for the other would be a number with no provenance —
    /// which is exactly what every implementation of this property did until the derivation ran: all
    /// three returned 0.28.
    /// </summary>
    float SuggestedDenseScoreFloor { get; }

    /// <summary>
    /// Embeds one text.
    /// </summary>
    /// <param name="text">Text to embed — an <see cref="EmbeddingDocument"/> or a query.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>
    /// A vector of length <see cref="Dimensions"/>, or an EMPTY memory when this source cannot
    /// embed this text (see the remarks on <see cref="IEmbeddingSource"/>).
    /// </returns>
    ValueTask<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// Vector helpers shared by every source, the index and the cache builder — so normalisation
/// happens in exactly one place and cosine can safely be a dot product everywhere.
/// </summary>
public static class EmbeddingVectors
{
    /// <summary>The "this source cannot embed this text" sentinel: an empty memory.</summary>
    public static ReadOnlyMemory<float> Unavailable => ReadOnlyMemory<float>.Empty;

    /// <summary>True when a vector is the unavailability sentinel.</summary>
    /// <param name="vector">Vector to test.</param>
    public static bool IsUnavailable(this ReadOnlyMemory<float> vector) => vector.Length == 0;

    /// <summary>Euclidean (L2) norm.</summary>
    /// <param name="vector">Vector.</param>
    public static float Norm(ReadOnlySpan<float> vector) =>
        vector.Length == 0 ? 0f : TensorPrimitives.Norm(vector);

    /// <summary>
    /// Scales a vector to unit length in place. A zero vector is left as zeros — dividing by
    /// its norm would produce NaNs that then poison every cosine it touches.
    /// </summary>
    /// <param name="vector">Vector, modified in place.</param>
    /// <returns>True when the vector was non-zero and is now unit length.</returns>
    public static bool NormalizeInPlace(Span<float> vector)
    {
        var norm = Norm(vector);
        if (norm <= float.Epsilon || !float.IsFinite(norm)) return false;

        TensorPrimitives.Divide(vector, norm, vector);
        return true;
    }

    /// <summary>Returns a unit-length copy. A zero vector copies through as zeros.</summary>
    /// <param name="vector">Source vector.</param>
    public static float[] Normalized(ReadOnlySpan<float> vector)
    {
        var copy = vector.ToArray();
        NormalizeInPlace(copy);
        return copy;
    }

    /// <summary>
    /// Cosine similarity, computed as a plain dot product. Valid ONLY when both operands are
    /// already unit length — which is the invariant <see cref="ProductVectorIndex"/> maintains
    /// at load and <see cref="ProductVectorIndex.Search"/> maintains for the query.
    /// </summary>
    /// <param name="unitLeft">Unit-length vector.</param>
    /// <param name="unitRight">Unit-length vector of the same length.</param>
    public static float DotOfUnitVectors(ReadOnlySpan<float> unitLeft, ReadOnlySpan<float> unitRight)
    {
        if (unitLeft.Length != unitRight.Length || unitLeft.Length == 0) return 0f;

        var dot = TensorPrimitives.Dot(unitLeft, unitRight);
        return float.IsFinite(dot) ? dot : 0f;
    }

    /// <summary>True when every component is zero — "no signal", as distinct from "unavailable".</summary>
    /// <param name="vector">Vector to test.</param>
    public static bool IsAllZero(ReadOnlySpan<float> vector)
    {
        for (int i = 0; i < vector.Length; i++)
        {
            if (vector[i] != 0f) return false;
        }
        return true;
    }

    /// <summary>
    /// Embeds a batch sequentially, preserving order and pairing each input with its vector.
    /// Sequential on purpose: this runs against a rate-limited deployment during
    /// <c>--rebuild-embeddings</c>, and a burst of 72 parallel calls is the fastest way to be
    /// throttled into a partial asset.
    /// </summary>
    /// <param name="source">The embedding source.</param>
    /// <param name="texts">Texts to embed, in order.</param>
    /// <param name="onProgress">Optional per-item callback: (index, total, text).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAllAsync(
        this IEmbeddingSource source,
        IReadOnlyList<string> texts,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(texts);

        var vectors = new ReadOnlyMemory<float>[texts.Count];
        for (int i = 0; i < texts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onProgress?.Invoke(i, texts.Count, texts[i]);
            vectors[i] = await source.EmbedAsync(texts[i], cancellationToken).ConfigureAwait(false);
        }

        return vectors;
    }
}
