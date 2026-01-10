// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;

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

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

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
            
            DatasetTestCase? testCase;
            try
            {
                testCase = ParseLine(line, lineNumber);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"Invalid JSON at line {lineNumber} in {path}: {ex.Message}", ex);
            }
            
            if (testCase != null)
            {
                yield return testCase;
            }
        }
    }

    private static DatasetTestCase? ParseLine(string line, int lineNumber)
    {
        var doc = JsonDocument.Parse(line, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        
        var root = doc.RootElement;
        
        var testCase = new DatasetTestCase
        {
            Id = JsonParsingHelper.GetStringOrDefault(root, "id", $"line_{lineNumber}"),
            Category = JsonParsingHelper.GetStringOrNull(root, "category"),
            Input = JsonParsingHelper.GetInput(root),
            ExpectedOutput = JsonParsingHelper.GetExpectedOutput(root),
        };
        
        // Parse context array
        if (root.TryGetProperty("context", out var contextProp))
        {
            testCase.Context = JsonParsingHelper.ParseStringArray(contextProp);
        }
        else if (root.TryGetProperty("contexts", out var contextsProp))
        {
            testCase.Context = JsonParsingHelper.ParseStringArray(contextsProp);
        }
        
        // Parse expected tools
        if (root.TryGetProperty("expected_tools", out var toolsProp))
        {
            testCase.ExpectedTools = JsonParsingHelper.ParseStringArray(toolsProp);
        }
        
        // Parse ground truth (for BFCL-style benchmarks)
        if (root.TryGetProperty("ground_truth", out var gtProp))
        {
            testCase.GroundTruth = JsonParsingHelper.ParseGroundTruth(gtProp);
        }
        else if (root.TryGetProperty("function", out var funcProp) && 
                 root.TryGetProperty("arguments", out var argsProp))
        {
            // Alternative format: { "function": "name", "arguments": {...} }
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = funcProp.GetString() ?? "",
                Arguments = JsonParsingHelper.ParseArguments(argsProp)
            };
        }
        
        // Collect any extra properties as metadata
        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();
            if (!JsonParsingHelper.IsKnownProperty(name))
            {
                testCase.Metadata[prop.Name] = JsonParsingHelper.GetJsonValue(prop.Value);
            }
        }
        
        return testCase;
    }
}
