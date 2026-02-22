// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
    
    /// <summary>Load all test cases from a file.</summary>
    /// <param name="path">Path to the dataset file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of test cases.</returns>
    Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default);
    
    /// <summary>Load test cases as a streaming enumerable (memory-efficient for large files).</summary>
    /// <param name="path">Path to the dataset file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of test cases.</returns>
    IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// A test case loaded from a dataset.
/// </summary>
public class DatasetTestCase
{
    /// <summary>Unique identifier for this test case.</summary>
    public string Id { get; set; } = "";
    
    /// <summary>Category or group (optional).</summary>
    public string? Category { get; set; }
    
    /// <summary>The input prompt/query.</summary>
    public string Input { get; set; } = "";
    
    /// <summary>Expected output/answer (for comparison).</summary>
    public string? ExpectedOutput { get; set; }
    
    /// <summary>Context documents (for RAG evaluation).</summary>
    public IReadOnlyList<string>? Context { get; set; }
    
    /// <summary>Expected tools to be called.</summary>
    public IReadOnlyList<string>? ExpectedTools { get; set; }
    
    /// <summary>Ground truth tool call (for function calling benchmarks).</summary>
    public GroundTruthToolCall? GroundTruth { get; set; }
    
    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object?> Metadata { get; set; } = new();
}

/// <summary>
/// Ground truth for a tool/function call (used in BFCL-style benchmarks).
/// </summary>
public class GroundTruthToolCall
{
    /// <summary>Tool/function name.</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Expected arguments.</summary>
    public Dictionary<string, object?> Arguments { get; set; } = new();
}

/// <summary>
/// Factory for creating dataset loaders.
/// </summary>
public static class DatasetLoaderFactory
{
    private static readonly Dictionary<string, Func<IDatasetLoader>> s_loaders = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jsonl"] = () => new JsonlDatasetLoader(),
        [".ndjson"] = () => new JsonlDatasetLoader(),
        [".json"] = () => new JsonDatasetLoader(),
        [".csv"] = () => new CsvDatasetLoader(),
        [".tsv"] = () => new CsvDatasetLoader('\t'),
        [".yaml"] = () => new YamlDatasetLoader(),
        [".yml"] = () => new YamlDatasetLoader(),
    };
    
    /// <summary>
    /// Create a loader based on file extension.
    /// </summary>
    public static IDatasetLoader CreateFromExtension(string extension)
    {
        if (s_loaders.TryGetValue(extension, out var factory))
        {
            return factory();
        }
        throw new ArgumentException($"No loader available for extension: {extension}", nameof(extension));
    }
    
    /// <summary>
    /// Create a loader for a specific format.
    /// </summary>
    public static IDatasetLoader Create(string format) => format.ToLowerInvariant() switch
    {
        "jsonl" => new JsonlDatasetLoader(),
        "json" => new JsonDatasetLoader(),
        "csv" => new CsvDatasetLoader(),
        "yaml" => new YamlDatasetLoader(),
        _ => throw new ArgumentException($"Unknown format: {format}", nameof(format))
    };
    
    /// <summary>
    /// Register a custom loader for an extension.
    /// </summary>
    public static void Register(string extension, Func<IDatasetLoader> factory)
    {
        s_loaders[extension] = factory;
    }
}
