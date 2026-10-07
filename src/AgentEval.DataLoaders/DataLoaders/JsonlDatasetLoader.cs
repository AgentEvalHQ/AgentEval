// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Models;

namespace AgentEval.DataLoaders;

/// <summary>
/// Loads test cases from JSONL (JSON Lines) format.
/// This is the industry standard format for AI datasets (HuggingFace, etc.).
/// </summary>
/// <remarks>
/// JSONL format is one JSON object per line, making it ideal for:
/// - Streaming large datasets without loading everything into memory
/// - Appending new test cases without rewriting the file
/// - Git-friendly diffs (line-based)
/// </remarks>
public class JsonlDatasetLoader : IDatasetLoader
{
    /// <inheritdoc />
    public string Format => "jsonl";
    
    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => new[] { ".jsonl", ".ndjson" };

    /// <inheritdoc />
    public bool IsTrulyStreaming => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        var results = new List<DatasetTestCase>();
        await foreach (var testCase in LoadStreamingAsync(path, ct))
        {
            results.Add(testCase);
        }
        return results;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(
        string path, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Dataset file not found: {path}", path);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 
            bufferSize: 4096, useAsync: true);
        using var reader = new StreamReader(stream);
        
        int lineNumber = 0;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            lineNumber++;
            
            if (string.IsNullOrWhiteSpace(line))
            {
                continue; // Skip empty lines
            }
            
            DatasetTestCase testCase;
            try
            {
                testCase = ParseLine(line, lineNumber);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Invalid JSON at line {lineNumber} in {path}: {ex.Message}", ex);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException($"Line {lineNumber} in {path}: {ex.Message}", ex);
            }

            yield return testCase;
        }
    }

    private static DatasetTestCase ParseLine(string line, int lineNumber)
    {
        using var doc = JsonDocument.Parse(line, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"the line is a JSON {root.ValueKind}, not a test case object.");
        }

        return JsonParsingHelper.ParseTestCase(root, $"line_{lineNumber}");
    }
}
