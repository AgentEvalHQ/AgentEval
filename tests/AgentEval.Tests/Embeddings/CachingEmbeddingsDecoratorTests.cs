// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Embeddings;
using Xunit;

namespace AgentEval.Tests.Embeddings;

/// <summary>
/// Tests for <see cref="CachingEmbeddingsDecorator"/>.
/// </summary>
public class CachingEmbeddingsDecoratorTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullInner_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new CachingEmbeddingsDecorator(null!));
    }

    [Fact]
    public void Constructor_ZeroMaxCacheSize_ThrowsArgumentOutOfRangeException()
    {
        var inner = new FakeEmbeddingsProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CachingEmbeddingsDecorator(inner, maxCacheSize: 0));
    }

    [Fact]
    public void Constructor_NegativeMaxCacheSize_ThrowsArgumentOutOfRangeException()
    {
        var inner = new FakeEmbeddingsProvider();
        Assert.Throws<ArgumentOutOfRangeException>(() => new CachingEmbeddingsDecorator(inner, maxCacheSize: -5));
    }

    [Fact]
    public void Constructor_ValidArgs_CacheCountZero()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        Assert.Equal(0, decorator.CacheCount);
    }

    #endregion

    #region GetEmbeddingAsync Tests

    [Fact]
    public async Task GetEmbeddingAsync_FirstCall_DelegatesToInner()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        var result = await decorator.GetEmbeddingAsync("hello");

        Assert.Equal(1, inner.SingleCallCount);
        Assert.Equal(3, result.Length); // FakeEmbeddingsProvider returns 3-element vectors
    }

    [Fact]
    public async Task GetEmbeddingAsync_SameTextTwice_InnerCalledOnlyOnce()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        var result1 = await decorator.GetEmbeddingAsync("hello");
        var result2 = await decorator.GetEmbeddingAsync("hello");

        Assert.Equal(1, inner.SingleCallCount);
        Assert.Equal(result1.ToArray(), result2.ToArray());
    }

    [Fact]
    public async Task GetEmbeddingAsync_DifferentTexts_InnerCalledForEach()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        await decorator.GetEmbeddingAsync("hello");
        await decorator.GetEmbeddingAsync("world");

        Assert.Equal(2, inner.SingleCallCount);
        Assert.Equal(2, decorator.CacheCount);
    }

    [Fact]
    public async Task GetEmbeddingAsync_CacheFull_SkipsNewEntries()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner, maxCacheSize: 2);

        await decorator.GetEmbeddingAsync("a"); // cached
        await decorator.GetEmbeddingAsync("b"); // cached
        await decorator.GetEmbeddingAsync("c"); // NOT cached (full)

        Assert.Equal(3, inner.SingleCallCount);
        Assert.Equal(2, decorator.CacheCount);

        // Calling "c" again should hit inner again (was not cached)
        await decorator.GetEmbeddingAsync("c");
        Assert.Equal(4, inner.SingleCallCount);
    }

    #endregion

    #region GetEmbeddingsAsync Tests (Batch)

    [Fact]
    public async Task GetEmbeddingsAsync_AllMisses_DelegatesToInner()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        var results = await decorator.GetEmbeddingsAsync(["hello", "world"]);

        Assert.Equal(2, results.Count);
        Assert.Equal(1, inner.BatchCallCount);
        Assert.Equal(2, inner.BatchTextsCount);
        Assert.Equal(2, decorator.CacheCount);
    }

    [Fact]
    public async Task GetEmbeddingsAsync_AllHits_DoesNotCallInner()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        // Warm cache
        await decorator.GetEmbeddingAsync("hello");
        await decorator.GetEmbeddingAsync("world");

        // Batch call with all cached
        var results = await decorator.GetEmbeddingsAsync(["hello", "world"]);

        Assert.Equal(2, results.Count);
        Assert.Equal(0, inner.BatchCallCount); // No batch call needed
    }

    [Fact]
    public async Task GetEmbeddingsAsync_PartialHits_OnlyFetchesMisses()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        // Warm cache for "hello"
        await decorator.GetEmbeddingAsync("hello");
        Assert.Equal(1, inner.SingleCallCount);

        // Batch call: "hello" (cached) + "world" (miss) + "foo" (miss)
        var results = await decorator.GetEmbeddingsAsync(["hello", "world", "foo"]);

        Assert.Equal(3, results.Count);
        Assert.Equal(1, inner.BatchCallCount);
        Assert.Equal(2, inner.BatchTextsCount); // Only "world" and "foo" sent to inner
    }

    [Fact]
    public async Task GetEmbeddingsAsync_PreservesOrder()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        // Warm cache for "b"
        await decorator.GetEmbeddingAsync("b");

        // Request in order: a, b, c
        var results = await decorator.GetEmbeddingsAsync(["a", "b", "c"]);

        Assert.Equal(3, results.Count);
        // FakeEmbeddingsProvider uses hash-based vectors, so each text produces a unique embedding
        Assert.NotEqual(results[0].ToArray(), results[1].ToArray());
        Assert.NotEqual(results[1].ToArray(), results[2].ToArray());
    }

    #endregion

    #region ClearCache Tests

    [Fact]
    public async Task ClearCache_ResetsCache()
    {
        var inner = new FakeEmbeddingsProvider();
        var decorator = new CachingEmbeddingsDecorator(inner);

        await decorator.GetEmbeddingAsync("hello");
        Assert.Equal(1, decorator.CacheCount);

        decorator.ClearCache();
        Assert.Equal(0, decorator.CacheCount);

        // After clearing, should call inner again
        await decorator.GetEmbeddingAsync("hello");
        Assert.Equal(2, inner.SingleCallCount);
    }

    #endregion
}

#region Test Helpers

/// <summary>
/// Fake embeddings provider for testing — generates deterministic vectors from text hash.
/// </summary>
internal class FakeEmbeddingsProvider : IAgentEvalEmbeddings
{
    public int SingleCallCount { get; private set; }
    public int BatchCallCount { get; private set; }
    public int BatchTextsCount { get; private set; }

    public Task<ReadOnlyMemory<float>> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        SingleCallCount++;
        return Task.FromResult(GenerateVector(text));
    }

    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        BatchCallCount++;
        var textList = texts.ToList();
        BatchTextsCount += textList.Count;
        
        IReadOnlyList<ReadOnlyMemory<float>> results = textList.Select(GenerateVector).ToList();
        return Task.FromResult(results);
    }

    private static ReadOnlyMemory<float> GenerateVector(string text)
    {
        // Deterministic vector from hash code — unique per distinct text
        var hash = text.GetHashCode();
        return new ReadOnlyMemory<float>([hash * 0.001f, hash * 0.002f, hash * 0.003f]);
    }
}

#endregion
