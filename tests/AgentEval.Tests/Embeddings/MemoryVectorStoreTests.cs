// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Embeddings;
using Xunit;

namespace AgentEval.Tests.Embeddings;

/// <summary>
/// Tests for <see cref="MemoryVectorStore"/>. Pins the PERF-08 hardening: <see cref="MemoryVectorStore.Search"/>
/// scores/sorts on a snapshot taken under a brief lock, and drops NaN scores so a poisoned embedding cannot
/// make the ranking non-deterministic.
/// </summary>
public class MemoryVectorStoreTests
{
    [Fact]
    public void Search_ReturnsTopKOrderedByDescendingScore()
    {
        var store = new MemoryVectorStore();
        store.Add("a", "exact", new float[] { 1f, 0f });
        store.Add("b", "orthogonal", new float[] { 0f, 1f });
        store.Add("c", "opposite", new float[] { -1f, 0f });

        // minScore: -1 so the opposite vector (cosine -1.0) is not filtered and we exercise full ordering.
        var results = store.Search(new float[] { 1f, 0f }, topK: 3, minScore: -1f);

        Assert.Equal(3, results.Count);
        Assert.Equal("a", results[0].Id); // cosine 1.0
        Assert.Equal("c", results[2].Id); // cosine -1.0 ranked last
        // scores are monotonically non-increasing
        for (int i = 1; i < results.Count; i++)
            Assert.True(results[i - 1].Score >= results[i].Score);
    }

    [Fact]
    public void Search_DropsNaNScoredDocuments_PERF08()
    {
        // BUG-10 made CosineSimilarity return 0 for a *zero-magnitude* vector, but a NaN *element* in an
        // embedding still propagates NaN (its L2 norm is NaN, so the near-zero guard is skipped). PERF-08
        // pins the contract that such a document never appears in results — and never reaches the
        // OrderByDescending — so a poisoned embedding cannot corrupt ranking. (NaN is excluded both by
        // the explicit guard and by `>= minScore`; this test pins the observable contract, not which
        // clause does it.)
        var store = new MemoryVectorStore();
        store.Add("good", "real doc", new float[] { 1f, 0f });
        store.Add("poisoned", "nan doc", new float[] { float.NaN, 0f });

        var results = store.Search(new float[] { 1f, 0f }, topK: 5);

        Assert.Single(results);
        Assert.Equal("good", results[0].Id);
        Assert.DoesNotContain(results, r => r.Id == "poisoned");
        Assert.False(float.IsNaN(results[0].Score));
    }

    [Fact]
    public void Search_RespectsMinScore()
    {
        var store = new MemoryVectorStore();
        store.Add("a", "exact", new float[] { 1f, 0f });       // cosine 1.0
        store.Add("b", "orthogonal", new float[] { 0f, 1f });  // cosine 0.0

        var results = store.Search(new float[] { 1f, 0f }, topK: 5, minScore: 0.5f);

        Assert.Single(results);
        Assert.Equal("a", results[0].Id);
    }

    [Fact]
    public void Search_EmptyStore_ReturnsEmpty()
    {
        var store = new MemoryVectorStore();
        var results = store.Search(new float[] { 1f, 0f });
        Assert.Empty(results);
    }
}
