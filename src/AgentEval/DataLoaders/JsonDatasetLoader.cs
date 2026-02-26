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
            Id = JsonParsingHelper.GetStringOrDefault(element, "id", $"item_{index}"),
            Category = JsonParsingHelper.GetStringOrNull(element, "category"),
            Input = JsonParsingHelper.GetInput(element),
            ExpectedOutput = JsonParsingHelper.GetExpectedOutput(element),
        };

        // Parse context
        if (element.TryGetProperty("context", out var contextProp))
        {
            testCase.Context = JsonParsingHelper.ParseStringArray(contextProp);
        }
        else if (element.TryGetProperty("contexts", out var contextsProp))
        {
            testCase.Context = JsonParsingHelper.ParseStringArray(contextsProp);
        }
        else if (element.TryGetProperty("documents", out var docsProp))
        {
            testCase.Context = JsonParsingHelper.ParseStringArray(docsProp);
        }

        // Parse expected tools
        if (element.TryGetProperty("expected_tools", out var toolsProp))
        {
            testCase.ExpectedTools = JsonParsingHelper.ParseStringArray(toolsProp);
        }
        else if (element.TryGetProperty("tools", out var toolsProp2))
        {
            testCase.ExpectedTools = JsonParsingHelper.ParseStringArray(toolsProp2);
        }

        // Parse ground truth
        if (element.TryGetProperty("ground_truth", out var gtProp))
        {
            testCase.GroundTruth = JsonParsingHelper.ParseGroundTruth(gtProp);
        }
        else if (element.TryGetProperty("function", out var funcProp) &&
                 element.TryGetProperty("arguments", out var argsProp))
        {
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = funcProp.GetString() ?? "",
                Arguments = JsonParsingHelper.ParseArguments(argsProp)
            };
        }

        // Parse evaluation criteria
        if (element.TryGetProperty("evaluation_criteria", out var criteriaProp))
        {
            testCase.EvaluationCriteria = JsonParsingHelper.ParseStringArray(criteriaProp);
        }
        
        // Parse tags
        if (element.TryGetProperty("tags", out var tagsProp))
        {
            testCase.Tags = JsonParsingHelper.ParseStringArray(tagsProp);
        }
        
        // Parse passing score
        if (element.TryGetProperty("passing_score", out var scoreProp) && scoreProp.ValueKind == JsonValueKind.Number)
        {
            testCase.PassingScore = scoreProp.GetInt32();
        }
        
        // Collect metadata
        foreach (var prop in element.EnumerateObject())
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
