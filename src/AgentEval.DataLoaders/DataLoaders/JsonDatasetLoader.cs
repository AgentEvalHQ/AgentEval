// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Models;

namespace AgentEval.DataLoaders;

/// <summary>
/// Loads test cases from standard JSON array format.
/// </summary>
/// <remarks>
/// Expects a JSON file with an array of test case objects:
/// <code>
/// [
///   { "id": "test1", "input": "...", "expected": "..." },
///   { "id": "test2", "input": "...", "expected": "..." }
/// ]
/// </code>
/// 
/// Or an object with a "data" or "testCases" property containing the array:
/// <code>
/// {
///   "metadata": { ... },
///   "testCases": [ ... ]
/// }
/// </code>
/// </remarks>
public class JsonDatasetLoader : IDatasetLoader
{
    /// <inheritdoc />
    public string Format => "json";
    
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => new[] { ".json" };

    /// <inheritdoc />
    public bool IsTrulyStreaming => false;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Dataset file not found: {path}", path);
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        
        var doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }, ct);

        return ParseDocument(doc, path);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(
        string path, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // For JSON, we need to load the full document first, then yield items
        // True streaming would require a different JSON parser
        var items = await LoadAsync(path, ct);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static IReadOnlyList<DatasetTestCase> ParseDocument(JsonDocument doc, string path)
    {
        var root = doc.RootElement;
        JsonElement arrayElement;

        // Detect format: array or object with data property
        if (root.ValueKind == JsonValueKind.Array)
        {
            arrayElement = root;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            // Try common property names for the array (any spelling: testCases, test_cases, TestCases)
            if (!JsonParsingHelper.TryGetField(root, "data", out arrayElement)
                && !JsonParsingHelper.TryGetField(root, "test_cases", out arrayElement)
                && !JsonParsingHelper.TryGetField(root, "examples", out arrayElement)
                && !JsonParsingHelper.TryGetField(root, "samples", out arrayElement))
            {
                throw new InvalidDataException(
                    $"JSON file must be an array or object with 'data', 'testCases', 'test_cases', 'examples', or 'samples' property: {path}");
            }
        }
        else
        {
            throw new InvalidDataException(
                $"JSON file must be an array or object at root level: {path}");
        }

        if (arrayElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Test cases element must be an array: {path}");
        }

        var results = new List<DatasetTestCase>();
        int index = 0;
        foreach (var item in arrayElement.EnumerateArray())
        {
            results.Add(ParseTestCase(item, index, path));
            index++;
        }

        return results;
    }

    private static DatasetTestCase ParseTestCase(JsonElement element, int index, string path)
    {
        // An item that is not an object used to be skipped without a word, so a dataset could run fewer cases than
        // it holds.
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Test case {index} in {path} is a JSON {element.ValueKind}, not an object.");
        }

        try
        {
            return JsonParsingHelper.ParseTestCase(element, $"item_{index}");
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Test case {index} in {path}: {ex.Message}", ex);
        }
    }
}
