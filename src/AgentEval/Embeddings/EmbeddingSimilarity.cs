// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Numerics.Tensors;

namespace AgentEval.Embeddings;

/// <summary>
/// Utility class for computing similarity between embeddings.
/// Uses TensorPrimitives for efficient vector operations.
/// </summary>
public static class EmbeddingSimilarity
{
    /// <summary>
    /// Compute cosine similarity between two embedding vectors.
    /// </summary>
    /// <param name="a">First embedding vector.</param>
    /// <param name="b">Second embedding vector.</param>
    /// <returns>Cosine similarity in range [-1, 1], typically [0, 1] for text embeddings.</returns>
    /// <exception cref="ArgumentException">Thrown if vectors have different dimensions.</exception>
    public static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Embedding dimensions must match. Got {a.Length} and {b.Length}.");
        }
        
        return TensorPrimitives.CosineSimilarity(a, b);
    }
    
    /// <summary>
    /// Compute cosine similarity between two embedding vectors.
    /// </summary>
    /// <param name="a">First embedding vector.</param>
    /// <param name="b">Second embedding vector.</param>
    /// <returns>Cosine similarity in range [-1, 1], typically [0, 1] for text embeddings.</returns>
    public static float CosineSimilarity(ReadOnlyMemory<float> a, ReadOnlyMemory<float> b)
        => CosineSimilarity(a.Span, b.Span);
    
    /// <summary>
    /// Compute euclidean distance between two embedding vectors.
    /// </summary>
    /// <param name="a">First embedding vector.</param>
    /// <param name="b">Second embedding vector.</param>
    /// <returns>Euclidean distance (0 = identical).</returns>
    public static float EuclideanDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Embedding dimensions must match. Got {a.Length} and {b.Length}.");
        }
        
        return TensorPrimitives.Distance(a, b);
    }
    
    /// <summary>
    /// Compute dot product between two embedding vectors.
    /// </summary>
    /// <param name="a">First embedding vector.</param>
    /// <param name="b">Second embedding vector.</param>
    /// <returns>Dot product value.</returns>
    public static float DotProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Embedding dimensions must match. Got {a.Length} and {b.Length}.");
        }
        
        return TensorPrimitives.Dot(a, b);
    }
    
    /// <summary>
    /// Compute pairwise similarities between a query and multiple candidates.
    /// Useful for finding most similar documents.
    /// </summary>
    /// <param name="query">Query embedding.</param>
    /// <param name="candidates">Candidate embeddings to compare against.</param>
    /// <returns>Array of similarity scores in the same order as candidates.</returns>
    public static float[] ComputeSimilarities(
        ReadOnlyMemory<float> query, 
        IReadOnlyList<ReadOnlyMemory<float>> candidates)
    {
        var results = new float[candidates.Count];
        var querySpan = query.Span;
        
        for (int i = 0; i < candidates.Count; i++)
        {
            results[i] = CosineSimilarity(querySpan, candidates[i].Span);
        }
        
        return results;
    }
    
    /// <summary>
    /// Find the top-k most similar candidates to a query.
    /// </summary>
    /// <param name="query">Query embedding.</param>
    /// <param name="candidates">Candidate embeddings with associated data.</param>
    /// <param name="topK">Number of top results to return.</param>
    /// <returns>Top-k candidates ordered by similarity (descending).</returns>
    public static IEnumerable<(T Item, float Similarity)> TopK<T>(
        ReadOnlyMemory<float> query,
        IEnumerable<(T Item, ReadOnlyMemory<float> Embedding)> candidates,
        int topK)
    {
        return candidates
            .Select(c => (c.Item, Similarity: CosineSimilarity(query, c.Embedding)))
            .OrderByDescending(x => x.Similarity)
            .Take(topK);
    }
}
