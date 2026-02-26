// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Embeddings;

/// <summary>
/// Interface for embedding generation in AgentEval.
/// Abstracts the embedding provider for flexibility.
/// </summary>
public interface IAgentEvalEmbeddings
{
    /// <summary>
    /// Generate an embedding for a single text.
    /// </summary>
    /// <param name="text">The text to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vector.</returns>
    Task<ReadOnlyMemory<float>> GetEmbeddingAsync(
        string text, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Generate embeddings for multiple texts in batch.
    /// </summary>
    /// <param name="texts">The texts to embed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The embedding vectors in the same order as input.</returns>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts, 
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapter that wraps Microsoft.Extensions.AI IEmbeddingGenerator.
/// </summary>
public class MEAIEmbeddingAdapter : IAgentEvalEmbeddings
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    
    /// <summary>
    /// Creates a new adapter wrapping a Microsoft.Extensions.AI embedding generator.
    /// </summary>
    /// <param name="generator">The embedding generator to wrap.</param>
    public MEAIEmbeddingAdapter(IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }
    
    /// <inheritdoc />
    public async Task<ReadOnlyMemory<float>> GetEmbeddingAsync(
        string text, 
        CancellationToken cancellationToken = default)
    {
        // Use the extension method for single value embedding generation
        var result = await _generator.GenerateAsync(text, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        
        return result.Vector;
    }
    
    /// <inheritdoc />
    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GetEmbeddingsAsync(
        IEnumerable<string> texts, 
        CancellationToken cancellationToken = default)
    {
        var textList = texts.ToList();
        
        // Use the core interface method for batch embedding generation
        var results = await _generator.GenerateAsync(textList, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        
        return results.Select(e => e.Vector).ToList();
    }
}
