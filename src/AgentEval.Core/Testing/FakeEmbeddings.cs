// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Embeddings;

namespace AgentEval.Testing;

/// <summary>
/// Fake embeddings implementation for testing without external API calls.
/// Generates deterministic pseudo-embeddings based on text content.
/// 
/// This is useful for:
/// - Running samples without Azure credentials
/// - Unit testing embedding-based metrics
/// - CI/CD pipelines where API costs should be avoided
/// </summary>
/// <remarks>
/// The embeddings are NOT semantically meaningful but provide:
/// - Reproducibility: Same text always produces same embedding
/// - Crude similarity: Texts sharing words produce somewhat similar embeddings
/// - Differentiation: Different texts produce different embeddings
/// </remarks>
public sealed class FakeEmbeddings : IAgentEvalEmbeddings
{
    private readonly int _dimensions;
    
    /// <summary>
    /// Creates a new FakeEmbeddings instance.
    /// </summary>
    /// <param name="dimensions">Number of dimensions for the fake embeddings (default 1536 matches OpenAI ada-002).</param>
    public FakeEmbeddings(int dimensions = 1536)
    {
        _dimensions = dimensions;
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<float>> GetEmbeddingAsync(
        string text, 
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(GenerateDeterministicEmbedding(text));
    }
    
    /// <inheritdoc />
    public Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts, 
        CancellationToken cancellationToken = default)
    {
        var embeddings = new List<ReadOnlyMemory<float>>();
        
        foreach (var text in texts)
        {
            embeddings.Add(GenerateDeterministicEmbedding(text));
        }
        
        return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(embeddings);
    }

    /// <summary>
    /// Generates a deterministic "embedding" based on text content.
    /// </summary>
    private ReadOnlyMemory<float> GenerateDeterministicEmbedding(string text)
    {
        var embedding = new float[_dimensions];

        // Use a STABLE hash as the RNG seed for reproducibility. String.GetHashCode() is
        // randomized per process on .NET Core+, so it produced different embeddings across
        // runs and broke the documented cross-process determinism (BUG-25). FNV-1a over the
        // UTF-8 bytes is deterministic across processes and platforms.
        var textSeed = unchecked((int)StableHash(text));
        var textRandom = new Random(textSeed);
        
        // Generate base embedding from text hash
        for (int i = 0; i < _dimensions; i++)
        {
            embedding[i] = (float)(textRandom.NextDouble() * 2 - 1); // Range [-1, 1]
        }
        
        // Add some word-based features for crude "similarity"
        // Words are hashed to indices and contribute to those dimensions
        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var word in words)
        {
            // Stable per-word hash (see textSeed note above); GetHashCode() would vary per process.
            var wordHash = StableHash(word);
            var indices = new[] 
            { 
                (int)(wordHash % (uint)_dimensions),
                (int)((wordHash * 31) % (uint)_dimensions),
                (int)((wordHash * 37) % (uint)_dimensions)
            };
            
            foreach (var idx in indices)
            {
                embedding[idx] += 0.1f; // Boost dimensions associated with this word
            }
        }
        
        // Normalize to unit vector (required for cosine similarity)
        var magnitude = (float)Math.Sqrt(embedding.Sum(x => x * x));
        for (int i = 0; i < _dimensions; i++)
        {
            embedding[i] /= magnitude;
        }
        
        return embedding;
    }

    /// <summary>
    /// Deterministic, process-stable 32-bit FNV-1a hash of the UTF-8 bytes of <paramref name="s"/>.
    /// Used instead of <see cref="string.GetHashCode()"/> (which is randomized per process on
    /// .NET Core+) so the same text always yields the same fake embedding across runs (BUG-25).
    /// </summary>
    internal static uint StableHash(string s)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(s))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }
}
