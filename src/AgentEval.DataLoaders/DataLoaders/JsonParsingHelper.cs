// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using AgentEval.Models;

namespace AgentEval.DataLoaders;

/// <summary>
/// Shared helper methods for JSON parsing across data loaders.
/// Extracts common parsing logic to avoid duplication.
/// </summary>
internal static class JsonParsingHelper
{
    /// <summary>
    /// Checks if a property name, in any spelling (<see cref="DatasetFieldNames"/>), is a known/standard property.
    /// </summary>
    public static bool IsKnownProperty(string name) => DatasetFieldNames.IsKnown(name);

    /// <summary>
    /// Finds the first property of <paramref name="element"/> whose name is <paramref name="field"/> in any spelling:
    /// <c>expected_output</c>, <c>expectedOutput</c> and <c>ExpectedOutput</c> all match (<see cref="DatasetFieldNames"/>).
    /// </summary>
    public static bool TryGetField(JsonElement element, string field, out JsonElement value)
    {
        var wanted = DatasetFieldNames.Normalize(field);
        foreach (var prop in element.EnumerateObject())
        {
            if (DatasetFieldNames.Normalize(prop.Name) == wanted)
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Gets a string property value or returns the default value.
    /// </summary>
    public static string GetStringOrDefault(JsonElement element, string propertyName, string defaultValue)
    {
        return TryGetField(element, propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? defaultValue
            : defaultValue;
    }

    /// <summary>
    /// Gets a string property value or returns null.
    /// </summary>
    public static string? GetStringOrNull(JsonElement element, string propertyName)
    {
        return TryGetField(element, propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    /// <summary>
    /// Reads one test case from a JSON object, the same way for <c>.json</c> and <c>.jsonl</c>. Field names match in
    /// any spelling (<see cref="DatasetFieldNames"/>); any other property is kept in <see cref="DatasetTestCase.Metadata"/>.
    /// </summary>
    /// <param name="element">A JSON object.</param>
    /// <param name="defaultId">The id to use when the object has none.</param>
    /// <exception cref="InvalidDataException">
    /// Two properties name the same field in different spellings, a single-value field holds a list or object, a list
    /// holds an object, or <c>passing_score</c> is not a whole number.
    /// </exception>
    public static DatasetTestCase ParseTestCase(JsonElement element, string defaultId)
    {
        // The test-case fields by normalized name, read once. Two spellings of one field are an error: one of them
        // would be lost, and which one would depend on the order of the keys.
        var fields = new Dictionary<string, JsonProperty>(StringComparer.Ordinal);
        var testCase = new DatasetTestCase();
        foreach (var prop in element.EnumerateObject())
        {
            var name = DatasetFieldNames.Normalize(prop.Name);
            if (!DatasetFieldNames.KnownFields.Contains(name))
            {
                testCase.Metadata[prop.Name] = GetJsonValue(prop.Value);
            }
            else if (!fields.TryAdd(name, prop))
            {
                throw new InvalidDataException(
                    $"'{fields[name].Name}' and '{prop.Name}' are the same field in two spellings; keep one.");
            }
        }

        JsonProperty? Field(params string[] names)
        {
            foreach (var n in names)
            {
                if (fields.TryGetValue(DatasetFieldNames.Normalize(n), out var prop) && prop.Value.ValueKind != JsonValueKind.Null)
                {
                    return prop;
                }
            }
            return null;
        }

        string? Scalar(params string[] names) => Field(names) is { } prop ? ScalarText(prop) : null;
        IReadOnlyList<string>? Strings(params string[] names) => Field(names) is { } prop ? StringList(prop) : null;

        testCase.Id = Scalar("id") ?? defaultId;
        testCase.Category = Scalar("category");
        testCase.Input = Scalar("input", "question", "prompt", "query") ?? "";
        testCase.ExpectedOutput = Scalar("expected", "expected_output", "answer", "response");
        testCase.Context = Strings("context", "contexts", "documents");
        testCase.ExpectedTools = Strings("expected_tools", "tools");
        testCase.EvaluationCriteria = Strings("evaluation_criteria");
        testCase.Tags = Strings("tags");

        if (Field("passing_score") is { } score)
        {
            testCase.PassingScore = score.Value.ValueKind switch
            {
                JsonValueKind.Number when score.Value.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(score.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => throw new InvalidDataException($"'{score.Name}' must be a whole number, not {score.Value.GetRawText()}."),
            };
        }

        if (Field("ground_truth") is { } groundTruth)
        {
            if (groundTruth.Value.ValueKind == JsonValueKind.Object)
            {
                testCase.GroundTruth = ParseGroundTruth(groundTruth.Value);
            }
            else
            {
                // The ground-truth field is an expected TOOL CALL ({ "name", "arguments" }). Text here is a reference
                // answer by another name (`dataset init` wrote one through 0.43); keep it as metadata, as before.
                testCase.Metadata[groundTruth.Name] = GetJsonValue(groundTruth.Value);
            }
        }
        else if (Field("function") is { } function && Field("arguments") is { } arguments)
        {
            // BFCL style: { "function": "name", "arguments": {...} }
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = ScalarText(function),
                Arguments = ParseArguments(arguments.Value),
            };
        }

        return testCase;
    }

    /// <summary>A single-value field as text: a string as is, a number or boolean as written.</summary>
    private static string ScalarText(JsonProperty prop) => prop.Value.ValueKind switch
    {
        JsonValueKind.String => prop.Value.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
        _ => throw new InvalidDataException($"'{prop.Name}' must be a single value, not {prop.Value.ValueKind}."),
    };

    /// <summary>A list field: an array of single values (nulls skipped), or one value as a one-item list.</summary>
    private static IReadOnlyList<string> StringList(JsonProperty prop) => prop.Value.ValueKind switch
    {
        JsonValueKind.Array => prop.Value.EnumerateArray()
            .Where(item => item.ValueKind != JsonValueKind.Null)
            .Select(item => item.ValueKind switch
            {
                JsonValueKind.String => item.GetString() ?? "",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => item.GetRawText(),
                _ => throw new InvalidDataException($"'{prop.Name}' must be a list of single values, not of {item.ValueKind}."),
            })
            .ToList(),
        JsonValueKind.Object => throw new InvalidDataException($"'{prop.Name}' must be a value or a list of values, not an object."),
        _ => [ScalarText(prop)],
    };

    /// <summary>
    /// Parses a JSON element that can be either a string or an array of strings.
    /// </summary>
    public static IReadOnlyList<string>? ParseStringArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? string.Empty)
                .ToList();
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            return new[] { element.GetString() ?? string.Empty };
        }
        return null;
    }

    /// <summary>
    /// Parses ground truth tool call information from JSON.
    /// </summary>
    public static GroundTruthToolCall? ParseGroundTruth(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = GetStringOrDefault(element, "name", GetStringOrDefault(element, "function", ""));
        var args = TryGetField(element, "arguments", out var argsProp)
            ? ParseArguments(argsProp)
            : new Dictionary<string, object?>();

        return new GroundTruthToolCall { Name = name, Arguments = args };
    }

    /// <summary>
    /// Parses arguments dictionary from JSON object.
    /// </summary>
    public static Dictionary<string, object?> ParseArguments(JsonElement element)
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

    /// <summary>
    /// Converts a JSON element to a CLR object representation.
    /// </summary>
    public static object? GetJsonValue(JsonElement element) => element.ValueKind switch
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

    /// <summary>
    /// Gets the input string from multiple possible property names.
    /// </summary>
    public static string GetInput(JsonElement element)
    {
        return GetStringOrDefault(element, "input",
            GetStringOrDefault(element, "question",
            GetStringOrDefault(element, "prompt",
            GetStringOrDefault(element, "query", ""))));
    }

    /// <summary>
    /// Gets the expected output string from multiple possible property names.
    /// </summary>
    public static string? GetExpectedOutput(JsonElement element)
    {
        return GetStringOrNull(element, "expected")
            ?? GetStringOrNull(element, "expected_output")
            ?? GetStringOrNull(element, "answer")
            ?? GetStringOrNull(element, "response");
    }
}
