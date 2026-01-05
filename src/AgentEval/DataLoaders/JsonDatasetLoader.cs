// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.Json;

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

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

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
            // Try common property names for the array
            if (root.TryGetProperty("data", out var dataProp))
            {
                arrayElement = dataProp;
            }
            else if (root.TryGetProperty("testCases", out var testCasesProp))
            {
                arrayElement = testCasesProp;
            }
            else if (root.TryGetProperty("test_cases", out var testCasesSnakeProp))
            {
                arrayElement = testCasesSnakeProp;
            }
            else if (root.TryGetProperty("examples", out var examplesProp))
            {
                arrayElement = examplesProp;
            }
            else if (root.TryGetProperty("samples", out var samplesProp))
            {
                arrayElement = samplesProp;
            }
            else
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
            var testCase = ParseTestCase(item, index);
            if (testCase != null)
            {
                results.Add(testCase);
            }
            index++;
        }

        return results;
    }

    private static DatasetTestCase? ParseTestCase(JsonElement element, int index)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var testCase = new DatasetTestCase
        {
            Id = GetStringOrDefault(element, "id", $"item_{index}"),
            Category = GetStringOrNull(element, "category"),
            Input = GetStringOrDefault(element, "input", 
                GetStringOrDefault(element, "question", 
                GetStringOrDefault(element, "prompt", 
                GetStringOrDefault(element, "query", "")))),
            ExpectedOutput = GetStringOrNull(element, "expected") 
                ?? GetStringOrNull(element, "expected_output") 
                ?? GetStringOrNull(element, "answer")
                ?? GetStringOrNull(element, "response"),
        };

        // Parse context
        if (element.TryGetProperty("context", out var contextProp))
        {
            testCase.Context = ParseStringArray(contextProp);
        }
        else if (element.TryGetProperty("contexts", out var contextsProp))
        {
            testCase.Context = ParseStringArray(contextsProp);
        }
        else if (element.TryGetProperty("documents", out var docsProp))
        {
            testCase.Context = ParseStringArray(docsProp);
        }

        // Parse expected tools
        if (element.TryGetProperty("expected_tools", out var toolsProp))
        {
            testCase.ExpectedTools = ParseStringArray(toolsProp);
        }
        else if (element.TryGetProperty("tools", out var toolsProp2))
        {
            testCase.ExpectedTools = ParseStringArray(toolsProp2);
        }

        // Parse ground truth
        if (element.TryGetProperty("ground_truth", out var gtProp))
        {
            testCase.GroundTruth = ParseGroundTruth(gtProp);
        }
        else if (element.TryGetProperty("function", out var funcProp) &&
                 element.TryGetProperty("arguments", out var argsProp))
        {
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = funcProp.GetString() ?? "",
                Arguments = ParseArguments(argsProp)
            };
        }

        // Collect metadata
        foreach (var prop in element.EnumerateObject())
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
        "id" or "category" => true,
        "input" or "question" or "prompt" or "query" => true,
        "expected" or "expected_output" or "answer" or "response" => true,
        "context" or "contexts" or "documents" => true,
        "expected_tools" or "tools" => true,
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
