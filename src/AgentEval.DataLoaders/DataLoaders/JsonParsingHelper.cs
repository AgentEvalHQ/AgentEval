// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
    public static DatasetTestCase ParseTestCase(JsonElement element, string defaultId)
    {
        var testCase = new DatasetTestCase
        {
            Id = GetStringOrDefault(element, "id", defaultId),
            Category = GetStringOrNull(element, "category"),
            Input = GetInput(element),
            ExpectedOutput = GetExpectedOutput(element),
        };

        if (TryGetField(element, "context", out var context)
            || TryGetField(element, "contexts", out context)
            || TryGetField(element, "documents", out context))
        {
            testCase.Context = ParseStringArray(context);
        }

        if (TryGetField(element, "expected_tools", out var tools) || TryGetField(element, "tools", out tools))
        {
            testCase.ExpectedTools = ParseStringArray(tools);
        }

        if (TryGetField(element, "ground_truth", out var groundTruth))
        {
            testCase.GroundTruth = ParseGroundTruth(groundTruth);
        }
        else if (TryGetField(element, "function", out var function) && TryGetField(element, "arguments", out var arguments))
        {
            // BFCL style: { "function": "name", "arguments": {...} }
            testCase.GroundTruth = new GroundTruthToolCall
            {
                Name = function.ValueKind == JsonValueKind.String ? function.GetString() ?? "" : "",
                Arguments = ParseArguments(arguments),
            };
        }

        if (TryGetField(element, "evaluation_criteria", out var criteria))
        {
            testCase.EvaluationCriteria = ParseStringArray(criteria);
        }

        if (TryGetField(element, "tags", out var tags))
        {
            testCase.Tags = ParseStringArray(tags);
        }

        if (TryGetField(element, "passing_score", out var score) && score.ValueKind == JsonValueKind.Number)
        {
            testCase.PassingScore = score.GetInt32();
        }

        foreach (var prop in element.EnumerateObject())
        {
            if (!IsKnownProperty(prop.Name))
            {
                testCase.Metadata[prop.Name] = GetJsonValue(prop.Value);
            }
        }

        return testCase;
    }

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
