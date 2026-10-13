// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace AgentEval.Results.Observations;

/// <summary>Bounded OTLP/JSON trace decoding. Unknown message fields have no semantic effect.</summary>
public sealed class OtlpJsonTraceDecoder : IOtlpTraceDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly OtlpIngestionLimits _limits;
    /// <summary>Creates a decoder with validated immutable budgets.</summary>
    public OtlpJsonTraceDecoder(OtlpIngestionLimits? limits = null)
    {
        _limits = limits ?? new();
        _limits.Validate();
    }

    /// <inheritdoc />
    public OtlpTraceBatch Decode(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (payload.Length > _limits.MaxDecodedBytes) throw Limit("decoded byte budget exceeded");
        try
        {
            // Validate even ignored unknown fields; JsonDocument defers decoding some string tokens.
            _ = StrictUtf8.GetCharCount(payload.Span);
            using var document = JsonDocument.Parse(payload, new() { MaxDepth = _limits.MaxDepth });
            var reader = new Reader(_limits, cancellationToken);
            reader.CheckStructure(document.RootElement);
            return reader.Batch(document.RootElement);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { throw Malformed(); }
    }

    private static OtlpIngestionException Malformed() => new("malformed", "invalid OTLP JSON message");
    private static OtlpIngestionException Limit(string reason) => new("limit", reason);

    private sealed class Reader(OtlpIngestionLimits limits, CancellationToken cancellationToken)
    {
        private int _items;
        private int _spans;

        // Unknown fields are ignored semantically, but still have bounded JSON allocation and traversal.
        internal void CheckStructure(JsonElement element)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_items > limits.MaxItems) throw Limit("aggregate item budget exceeded");
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject()) CheckStructure(property.Value);
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) CheckStructure(child);
        }

        internal OtlpTraceBatch Batch(JsonElement value)
        {
            Object(value);
            return new(List(value, "resourceSpans", ResourceSpans));
        }

        private OtlpResourceSpans ResourceSpans(JsonElement value)
        {
            Object(value);
            var resource = Field(value, "resource");
            return new(Absent(resource) ? null : Resource(resource), Text(value, "schemaUrl"), List(value, "scopeSpans", ScopeSpans));
        }

        private OtlpResource Resource(JsonElement value)
        {
            Object(value);
            return new(Attributes(value), UInt32(value, "droppedAttributesCount"), List(value, "entityRefs", EntityRef));
        }

        private OtlpEntityRef EntityRef(JsonElement value)
        {
            Object(value);
            return new(Text(value, "schemaUrl"), Text(value, "type"), List(value, "idKeys", String), List(value, "descriptionKeys", String));
        }

        private OtlpScopeSpans ScopeSpans(JsonElement value)
        {
            Object(value);
            var scope = Field(value, "scope");
            return new(Absent(scope) ? null : Scope(scope), Text(value, "schemaUrl"), List(value, "spans", Span));
        }

        private OtlpScope Scope(JsonElement value)
        {
            Object(value);
            return new(Text(value, "name"), Text(value, "version"), Attributes(value), UInt32(value, "droppedAttributesCount"));
        }

        private OtlpSpan Span(JsonElement value)
        {
            Object(value);
            if (++_spans > limits.MaxSpans) throw Limit("span budget exceeded");
            var status = Field(value, "status");
            return new(Id(value, "traceId", 32), Id(value, "spanId", 16), Id(value, "parentSpanId", 16, optional: true),
                Text(value, "traceState"), Text(value, "name"), Enum(value, "kind"),
                UInt64(value, "startTimeUnixNano"), UInt64(value, "endTimeUnixNano"), UInt32(value, "flags"),
                Attributes(value), UInt32(value, "droppedAttributesCount"), List(value, "events", Event),
                UInt32(value, "droppedEventsCount"), List(value, "links", Link), UInt32(value, "droppedLinksCount"),
                Absent(status) ? null : Status(status));
        }

        private OtlpSpanEvent Event(JsonElement value)
        {
            Object(value);
            return new(UInt64(value, "timeUnixNano"), Text(value, "name"), Attributes(value), UInt32(value, "droppedAttributesCount"));
        }

        private OtlpSpanLink Link(JsonElement value)
        {
            Object(value);
            return new(Id(value, "traceId", 32), Id(value, "spanId", 16), Text(value, "traceState"),
                UInt32(value, "flags"), Attributes(value), UInt32(value, "droppedAttributesCount"));
        }

        private OtlpSpanStatus Status(JsonElement value)
        {
            Object(value);
            return new(Enum(value, "code"), Text(value, "message"));
        }

        private ImmutableArray<OtlpAttribute> Attributes(JsonElement value, string field = "attributes")
        {
            var attributes = List(value, field, Attribute, limits.MaxAttributes);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attribute in attributes)
                if (!keys.Add(attribute.Key)) throw Malformed();
            return attributes;
        }

        private OtlpAttribute Attribute(JsonElement value)
        {
            Object(value);
            return new(Text(value, "key"), Value(Field(value, "value")));
        }

        private OtlpValue Value(JsonElement value)
        {
            if (Absent(value)) return new OtlpValue.Empty();
            Object(value);
            string? selected = null;
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name is not ("stringValue" or "boolValue" or "intValue" or "doubleValue" or "arrayValue" or "kvlistValue" or "bytesValue")
                    || Absent(property.Value)) continue;
                if (selected is not null) throw Malformed();
                selected = property.Name;
            }
            if (selected is null) return new OtlpValue.Empty();
            var item = Field(value, selected);
            return selected switch
            {
                "stringValue" => new OtlpValue.Text(String(item)),
                "boolValue" => new OtlpValue.Boolean(item.GetBoolean()),
                "intValue" => new OtlpValue.Integer((long)Integer(item, long.MinValue, long.MaxValue)),
                "doubleValue" => new OtlpValue.Real(Double(item)),
                "arrayValue" => Array(item),
                "kvlistValue" => KeyValues(item),
                "bytesValue" => Bytes(item),
                _ => throw Malformed()
            };
        }

        private OtlpValue Array(JsonElement value)
        {
            Object(value);
            return new OtlpValue.Array(List(value, "values", Value));
        }
        private OtlpValue KeyValues(JsonElement value)
        {
            Object(value);
            return new OtlpValue.KeyValues(Attributes(value, "values"));
        }
        private OtlpValue Bytes(JsonElement value)
        {
            var encoded = value.GetString() ?? throw Malformed();
            if (encoded.Length > ((limits.MaxValueBytes + 2L) / 3 * 4)) throw Limit("bytes value budget exceeded");
            // ProtoJSON accepts the standard and URL-safe alphabets, with or without padding.
            if (encoded.Any(char.IsWhiteSpace)) throw Malformed();
            encoded = encoded.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > limits.MaxValueBytes) throw Limit("bytes value budget exceeded");
            return new OtlpValue.Bytes(ImmutableArray.CreateRange(bytes));
        }

        private static double Double(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number)
            {
                var result = value.GetDouble();
                if (!double.IsFinite(result)) throw Malformed();
                return result;
            }
            var text = value.GetString() ?? throw Malformed();
            if (text.Length > 128) throw Limit("double representation budget exceeded");
            return text switch
            {
                "NaN" => double.NaN, "Infinity" => double.PositiveInfinity, "-Infinity" => double.NegativeInfinity,
                _ when !text.Any(char.IsWhiteSpace) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    && double.IsFinite(number) => number,
                _ => throw Malformed()
            };
        }

        private ImmutableArray<T> List<T>(JsonElement owner, string field, Func<JsonElement, T> read, int? maximum = null)
        {
            var value = Field(owner, field);
            if (Absent(value)) return [];
            if (value.ValueKind != JsonValueKind.Array) throw Malformed();
            if (value.GetArrayLength() > (maximum ?? limits.MaxItems)) throw Limit("collection budget exceeded");
            var items = ImmutableArray.CreateBuilder<T>(value.GetArrayLength());
            foreach (var item in value.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Absent(item)) throw Malformed();
                items.Add(read(item));
            }
            return items.MoveToImmutable();
        }

        private string Text(JsonElement value, string field)
        {
            var item = Field(value, field);
            return Absent(item) ? "" : String(item);
        }
        private string String(JsonElement value)
        {
            var text = value.GetString() ?? throw Malformed();
            if (Encoding.UTF8.GetByteCount(text) > limits.MaxValueBytes) throw Limit("string value budget exceeded");
            return text;
        }
        private string Id(JsonElement value, string field, int length, bool optional = false)
        {
            var text = Text(value, field);
            if (optional && text.Length == 0) return "";
            if (text.Length != length || !text.All(char.IsAsciiHexDigit) || text.All(c => c == '0')) throw Malformed();
            return text.ToLowerInvariant();
        }
        private static JsonElement Field(JsonElement value, string field) => value.TryGetProperty(field, out var item) ? item : default;
        private static bool Absent(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
        private static void Object(JsonElement value) { if (value.ValueKind != JsonValueKind.Object) throw Malformed(); }
        private static uint UInt32(JsonElement value, string field) => (uint)Integer(Field(value, field), 0, uint.MaxValue);
        private static ulong UInt64(JsonElement value, string field) => (ulong)Integer(Field(value, field), 0, ulong.MaxValue);
        private static int Enum(JsonElement value, string field)
        {
            var item = Field(value, field);
            if (!Absent(item) && item.ValueKind != JsonValueKind.Number) throw Malformed();
            return (int)Integer(item, int.MinValue, int.MaxValue);
        }

        // Parse integral exponent notation exactly, never through binary64 or a rounding decimal conversion.
        private static BigInteger Integer(JsonElement value, BigInteger minimum, BigInteger maximum)
        {
            if (Absent(value)) return BigInteger.Zero;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            if (text.Length > 128) throw Limit("integer representation budget exceeded");
            if (text.Length == 0) throw Malformed();
            var negative = text[0] == '-';
            if (text[0] is '-' or '+') text = text[1..];
            var parts = text.Split(['e', 'E']);
            if (parts.Length > 2) throw Malformed();
            var exponent = parts.Length == 2 ? int.Parse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;
            var mantissa = parts[0].Split('.');
            if (mantissa.Length > 2 || mantissa.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit))) throw Malformed();
            var digits = string.Concat(mantissa).TrimStart('0');
            var scale = (long)exponent - (mantissa.Length == 2 ? mantissa[1].Length : 0);
            if (digits.Length == 0) return BigInteger.Zero;
            if (scale < 0)
            {
                if (-scale >= digits.Length || digits.AsSpan(digits.Length + (int)scale).ContainsAnyExcept('0')) throw Malformed();
                digits = digits[..(digits.Length + (int)scale)];
                scale = 0;
            }
            if (scale > 20 || digits.Length + scale > 20) throw Malformed();
            var number = BigInteger.Parse(digits, CultureInfo.InvariantCulture) * BigInteger.Pow(10, (int)scale);
            if (negative) number = -number;
            if (number < minimum || number > maximum) throw Malformed();
            return number;
        }
    }
}
