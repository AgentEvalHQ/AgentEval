// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.Interop.AssertAi;

/// <summary>
/// Reading and writing ASSERT's files the way ASSERT does. ASSERT writes one <c>json.dumps(row, ensure_ascii=False)</c>
/// per line, UTF-8 without a BOM, and opens files in text mode, so a file written on Windows has CRLF line endings.
/// Python's JSON also writes the bare tokens <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c>, which are not JSON.
/// </summary>
internal static class AssertAiJson
{
    /// <summary>Output that matches ASSERT's: non-ASCII written as is, one object per line, LF.</summary>
    public static readonly JsonSerializerOptions Write = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions Indented = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    /// <summary>Reads a JSON Lines file. Blank lines are skipped, as ASSERT skips them; a line that is not a JSON
    /// object is an error naming the file and line, never a silently dropped row.</summary>
    public static IReadOnlyList<(int Line, JsonObject Row)> ReadJsonLines(string path)
    {
        var rows = new List<(int, JsonObject)>();
        var lineNumber = 0;
        foreach (var raw in ReadText(path).Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var node = ParsePython(line, path, lineNumber);
            if (node is not JsonObject row)
            {
                throw new InvalidDataException($"{path}:{lineNumber}: expected a JSON object, found {Describe(node)}.");
            }

            rows.Add((lineNumber, row));
        }

        return rows;
    }

    /// <summary>Reads a JSON file whose top level must be an object.</summary>
    public static JsonObject ReadObject(string path)
    {
        var node = ParsePython(ReadText(path), path, null);
        return node as JsonObject
            ?? throw new InvalidDataException($"{path}: expected a JSON object, found {Describe(node)}.");
    }

    /// <summary>Writes rows as JSON Lines: UTF-8 without a BOM, LF after every row.</summary>
    public static void WriteJsonLines(string path, IEnumerable<JsonNode> rows)
    {
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            text.Append(row.ToJsonString(Write)).Append('\n');
        }

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Writes a JSON file indented, without a trailing newline, as ASSERT writes <c>taxonomy.json</c>.</summary>
    public static void WriteObject(string path, JsonNode node) =>
        File.WriteAllText(path, node.ToJsonString(Indented), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    /// <summary>
    /// Parses JSON as Python's <c>json.loads</c> accepts it. When strict JSON fails, the bare tokens <c>NaN</c>,
    /// <c>Infinity</c> and <c>-Infinity</c> outside strings are read as <c>null</c> (no finite number stands for them);
    /// any other error is reported with the file and line.
    /// </summary>
    internal static JsonNode? ParsePython(string text, string source, int? line)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException strict)
        {
            var (replaced, count) = ReplaceNonFiniteTokens(text);
            if (count > 0)
            {
                try
                {
                    return JsonNode.Parse(replaced);
                }
                catch (JsonException)
                {
                    // Fall through to the original error, which points at the real problem.
                }
            }

            var where = line is { } l ? $"{source}:{l}" : source;
            throw new InvalidDataException($"{where}: not valid JSON ({strict.Message})", strict);
        }
    }

    /// <summary>Replaces Python's non-finite number tokens outside strings with <c>null</c>.</summary>
    internal static (string Text, int Count) ReplaceNonFiniteTokens(string text)
    {
        var output = new StringBuilder(text.Length);
        var count = 0;
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                output.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    output.Append(text[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                output.Append(c);
                continue;
            }

            var token = MatchToken(text, i);
            if (token > 0)
            {
                output.Append("null");
                i += token - 1;
                count++;
                continue;
            }

            output.Append(c);
        }

        return (output.ToString(), count);
    }

    private static int MatchToken(string text, int i)
    {
        foreach (var token in new[] { "-Infinity", "Infinity", "NaN" })
        {
            if (string.CompareOrdinal(text, i, token, 0, token.Length) == 0
                && (i + token.Length == text.Length || !char.IsLetterOrDigit(text[i + token.Length]))
                && (i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                return token.Length;
            }
        }

        return 0;
    }

    private static string ReadText(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }

    private static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonArray => "an array",
        JsonValue v => $"a {v.GetValueKind().ToString().ToLowerInvariant()}",
        _ => node.GetType().Name,
    };

    // ---- Python-like reads of a JsonNode -----------------------------------------------------------------------

    /// <summary>The value when it is a JSON string (Python <c>isinstance(x, str)</c>).</summary>
    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>The value when it is a JSON boolean (Python <c>isinstance(x, bool)</c>).</summary>
    public static bool? Bool(JsonNode? node) => node is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    } : null;

    /// <summary>The value when it is a JSON integer, written without a fraction or exponent (Python <c>int</c>).</summary>
    public static long? Int(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }

        var text = v.ToJsonString();
        return text.IndexOfAny(['.', 'e', 'E']) < 0 && long.TryParse(text, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    /// <summary>Python truthiness of a string field: present, a string, and not empty.</summary>
    public static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.True => true,
            JsonValueKind.Number => v.ToJsonString() is not ("0" or "0.0" or "-0" or "-0.0"),
            _ => false,
        },
        JsonArray a => a.Count > 0,
        JsonObject o => o.Count > 0,
        _ => false,
    };
}
