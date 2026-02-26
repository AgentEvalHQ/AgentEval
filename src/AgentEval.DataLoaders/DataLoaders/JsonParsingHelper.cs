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
    private static readonly string[] KnownPropertyNames = new[]
    {
        "id", "category",
        "input", "question", "prompt", "query",
        "expected", "expected_output", "answer", "response",
        "context", "contexts", "documents",
        "expected_tools", "tools",
        "ground_truth", "function", "arguments",
        "evaluation_criteria", "tags", "passing_score"
    };

    /// <summary>
    /// Checks if a property name is a known/standard property.
    /// </summary>
    public static bool IsKnownProperty(string name)
    {
        return Array.Exists(KnownPropertyNames, p => 
            string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a string property value or returns the default value.
    /// </summary>
    public static string GetStringOrDefault(JsonElement element, string propertyName, string defaultValue)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? defaultValue
            : defaultValue;
    }

    /// <summary>
    /// Gets a string property value or returns null.
    /// </summary>
    public static string? GetStringOrNull(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
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
        var args = element.TryGetProperty("arguments", out var argsProp)
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
