// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.Output;

/// <summary>
/// Projects a JSON document into its canonical byte representation for hashing.
/// Plan-13 T1.7 (v1.1): closes the field-edit tamper gap in the audit chain.
/// </summary>
/// <remarks>
/// <para>
/// Pre-v1.1 <see cref="ContentHasher"/> hashed scenario / summary / trace files as RAW bytes.
/// That meant a whitespace-only edit or a property-reorder by a JSON-formatting tool broke
/// the hash — operators would re-run benchmarks "because the hash failed" when nothing
/// semantically changed. Conversely, an attacker who could replicate the exact byte layout
/// could edit values without detection (the chain only knew "raw bytes match the stored
/// hash"; it never re-checked semantic equivalence).
/// </para>
/// <para>
/// This projector implements a minimal subset of RFC 8785 / JCS (JSON Canonicalization
/// Scheme): alphabetic property order, no insignificant whitespace, deterministic number
/// encoding. Same input semantics → same canonical bytes regardless of original whitespace
/// or property order; different semantics → different canonical bytes (caught at hash check).
/// </para>
/// <para>
/// <b>BREAKING</b>: workspaces written under v0.8.1-beta (pre-v1.1) have manifest
/// <c>contentHash</c> values computed against raw bytes. Under v1.1 they'll fail
/// <see cref="ContentHasher.VerifyAsync"/>. <c>agenteval doctor</c> surfaces the mismatch
/// with a friendly message pointing the operator at <c>agenteval bench …</c> to regenerate.
/// </para>
/// </remarks>
public static class CanonicalJsonProjector
{
    /// <summary>
    /// Reads <paramref name="filePath"/>, parses it as JSON, and returns its canonical
    /// byte representation. Returns the raw file bytes when the file is not valid JSON
    /// (so non-JSON artefacts stay covered by the hash without breaking it).
    /// </summary>
    public static async Task<byte[]> ProjectFileAsync(string filePath, CancellationToken ct = default)
    {
        var raw = await File.ReadAllBytesAsync(filePath, ct);
        try
        {
            var node = JsonNode.Parse(raw);
            if (node is null) return raw;
            return ProjectNode(node);
        }
        catch (JsonException)
        {
            // Not valid JSON — hash the raw bytes as-is. This keeps trace artefacts
            // and other not-strictly-JSON files inside the audit envelope.
            return raw;
        }
    }

    /// <summary>
    /// Returns the canonical UTF-8 representation of <paramref name="node"/>.
    /// Alphabetic property order, no insignificant whitespace, deterministic number encoding.
    /// </summary>
    public static byte[] ProjectNode(JsonNode? node)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false }))
        {
            WriteCanonical(writer, node);
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Reads <paramref name="json"/> as a JsonNode tree and returns its canonical UTF-8
    /// representation. Useful for testing + for one-shot in-memory projection from a string.
    /// </summary>
    public static byte[] ProjectString(string json)
    {
        var node = JsonNode.Parse(json);
        return ProjectNode(node);
    }

    private static void WriteCanonical(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case null:
                w.WriteNullValue();
                return;

            case JsonObject obj:
                w.WriteStartObject();
                // Alphabetic property ordering — the load-bearing transform.
                foreach (var kvp in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(kvp.Key);
                    WriteCanonical(w, kvp.Value);
                }
                w.WriteEndObject();
                return;

            case JsonArray arr:
                w.WriteStartArray();
                foreach (var item in arr)
                    WriteCanonical(w, item);
                w.WriteEndArray();
                return;

            case JsonValue val:
                WriteCanonicalValue(w, val);
                return;

            default:
                throw new InvalidOperationException($"Unknown JsonNode kind: {node.GetType().FullName}");
        }
    }

    private static void WriteCanonicalValue(Utf8JsonWriter w, JsonValue val)
    {
        // GetValue<JsonElement>() returns the underlying primitive in a kind-typed shape;
        // we then re-emit through the writer's appropriate primitive method so the byte
        // form is deterministic (e.g., booleans → `true`/`false`, integer numbers as
        // bare digits without trailing `.0`).
        var element = val.GetValue<JsonElement>();
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                w.WriteStringValue(element.GetString());
                return;
            case JsonValueKind.Number:
                // Canonical number form: integer → no decimal point; double → "R" round-trip
                // format. Avoids `1.0` / `1` divergence breaking the hash.
                if (element.TryGetInt64(out var i))
                    w.WriteNumberValue(i);
                else
                    w.WriteRawValue(element.GetDouble().ToString("R", CultureInfo.InvariantCulture), skipInputValidation: true);
                return;
            case JsonValueKind.True:
                w.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                w.WriteBooleanValue(false);
                return;
            case JsonValueKind.Null:
                w.WriteNullValue();
                return;
            default:
                throw new InvalidOperationException($"Unexpected primitive value kind: {element.ValueKind}");
        }
    }
}
