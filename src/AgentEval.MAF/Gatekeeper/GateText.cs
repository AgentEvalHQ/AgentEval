// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Renders a tool-call argument value or tool result (an <see cref="object"/> that may be a string, number,
/// <see cref="JsonElement"/>, or a complex object) to a stable, culture-invariant string for the deterministic
/// gates to scan. Shared by the referential-integrity and taint gates so their text extraction is identical.
/// </summary>
internal static class GateText
{
    // Relaxed (non-HTML-escaping) encoder so a serialized secret is rendered with the SAME bytes that flow to a
    // string sink — the default encoder escapes < > & ' and non-ASCII to \uXXXX, which would make a tainted token
    // fail to substring-match the raw value the model actually sends (a silent taint miss).
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Stringify(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() ?? string.Empty : je.GetRawText(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => SafeJson(value),
    };

    private static string SafeJson(object value)
    {
        try
        {
            return JsonSerializer.Serialize(value, SerializerOptions);
        }
        // A defensive renderer must never throw into the gate: NotSupportedException (unsupported type) and
        // JsonException (a reference cycle or >64-deep graph — a realistic ORM/entity result) both degrade to
        // ToString() rather than propagating and failing the tool call closed.
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            return value.ToString() ?? string.Empty;
        }
    }
}
