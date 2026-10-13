// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;
using System.Text;
using System.Text.Json;

namespace AgentEval.DataLoaders;

/// <summary>
/// Static convenience façade for creating dataset loaders.
/// </summary>
/// <remarks>
/// Delegates to a shared <see cref="DefaultDatasetLoaderFactory"/> instance internally.
/// For DI scenarios, inject <see cref="IDatasetLoaderFactory"/> instead.
/// </remarks>
public static class DatasetLoaderFactory
{
    private static readonly DefaultDatasetLoaderFactory s_default = new();

    /// <summary>
    /// Create a loader based on file extension.
    /// </summary>
    public static IDatasetLoader CreateFromExtension(string extension)
        => s_default.CreateFromExtension(extension);

    /// <summary>
    /// Create a loader for a specific format.
    /// </summary>
    public static IDatasetLoader Create(string format)
        => s_default.Create(format);

    /// <summary>
    /// Register a custom loader for an extension.
    /// </summary>
    public static void Register(string extension, Func<IDatasetLoader> factory)
        => s_default.Register(extension, factory);

    /// <summary>
    /// Load all test cases from a file, auto-detecting the loader from the file extension.
    /// </summary>
    /// <param name="path">Path to the dataset file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of dataset test cases.</returns>
    public static Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(path);
        var loader = s_default.CreateFromExtension(extension);
        return loader.LoadAsync(path, ct);
    }

    /// <summary>
    /// Loads a supplied UTF-8 snapshot with the built-in JSON, JSONL, YAML, CSV or TSV parser, without opening files.
    /// A caller can hash these same bytes to bind execution to the content it inspected.
    /// </summary>
    /// <param name="content">The dataset bytes; decoded before asynchronous parsing begins.</param>
    /// <param name="extension">The file extension, including its dot; registered custom loaders are not used.</param>
    /// <param name="requireExplicitIds">Refuse missing or duplicate IDs instead of generating them; preserve CSV IDs without trimming.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The cases in their original order.</returns>
    /// <exception cref="InvalidDataException">The format is unsupported, UTF-8 is invalid, or required IDs are missing or duplicated.</exception>
    public static async Task<IReadOnlyList<DatasetTestCase>> LoadSnapshotAsync(ReadOnlyMemory<byte> content,
        string extension, bool requireExplicitIds = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(extension);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content.Span); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Dataset snapshot must be valid UTF-8.", ex); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        IReadOnlyList<DatasetTestCase> cases;
        switch (extension.ToLowerInvariant())
        {
            case ".json":
                using (var document = JsonDocument.Parse(text, new JsonDocumentOptions
                    { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                    cases = JsonDatasetLoader.ParseDocument(document, "snapshot", requireExplicitIds);
                break;
            case ".jsonl":
            case ".ndjson":
                var lines = new List<DatasetTestCase>();
                using (var reader = new StringReader(text))
                {
                    string? line;
                    var number = 0;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        ct.ThrowIfCancellationRequested();
                        number++;
                        if (!string.IsNullOrWhiteSpace(line))
                            lines.Add(JsonlDatasetLoader.ParseLine(line, number, requireExplicitIds));
                    }
                }
                cases = lines;
                break;
            case ".yaml":
            case ".yml":
                cases = YamlDatasetLoader.ParseYaml(text, "snapshot", requireExplicitIds);
                break;
            case ".csv":
            case ".tsv":
                var rows = new List<DatasetTestCase>();
                using (var reader = new StringReader(text))
                    await foreach (var item in CsvDatasetLoader.ParseReaderAsync(reader,
                        extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',', "snapshot", requireExplicitIds, ct))
                        rows.Add(item);
                cases = rows;
                break;
            default:
                throw new InvalidDataException("Dataset snapshots support .json, .jsonl, .ndjson, .yaml, .yml, .csv and .tsv.");
        }
        ct.ThrowIfCancellationRequested();
        if (requireExplicitIds)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in cases)
                if (string.IsNullOrWhiteSpace(item.Id) || !seen.Add(item.Id))
                    throw new InvalidDataException("Every snapshot case requires an explicit, non-empty, unique ID.");
        }
        return cases;
    }

    /// <summary>
    /// Stream test cases from a file, auto-detecting the loader from the file extension.
    /// </summary>
    /// <param name="path">Path to the dataset file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of dataset test cases.</returns>
    public static IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(string path, CancellationToken ct = default)
    {
        var extension = Path.GetExtension(path);
        var loader = s_default.CreateFromExtension(extension);
        return loader.LoadStreamingAsync(path, ct);
    }
}
