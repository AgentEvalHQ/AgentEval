// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Embeddings;

/// <summary>
/// A simple in-memory vector store for demos and testing.
/// Uses cosine similarity for searching.
/// 
/// This is NOT intended for production use - use a proper vector database
/// like Azure AI Search, Pinecone, Qdrant, or Milvus for real applications.
/// </summary>
/// <remarks>
/// Thread-safe via a single lock: writes (Add/AddRange/Clear) are mutually exclusive, and
/// <see cref="Search"/> snapshots the document set under a brief lock then scores/sorts OUTSIDE the
/// lock so a long O(n) scan does not block writes (PERF-08). Intended for demos/testing only.
/// </remarks>
public class MemoryVectorStore
{
    private readonly List<VectorDocument> _documents = [];
    private readonly object _lock = new();

    /// <summary>
    /// Gets the number of documents in the store.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _documents.Count;
            }
        }
    }

    /// <summary>
    /// Add a document with its embedding to the store.
    /// </summary>
    /// <param name="id">Unique identifier for the document.</param>
    /// <param name="text">The text content of the document.</param>
    /// <param name="embedding">The embedding vector for the document.</param>
    /// <param name="metadata">Optional metadata for the document.</param>
    public void Add(string id, string text, ReadOnlyMemory<float> embedding, Dictionary<string, object>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(text);

        lock (_lock)
        {
            _documents.Add(new VectorDocument(id, text, embedding, metadata ?? []));
        }
    }

    /// <summary>
    /// Add multiple documents with their embeddings to the store.
    /// </summary>
    /// <param name="documents">Collection of (id, text, embedding) tuples.</param>
    public void AddRange(IEnumerable<(string Id, string Text, ReadOnlyMemory<float> Embedding)> documents)
    {
        lock (_lock)
        {
            foreach (var (id, text, embedding) in documents)
            {
                _documents.Add(new VectorDocument(id, text, embedding, []));
            }
        }
    }

    /// <summary>
    /// Search for the most similar documents to a query embedding.
    /// </summary>
    /// <param name="queryEmbedding">The embedding of the search query.</param>
    /// <param name="topK">Maximum number of results to return.</param>
    /// <param name="minScore">Minimum similarity score (0-1) to include in results.</param>
    /// <returns>Top-K most similar documents ordered by similarity (descending).</returns>
    public IReadOnlyList<SearchResult> Search(ReadOnlyMemory<float> queryEmbedding, int topK = 3, float minScore = 0.0f)
    {
        // PERF-08: snapshot the document set under a brief lock, then do the O(n) projection/score/sort
        // OUTSIDE the lock. The full scan no longer blocks concurrent Add/AddRange/Clear writers. The
        // snapshot copies references only (VectorDocument holds a ReadOnlyMemory<float>), so it is cheap
        // and the embeddings are not deep-copied.
        List<VectorDocument> snapshot;
        lock (_lock)
        {
            snapshot = new List<VectorDocument>(_documents);
        }

        return snapshot
            .Select(doc => new SearchResult(
                doc.Id,
                doc.Text,
                EmbeddingSimilarity.CosineSimilarity(queryEmbedding, doc.Embedding),
                doc.Metadata))
            // Explicitly drop NaN scores (a NaN element in an embedding propagates through CosineSimilarity)
            // before ranking. `r.Score >= minScore` already excludes NaN today (every NaN comparison is
            // false, for any minScore), so this is defensive/intent-documenting: it keeps NaN out of
            // OrderByDescending even if the predicate is ever reordered or relaxed.
            .Where(r => !float.IsNaN(r.Score) && r.Score >= minScore)
            .OrderByDescending(r => r.Score)
            .Take(topK)
            .ToList();
    }

    /// <summary>
    /// Clear all documents from the store.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _documents.Clear();
        }
    }

    /// <summary>
    /// Check if a document with the given ID exists.
    /// </summary>
    public bool Contains(string id)
    {
        lock (_lock)
        {
            return _documents.Any(d => d.Id == id);
        }
    }

    /// <summary>
    /// Get all document IDs in the store.
    /// </summary>
    public IReadOnlyList<string> GetAllIds()
    {
        lock (_lock)
        {
            return _documents.Select(d => d.Id).ToList();
        }
    }

    /// <summary>
    /// Represents a document stored in the vector store.
    /// </summary>
    private sealed record VectorDocument(
        string Id,
        string Text,
        ReadOnlyMemory<float> Embedding,
        Dictionary<string, object> Metadata);

    /// <summary>
    /// Represents a search result with similarity score.
    /// </summary>
    /// <param name="Id">Document identifier.</param>
    /// <param name="Text">Document text content.</param>
    /// <param name="Score">Cosine similarity score (0-1, higher is more similar).</param>
    /// <param name="Metadata">Document metadata.</param>
    public sealed record SearchResult(
        string Id,
        string Text,
        float Score,
        Dictionary<string, object> Metadata);
}
