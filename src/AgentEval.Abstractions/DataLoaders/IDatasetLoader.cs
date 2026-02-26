// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.DataLoaders;

/// <summary>
/// Interface for loading test datasets.
/// </summary>
public interface IDatasetLoader
{
    /// <summary>The format this loader handles (e.g., "jsonl", "json", "csv").</summary>
    string Format { get; }
    
    /// <summary>File extensions this loader can handle.</summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    /// Indicates whether <see cref="LoadStreamingAsync"/> provides true streaming
    /// (line-by-line without buffering the entire file).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>true</c> — JSONL and CSV loaders stream row-by-row.</item>
    ///   <item><c>false</c> — JSON and YAML loaders must buffer the entire document before yielding items.</item>
    /// </list>
    /// </remarks>
    bool IsTrulyStreaming { get; }

    /// <summary>Load all test cases from a file.</summary>
    /// <param name="path">Path to the dataset file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of test cases.</returns>
    Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default);
    
    /// <summary>Load test cases as a streaming enumerable (memory-efficient for large files).</summary>
    /// <remarks>
    /// <para>
    /// <strong>Important:</strong> Not all loaders provide true streaming. JSONL and CSV loaders
    /// stream line-by-line without buffering the entire file. JSON and YAML loaders must buffer
    /// the entire document in memory first, then yield items — they do not reduce peak memory.
    /// </para>
    /// <para>
    /// For large datasets (10,000+ rows), prefer JSONL format for genuine streaming benefits.
    /// Check <see cref="IsTrulyStreaming"/> to determine whether a loader truly streams.
    /// </para>
    /// </remarks>
    /// <param name="path">Path to the dataset file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of test cases.</returns>
    IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(string path, CancellationToken ct = default);
}
