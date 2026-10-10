// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Adapters.Inspect;

/// <summary>
/// JSON as an Inspect eval log holds it (contracts/aef/1/interop/inspect.md, "The target"): a score's unscored value is
/// the bare token <c>NaN</c>, which is not JSON, and Inspect (Python) writes its logs with <c>json.dumps</c>. In a tree
/// read or built here, <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> are held as marker strings
/// (<see cref="NaN"/>), which no Inspect text holds (they start with U+0000), and written back as the bare tokens.
/// </summary>
internal static class InspectJson
{
    /// <summary>The marker of Inspect's NaN in a tree.</summary>
    public const string NaNMarker = "\u0000NaN\u0000";

    private const string InfinityMarker = "\u0000Infinity\u0000";
    private const string MinusInfinityMarker = "\u0000-Infinity\u0000";

    /// <summary>A fresh NaN value.</summary>
    public static JsonValue NaN() => JsonValue.Create(NaNMarker);

    /// <summary>Whether <paramref name="node"/> is Inspect's NaN.</summary>
    public static bool IsNaN(JsonNode? node) => Marker(node) == NaNMarker;

    /// <summary>Whether <paramref name="node"/> is a non-finite number (NaN or an infinity).</summary>
    public static bool IsNonFinite(JsonNode? node) => Marker(node) is not null;

    /// <summary>Whether <paramref name="node"/> holds a non-finite number anywhere in it.</summary>
    public static bool HoldsNonFinite(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(m => HoldsNonFinite(m.Value)),
        JsonArray array => array.Any(HoldsNonFinite),
        _ => IsNonFinite(node),
    };

    /// <summary>
    /// Reads an Inspect log: JSON in UTF-8 (a byte-order mark is skipped), where <c>NaN</c>, <c>Infinity</c> and
    /// <c>-Infinity</c> may stand where a number does.
    /// </summary>
    /// <exception cref="InvalidDataException">Not such a text.</exception>
    public static JsonNode? Parse(byte[] utf8)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        var start = utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF ? 3 : 0;
        var text = new List<byte>(utf8.Length + 64);
        var inString = false;
        for (var i = start; i < utf8.Length; i++)
        {
            var b = utf8[i];
            if (inString)
            {
                text.Add(b);
                if (b == (byte)'\\' && i + 1 < utf8.Length)
                {
                    text.Add(utf8[++i]);
                }
                else if (b == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            if (b == (byte)'"')
            {
                inString = true;
                text.Add(b);
                continue;
            }

            // Outside a string, letters stand only in true, false, null and these tokens.
            var (marker, length) = Token(utf8, i, "NaN"u8) ? (NaNMarker, 3)
                : Token(utf8, i, "Infinity"u8) ? (InfinityMarker, 8)
                : Token(utf8, i, "-Infinity"u8) ? (MinusInfinityMarker, 9)
                : (null, 0);
            if (marker is not null)
            {
                text.AddRange(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(marker)));
                i += length - 1;   // the loop adds the last one
                continue;
            }

            text.Add(b);
        }

        try
        {
            var node = JsonNode.Parse(text.ToArray(), nodeOptions: null, new JsonDocumentOptions { MaxDepth = 512 });
            Touch(node);   // a member name twice is found here, not later
            return node;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Not an Inspect eval log in JSON form: {e.Message}", e);
        }
    }

    /// <summary>
    /// The text Python's <c>json.dumps(value, indent=2, ensure_ascii=False)</c> writes, as Inspect writes a <c>.json</c>
    /// log: two-space indent, <c>": "</c> between a name and its value, non-ASCII text as UTF-8, numbers as Python spells
    /// them (<see cref="Number"/>), and the markers as the bare tokens.
    /// </summary>
    public static string Indented(JsonNode? value)
    {
        var output = new StringBuilder();
        Write(output, value, indent: 0, indented: true);
        return output.ToString();
    }

    /// <summary>The text Python's <c>json.dumps(value, separators=(",", ":"), ensure_ascii=False)</c> writes, in UTF-8.</summary>
    public static byte[] Compact(JsonNode? value)
    {
        var output = new StringBuilder();
        Write(output, value, indent: 0, indented: false);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    /// <summary>
    /// A number as Python's <c>json</c> writes it: an integer (a JSON number without fraction or exponent) in plain digits,
    /// any other number as Python's <c>repr</c> of the float (the shortest text that reads back as the same binary64 value,
    /// <c>1.0</c>, <c>0.0001</c>, <c>1e-05</c>, <c>1e+16</c>).
    /// </summary>
    public static string Number(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            var text = element.GetRawText();
            return text.AsSpan().IndexOfAny(".eE") < 0
                ? BigInteger.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
                : Repr(double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        // A value built in memory: an integer type stays an integer, a double is a float.
        if (value.TryGetValue<int>(out var n))
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        return value.TryGetValue<double>(out var d) ? Repr(d) : value.ToJsonString();
    }

    /// <summary>Python's <c>repr</c> of a float (its <c>json</c> module writes floats so).</summary>
    public static string Repr(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        // The shortest round-trip digits (.NET Core 3.0 and later), then Python's layout: fixed notation when the decimal
        // point falls from 4 places left of the first digit to 16 right of it, else d.ddde±XX.
        var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOfAny(['E', 'e']);
        var exponent = e < 0 ? 0 : int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? r : r[..e];
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? mantissa : mantissa[..dot];
        var digits = whole + (dot < 0 ? "" : mantissa[(dot + 1)..]);
        var point = whole.Length + exponent;
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits[lead..].TrimEnd('0');
        point -= lead;

        string text;
        if (point is > -4 and <= 16)
        {
            text = point <= 0 ? "0." + new string('0', -point) + digits
                : point >= digits.Length ? digits + new string('0', point - digits.Length) + ".0"
                : digits[..point] + "." + digits[point..];
        }
        else
        {
            var power = point - 1;
            text = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (power < 0 ? "-" : "+")
                   + Math.Abs(power).ToString("00", CultureInfo.InvariantCulture);
        }

        return value < 0 ? "-" + text : text;
    }

    private static string? Marker(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() is NaNMarker or InfinityMarker or MinusInfinityMarker
            ? v.GetValue<string>()
            : null;

    private static bool Token(byte[] utf8, int at, ReadOnlySpan<byte> token) =>
        at + token.Length <= utf8.Length && utf8.AsSpan(at, token.Length).SequenceEqual(token);

    private static void Touch(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj)
                {
                    Touch(value);
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Touch(item);
                }

                break;
        }
    }

    private static void Write(StringBuilder output, JsonNode? node, int indent, bool indented)
    {
        switch (node)
        {
            case null:
                output.Append("null");
                break;
            case JsonObject obj:
                if (obj.Count == 0)
                {
                    output.Append("{}");
                    break;
                }

                output.Append('{');
                var first = true;
                foreach (var (name, value) in obj)
                {
                    if (!first)
                    {
                        output.Append(',');
                    }

                    first = false;
                    NewLine(output, indent + 1, indented);
                    String(output, name);
                    output.Append(indented ? ": " : ":");
                    Write(output, value, indent + 1, indented);
                }

                NewLine(output, indent, indented);
                output.Append('}');
                break;
            case JsonArray array:
                if (array.Count == 0)
                {
                    output.Append("[]");
                    break;
                }

                output.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Append(',');
                    }

                    NewLine(output, indent + 1, indented);
                    Write(output, array[i], indent + 1, indented);
                }

                NewLine(output, indent, indented);
                output.Append(']');
                break;
            case JsonValue value:
                switch (Marker(value))
                {
                    case NaNMarker:
                        output.Append("NaN");
                        return;
                    case InfinityMarker:
                        output.Append("Infinity");
                        return;
                    case MinusInfinityMarker:
                        output.Append("-Infinity");
                        return;
                }

                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        String(output, value.GetValue<string>());
                        break;
                    case JsonValueKind.Number:
                        output.Append(Number(value));
                        break;
                    case JsonValueKind.True:
                        output.Append("true");
                        break;
                    case JsonValueKind.False:
                        output.Append("false");
                        break;
                    default:
                        output.Append("null");
                        break;
                }

                break;
        }
    }

    private static void NewLine(StringBuilder output, int indent, bool indented)
    {
        if (indented)
        {
            output.Append('\n').Append(' ', indent * 2);
        }
    }

    // Python's json with ensure_ascii=False: only ", \ and the C0 controls are escaped (\n, \r, \t, \b, \f by name).
    private static void String(StringBuilder output, string text)
    {
        output.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case < ' ':
                    output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    output.Append(c);
                    break;
            }
        }

        output.Append('"');
    }
}
