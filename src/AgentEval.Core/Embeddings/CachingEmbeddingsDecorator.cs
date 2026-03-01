// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;

namespace AgentEval.Embeddings;

/// <summary>
/// Decorator that caches embedding results to avoid redundant API calls for repeated texts.
/// </summary>
/// <remarks>
/// <para>
/// Safe for stochastic evaluation: during repeated test runs the fixed reference texts
/// (ground truth, context, input) are the same and produce identical embeddings.
/// Only the agent's actual output changes — and that always generates a fresh embedding.
/// </para>
/// <para>
/// Thread-safe: uses <see cref="ConcurrentDictionary{TKey,TValue}"/> for concurrent access.
/// The <c>maxCacheSize</c> limit is approximate — under high concurrency the cache may
/// briefly exceed the limit by the number of concurrent writers before stabilizing.
/// New entries are silently skipped once the limit is reached
/// (cache serves as a best-effort optimization, not a strict bound).
/// </para>
/// </remarks>
public class CachingEmbeddingsDecorator : IAgentEvalEmbeddings
{
    private readonly IAgentEvalEmbeddings _inner;
    private readonly int _maxCacheSize;
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<float>> _cache = new();

    /// <summary>
    /// Creates a new caching decorator around an existing embeddings provider.
    /// </summary>
    /// <param name="inner">The underlying embeddings provider to cache results from.</param>
    /// <param name="maxCacheSize">Maximum number of cached entries. Default is 1000.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="inner"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxCacheSize"/> is less than 1.</exception>
    public CachingEmbeddingsDecorator(IAgentEvalEmbeddings inner, int maxCacheSize = 1000)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        if (maxCacheSize < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCacheSize), maxCacheSize, "Max cache size must be at least 1.");
        _maxCacheSize = maxCacheSize;
    }

    /// <summary>
    /// Gets the current number of cached entries.
    /// </summary>
    public int CacheCount => _cache.Count;

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> GetEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(text, out var cached))
            return cached;

        var embedding = await _inner.GetEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
        TryAddToCache(text, embedding);
        return embedding;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();

        // Identify cache hits and misses
        var results = new ReadOnlyMemory<float>[textList.Count];
        var missIndices = new List<int>();
        var missTexts = new List<string>();

        for (int i = 0; i < textList.Count; i++)
        {
            if (_cache.TryGetValue(textList[i], out var cached))
            {
                results[i] = cached;
            }
            else
            {
                missIndices.Add(i);
                missTexts.Add(textList[i]);
            }
        }

        // Fetch only cache misses from the inner provider
        if (missTexts.Count > 0)
        {
            var freshEmbeddings = await _inner.GetEmbeddingsAsync(missTexts, cancellationToken).ConfigureAwait(false);

            for (int j = 0; j < missIndices.Count; j++)
            {
                results[missIndices[j]] = freshEmbeddings[j];
                TryAddToCache(missTexts[j], freshEmbeddings[j]);
            }
        }

        return results;
    }

    /// <summary>
    /// Clears all cached embeddings.
    /// </summary>
    public void ClearCache() => _cache.Clear();

    private void TryAddToCache(string text, ReadOnlyMemory<float> embedding)
    {
        // Best-effort approximate bound: Count check is not atomic with TryAdd, so under
        // high concurrency the cache may briefly exceed _maxCacheSize by a small margin.
        // This is acceptable for eval workloads (no eviction policy needed).
        if (_cache.Count < _maxCacheSize)
        {
            _cache.TryAdd(text, embedding);
        }
    }
}
