// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Testing;

/// <summary>
/// Regression coverage for BUG-25: FakeEmbeddings derived its RNG seed and per-word indices
/// from String.GetHashCode(), which is randomized per process on .NET Core+ — so the same
/// text produced different embeddings across runs, breaking the documented cross-process
/// reproducibility used for snapshot / cosine assertions.
/// </summary>
public class FakeEmbeddingsTests
{
    [Fact]
    public void StableHash_MatchesCanonicalFnv1a_NotProcessRandomizedGetHashCode()
    {
        // FNV-1a/32 canonical test vectors. These are fixed for all time and all processes;
        // String.GetHashCode() could not reproduce them (its empty-string hash is not the
        // FNV offset basis and varies per process under randomized hashing).
        Assert.Equal(2166136261u, FakeEmbeddings.StableHash(""));     // FNV offset basis
        Assert.Equal(1335831723u, FakeEmbeddings.StableHash("hello")); // 0x4F9F2CAB
    }

    [Fact]
    public async Task GetEmbedding_IsDeterministic_AcrossInstancesAndCalls()
    {
        var a = await new FakeEmbeddings(64).GetEmbeddingAsync("reproducible text");
        var b = await new FakeEmbeddings(64).GetEmbeddingAsync("reproducible text");

        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact]
    public async Task GetEmbedding_MatchesProcessStableGoldenVector()
    {
        // Golden vector captured once. Because the embedding is now derived from a stable
        // hash, these exact values must reproduce on every run/process/platform. If someone
        // reintroduces String.GetHashCode(), this fails.
        var embedding = (await new FakeEmbeddings(dimensions: 8)
            .GetEmbeddingAsync("the quick brown fox")).ToArray();

        float[] expected =
        {
            -0.41664445f, 0.4443696f, -0.16077635f, 0.51762694f,
            -0.17324933f, 0.20753773f, 0.31423557f, 0.40413508f,
        };

        Assert.Equal(expected.Length, embedding.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], embedding[i], precision: 5);
    }
}
