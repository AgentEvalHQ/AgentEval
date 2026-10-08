// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Runs;

/// <summary>
/// Reading values out of documents already read: each accessor gives the value only when it has the JSON type asked
/// for, and null otherwise, so a rule never throws on a document a schema refused. Numbers are binary64 ([ENC-4]).
/// </summary>
internal static class AefNode
{
    /// <summary>The member <paramref name="name"/> of <paramref name="node"/> when it is an object, else null.</summary>
    public static JsonNode? Get(JsonNode? node, string name) =>
        node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value : null;

    /// <summary>Whether <paramref name="node"/> is an object holding a member <paramref name="name"/> (whatever its value, null included).</summary>
    public static bool Has(JsonNode? node, string name) => node is JsonObject obj && obj.ContainsKey(name);

    /// <summary>The nested member at the path of names, or null.</summary>
    public static JsonNode? At(JsonNode? node, params string[] names)
    {
        foreach (var name in names)
        {
            node = Get(node, name);
        }

        return node;
    }

    /// <summary>A string value, or null.</summary>
    public static string? String(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A number as binary64, or null. A value built in code (an int, a decimal) is read through its JSON text.</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        return v.TryGetValue<double>(out var number)
            ? number
            : double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Whether the value is the JSON literal <c>true</c>.</summary>
    public static bool IsTrue(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    /// <summary>The items of an array, or none.</summary>
    public static IEnumerable<JsonNode?> Items(JsonNode? node) => node as JsonArray ?? (IEnumerable<JsonNode?>)[];

    /// <summary>The object items of an array, or none.</summary>
    public static IEnumerable<JsonObject> Objects(JsonNode? node) => Items(node).OfType<JsonObject>();

    /// <summary>The string items of an array, or none.</summary>
    public static IEnumerable<string> Strings(JsonNode? node) => Items(node).Select(String).OfType<string>();

    /// <summary>An AEF time ([ENC-8]), or null when the value is not one.</summary>
    public static AefTime? Time(JsonNode? node)
    {
        if (String(node) is not { } text)
        {
            return null;
        }

        try
        {
            return AefTime.Parse(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
