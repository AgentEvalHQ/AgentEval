// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Microsoft365.Diagnostics;

/// <summary>
/// The JSON conventions of the JSONL sinks (camelCase, enums as strings, nulls omitted so a missing fact is
/// absent rather than <c>null</c>) and the line bound: a line longer than <see cref="MaxLineLength"/> has its
/// <c>data</c> replaced by <see cref="TruncationMarker"/> and then its <c>message</c> cut with the marker appended,
/// so every line stays parseable.
/// </summary>
internal static class WorkIqDiagnosticsJson
{
    /// <summary>Upper bound of one JSONL line in UTF-16 characters (64 KiB).</summary>
    public const int MaxLineLength = 64 * 1024;

    /// <summary>Appended where a line was cut.</summary>
    public const string TruncationMarker = "…[truncated]";

    /// <summary>camelCase, enums as strings, nulls omitted, no indentation.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    /// <summary>The UTF-8 encoding of the files (no BOM).</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Serializes a trace event to one bounded line.</summary>
    public static string SerializeLine(WorkIqTraceEvent e)
    {
        Func<WorkIqTraceEvent, WorkIqTraceEvent>? withoutData = e.Data is null
            ? null
            : static value => value with { Data = JsonSerializer.SerializeToElement(TruncationMarker) };
        return SerializeBounded(e, e.Message, withMessage: static (value, message) => value with { Message = message }, withoutData);
    }

    /// <summary>Serializes an issue to one bounded line.</summary>
    public static string SerializeLine(WorkIqIssue i) =>
        SerializeBounded(i, i.Message, withMessage: (value, message) => value with { Message = message }, withoutData: null);

    private static string SerializeBounded<T>(T value, string message, Func<T, string, T> withMessage, Func<T, T>? withoutData)
    {
        var json = JsonSerializer.Serialize(value, Options);
        if (json.Length <= MaxLineLength)
        {
            return json;
        }

        if (withoutData is not null)
        {
            value = withoutData(value);
            json = JsonSerializer.Serialize(value, Options);
            if (json.Length <= MaxLineLength)
            {
                return json;
            }
        }

        var keep = message.Length;
        for (var attempt = 0; attempt < 32 && json.Length > MaxLineLength && keep > 0; attempt++)
        {
            var overflow = json.Length - MaxLineLength;
            keep = Math.Max(0, keep - overflow - TruncationMarker.Length);
            if (keep > 0 && char.IsHighSurrogate(message[keep - 1]))
            {
                keep--;
            }

            value = withMessage(value, message[..keep] + TruncationMarker);
            json = JsonSerializer.Serialize(value, Options);
        }

        return json;
    }
}
