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
            Id = GetStringOrDefault(root, "id", $"line_{lineNumber}"),
            Category = GetStringOrNull(root, "category"),
            Input = GetStringOrDefault(root, "input", GetStringOrDefault(root, "question", GetStringOrDefault(root, "prompt", ""))),
            ExpectedOutput = GetStringOrNull(root, "expected") ?? GetStringOrNull(root, "expected_output") ?? GetStringOrNull(root, "answer"),
        };
        
        // Parse context array
        if (root.TryGetProperty("context", out var contextProp))
        {
            testCase.Context = ParseStringArray(contextProp);
        }
        else if (root.TryGetProperty("contexts", out var contextsProp))
        {
            testCase.Context = ParseStringArray(contextsProp);
        }
        
        // Parse expected tools
        if (root.TryGetProperty("expected_tools", out var toolsProp))
        {
            testCase.ExpectedTools = ParseStringArray(toolsProp);
        }
        
        // Parse ground truth (for BFCL-style benchmarks)
        if (root.TryGetProperty("ground_truth", out var gtProp))
        {
            testCase.GroundTruth = ParseGroundTruth(gtProp);
        }
        else if (root.TryGetProperty("function", out var funcProp) && 
                 root.TryGetProperty("arguments", out var argsProp))
        {
            // Alternative format: { "function": "name", "arguments": {...} }
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = funcProp.GetString() ?? "",
                Arguments = ParseArguments(argsProp)
            };
        }
        
        // Collect any extra properties as metadata
        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();
            if (!IsKnownProperty(name))
            {
                testCase.Metadata[prop.Name] = GetJsonValue(prop.Value);
            }
        }
        
        return testCase;
    }

    private static bool IsKnownProperty(string name) => name switch
    {
        "id" or "category" or "input" or "question" or "prompt" => true,
        "expected" or "expected_output" or "answer" => true,
        "context" or "contexts" or "expected_tools" => true,
        "ground_truth" or "function" or "arguments" => true,
        _ => false
    };

    private static string GetStringOrDefault(JsonElement element, string propertyName, string defaultValue)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? defaultValue
            : defaultValue;
    }

    private static string? GetStringOrNull(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static IReadOnlyList<string>? ParseStringArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            return new[] { element.GetString()! };
        }
        return null;
    }

    private static GroundTruthToolCall? ParseGroundTruth(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = GetStringOrDefault(element, "name", GetStringOrDefault(element, "function", ""));
        var args = element.TryGetProperty("arguments", out var argsProp) 
            ? ParseArguments(argsProp) 
            : new Dictionary<string, object?>();

        return new GroundTruthToolCall { Name = name, Arguments = args };
    }

    private static Dictionary<string, object?> ParseArguments(JsonElement element)
    {
        var args = new Dictionary<string, object?>();
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                args[prop.Name] = GetJsonValue(prop.Value);
            }
        }
        return args;
    }

    private static object? GetJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(GetJsonValue).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => GetJsonValue(p.Value)),
        _ => element.GetRawText()
    };
}
