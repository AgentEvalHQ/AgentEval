// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Runtime.CompilerServices;
using AgentEval.Models;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace AgentEval.DataLoaders;

/// <summary>
/// Loads test cases from YAML format.
/// </summary>
/// <remarks>
/// Supports YAML files with either an array of test cases at root level:
/// <code>
/// - id: test1
///   input: What is 2+2?
///   expected: 4
/// - id: test2
///   input: Capital of France?
///   expected: Paris
/// </code>
///
/// Or an object with a data property:
/// <code>
/// metadata:
///   version: 1.0
/// testCases:
///   - id: test1
///     input: ...
/// </code>
///
/// Field names match in any spelling (<c>expected_output</c>, <c>expectedOutput</c>, <c>ExpectedOutput</c>), as in
/// the JSON, JSONL and CSV loaders. A key that is not a test-case field is kept in
/// <see cref="DatasetTestCase.Metadata"/>, as the JSON loaders do, and a <c>metadata:</c> mapping is merged into it.
/// </remarks>
public class YamlDatasetLoader : IDatasetLoader
{
    /// <inheritdoc />
    public string Format => "yaml";

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedExtensions => new[] { ".yaml", ".yml" };

    /// <inheritdoc />
    public bool IsTrulyStreaming => false;

    // Untyped: mappings come back as Dictionary<object, object>, sequences as List<object>, scalars as strings, so
    // every key can be matched in any spelling and none is dropped unseen.
    private static readonly IDeserializer s_deserializer = new DeserializerBuilder().Build();

    // The keys that may hold the list of test cases in a wrapper object, in order of preference.
    private static readonly string[] s_listKeys = ["test_cases", "data", "examples", "samples"];

    /// <inheritdoc />
    public async Task<IReadOnlyList<DatasetTestCase>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Dataset file not found: {path}", path);
        }

        var content = await File.ReadAllTextAsync(path, ct);
        return ParseYaml(content, path);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DatasetTestCase> LoadStreamingAsync(
        string path,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // For YAML, we load the full document then yield items
        var items = await LoadAsync(path, ct);
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    private static IReadOnlyList<DatasetTestCase> ParseYaml(string content, string path)
    {
        object? root;
        try
        {
            root = s_deserializer.Deserialize<object?>(content);
        }
        catch (YamlException ex)
        {
            // Name the line: before, a syntax error was swallowed and reported as a file of the wrong shape.
            throw new InvalidDataException(
                $"Invalid YAML in {path} at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", ex);
        }

        var items = root switch
        {
            List<object?> list => list,
            Dictionary<object, object?> map => FindTestCaseList(map, path),
            _ => null,
        };

        if (items is null)
        {
            throw new InvalidDataException(
                $"YAML file must be an array of test cases or object with 'testCases', 'data', 'examples', or 'samples' property: {path}");
        }

        var results = new List<DatasetTestCase>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not Dictionary<object, object?> fields)
            {
                throw new InvalidDataException($"Test case {i} in {path} is not a mapping of fields.");
            }

            results.Add(ConvertToDatasetTestCase(fields, i, path));
        }

        return results;
    }

    private static List<object?>? FindTestCaseList(Dictionary<object, object?> map, string path)
    {
        foreach (var listKey in s_listKeys)
        {
            var wanted = DatasetFieldNames.Normalize(listKey);
            foreach (var (key, value) in map)
            {
                if (DatasetFieldNames.Normalize(KeyName(key)) != wanted)
                {
                    continue;
                }

                return value as List<object?>
                    ?? throw new InvalidDataException($"'{KeyName(key)}' in {path} must be a list of test cases.");
            }
        }

        return null;
    }

    private static DatasetTestCase ConvertToDatasetTestCase(Dictionary<object, object?> map, int index, string path)
    {
        var testCase = new DatasetTestCase();

        // Known fields by normalized name (the first spelling wins); everything else is metadata.
        var fields = new Dictionary<string, (string Key, object? Value)>(StringComparer.Ordinal);
        foreach (var (rawKey, value) in map)
        {
            var key = KeyName(rawKey);
            var name = DatasetFieldNames.Normalize(key);
            if (DatasetFieldNames.KnownFields.Contains(name))
            {
                fields.TryAdd(name, (key, value));
            }
            else if (name == "metadata" && value is Dictionary<object, object?> metadata)
            {
                foreach (var (metaKey, metaValue) in metadata)
                {
                    testCase.Metadata[KeyName(metaKey)] = ToPlain(metaValue);
                }
            }
            else
            {
                testCase.Metadata[key] = ToPlain(value);
            }
        }

        string Where(string key) => $"'{key}' of test case {index} in {path}";

        string? Scalar(params string[] names)
        {
            foreach (var n in names)
            {
                if (fields.TryGetValue(DatasetFieldNames.Normalize(n), out var field) && field.Value is not null)
                {
                    return field.Value as string
                        ?? throw new InvalidDataException($"{Where(field.Key)} must be a single value, not a list or mapping.");
                }
            }
            return null;
        }

        IReadOnlyList<string>? Strings(params string[] names)
        {
            foreach (var n in names)
            {
                if (fields.TryGetValue(DatasetFieldNames.Normalize(n), out var field) && field.Value is not null)
                {
                    return field.Value switch
                    {
                        string single => [single],
                        List<object?> list => list.Select(item => item switch
                        {
                            null => "",
                            string s => s,
                            _ => throw new InvalidDataException($"{Where(field.Key)} must be a list of strings."),
                        }).ToList(),
                        _ => throw new InvalidDataException($"{Where(field.Key)} must be a string or a list of strings."),
                    };
                }
            }
            return null;
        }

        testCase.Id = Scalar("id") ?? $"item_{index}";
        testCase.Category = Scalar("category");
        testCase.Input = Scalar("input", "question", "prompt", "query") ?? "";
        testCase.ExpectedOutput = Scalar("expected", "expected_output", "answer", "response");
        testCase.Context = Strings("context", "contexts", "documents");
        testCase.ExpectedTools = Strings("expected_tools", "tools");
        testCase.EvaluationCriteria = Strings("evaluation_criteria");
        testCase.Tags = Strings("tags");

        if (Scalar("passing_score") is { } passingScore)
        {
            testCase.PassingScore = int.TryParse(passingScore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score)
                ? score
                : throw new InvalidDataException($"{Where(fields["passingscore"].Key)} must be a whole number, not '{passingScore}'.");
        }

        if (fields.TryGetValue("groundtruth", out var groundTruth) && groundTruth.Value is not null)
        {
            if (groundTruth.Value is not Dictionary<object, object?> gt)
            {
                throw new InvalidDataException($"{Where(groundTruth.Key)} must be a mapping with 'name' and 'arguments'.");
            }

            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = Lookup(gt, "name") as string ?? Lookup(gt, "function") as string ?? "",
                Arguments = ToArguments(Lookup(gt, "arguments")),
            };
        }
        else if (Scalar("function") is { Length: > 0 } function)
        {
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = function,
                Arguments = ToArguments(fields.TryGetValue("arguments", out var args) ? args.Value : null),
            };
        }

        return testCase;
    }

    private static string KeyName(object? key) => Convert.ToString(key, CultureInfo.InvariantCulture) ?? "";

    private static object? Lookup(Dictionary<object, object?> map, string field)
    {
        var wanted = DatasetFieldNames.Normalize(field);
        foreach (var (key, value) in map)
        {
            if (DatasetFieldNames.Normalize(KeyName(key)) == wanted)
            {
                return value;
            }
        }
        return null;
    }

    private static Dictionary<string, object?> ToArguments(object? value) =>
        ToPlain(value) as Dictionary<string, object?> ?? new Dictionary<string, object?>();

    /// <summary>YAML's untyped graph with string keys: the shape the JSON loaders produce for metadata.</summary>
    private static object? ToPlain(object? value) => value switch
    {
        Dictionary<object, object?> map => map.ToDictionary(kv => KeyName(kv.Key), kv => ToPlain(kv.Value)),
        List<object?> list => list.Select(ToPlain).ToList(),
        _ => value,
    };
}
