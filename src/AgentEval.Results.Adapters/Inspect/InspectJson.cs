// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Results.Json;

namespace AgentEval.Results.Adapters.Inspect;

/// <summary>
/// JSON as an Inspect eval log holds it (contracts/aef/1/interop/inspect.md, "The target"): a score's unscored value is
/// the bare token <c>NaN</c>, which is not JSON, and which neither System.Text.Json's reader nor its writer handle. In a tree
/// read or built here, <c>NaN</c>, <c>Infinity</c> and <c>-Infinity</c> are held as marker strings
/// (<see cref="NaN"/>), which no Inspect text holds (they start with U+0000), and written back as the bare tokens.
/// </summary>
internal static class InspectJson
{
    /// <summary>The marker of Inspect's NaN in a tree.</summary>
    public const string NaNMarker = "\u0000NaN\u0000";

    /// <summary>The marker of Inspect's <c>Infinity</c> in a tree.</summary>
    public const string InfinityMarker = "\u0000Infinity\u0000";

    /// <summary>The marker of Inspect's <c>-Infinity</c> in a tree.</summary>
    public const string MinusInfinityMarker = "\u0000-Infinity\u0000";

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
    /// Reads an Inspect log as I-JSON (RFC 7493), as AEF reads its own files ([ENC-1] to [ENC-3]), within [ENC-17]'s
    /// nesting depth of 64 (inspect.md, "Reading the log", R9-2), with the one exception Inspect needs: a bare
    /// <c>NaN</c>, read as <see cref="NaNMarker"/> (where it may stand is the caller's to check). Refused: a byte-order
    /// mark, bytes that are not UTF-8, a member named twice, an unpaired surrogate in a string or a name,
    /// <c>Infinity</c> or <c>-Infinity</c>, a number that overflows binary64, any other syntax error, nesting deeper
    /// than 64. There is no size limit: an Inspect log is not an AEF file.
    /// </summary>
    /// <exception cref="FormatException">The text is refused; the message says why.</exception>
    public static JsonNode? Parse(byte[] utf8)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        var text = new List<byte>(utf8.Length + 64);
        var inString = false;
        for (var i = 0; i < utf8.Length; i++)
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
            if (Token(utf8, i, "Infinity"u8) || Token(utf8, i, "-Infinity"u8))
            {
                throw new FormatException("the log holds Infinity or -Infinity, which no AEF number holds ([ENC-3])");
            }

            if (Token(utf8, i, "NaN"u8))
            {
                text.AddRange(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(NaNMarker)));
                i += 2;   // the loop steps past the last byte
                continue;
            }

            text.Add(b);
        }

        try
        {
            // AgentEval.Results' own I-JSON reader: the checks every AEF reader makes, on the bytes, before a node is built.
            return AefJsonReader.ParseValue(text.ToArray(), int.MaxValue);
        }
        catch (AefReadException e)
        {
            throw new FormatException($"the log is not I-JSON within a depth of {AefLimits.MaxDepth}: {e.Message}", e);
        }
    }

    /// <summary>
    /// A <c>.json</c> log laid out as Inspect lays one out (Python's <c>json.dumps(value, indent=2, ensure_ascii=False)</c>):
    /// two-space indent, <c>": "</c> between a name and its value, non-ASCII text as UTF-8, numbers as
    /// <see cref="Number"/> writes them, and the markers as the bare tokens (<c>NaN</c>), which a standard JSON writer
    /// does not write. The page fixes values, not bytes (R7I-1): the layout is a courtesy.
    /// </summary>
    public static string Indented(JsonNode? value)
    {
        var output = new StringBuilder();
        Write(output, value, indent: 0, indented: true);
        return output.ToString();
    }

    /// <summary>
    /// <paramref name="value"/> serialized by the JSON Canonicalization Scheme (RFC 8785, JCS), in UTF-8: no whitespace;
    /// numbers as ECMAScript's <c>Number::toString</c> writes them (<see cref="Shortest"/>); strings with only <c>"</c>,
    /// <c>\</c> and the control characters escaped (<c>\b \f \n \r \t</c> by name, the others as <c>\u00xx</c>), the rest
    /// literal; object members sorted by the UTF-16 code units of their names. So two writers give the same bytes for the
    /// same values, whatever their parser kept of member order or number spelling (inspect.md, "Content that is not text").
    /// </summary>
    /// <exception cref="FormatException">A NaN, an infinity or an unpaired surrogate, which JCS cannot write.</exception>
    public static byte[] Canonical(JsonNode? value)
    {
        var output = new StringBuilder();
        WriteCanonical(output, value);
        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static void WriteCanonical(StringBuilder output, JsonNode? node)
    {
        switch (node)
        {
            case null:
                output.Append("null");
                break;
            case JsonObject obj:
                output.Append('{');
                var members = obj.Select(m => (Name: m.Key == NaNMarker ? throw new FormatException("it holds NaN as a member name, which JCS cannot write") : Checked(m.Key), m.Value)).ToList();
                members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));   // UTF-16 code units (RFC 8785 §3.2.3)
                for (var i = 0; i < members.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Append(',');
                    }

                    CanonicalString(output, members[i].Name);
                    output.Append(':');
                    WriteCanonical(output, members[i].Value);
                }

                output.Append('}');
                break;
            case JsonArray array:
                output.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Append(',');
                    }

                    WriteCanonical(output, array[i]);
                }

                output.Append(']');
                break;
            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        string text;
                        try
                        {
                            text = value.GetValue<string>();
                        }
                        catch (InvalidOperationException)
                        {
                            throw new FormatException("it holds a string with an unpaired surrogate, which JCS cannot write");
                        }

                        if (text is NaNMarker or InfinityMarker or MinusInfinityMarker)
                        {
                            throw new FormatException($"it holds {text.Trim('\0')}, which JCS cannot write");
                        }

                        CanonicalString(output, Checked(text));
                        break;
                    case JsonValueKind.Number:
                        output.Append(Shortest(value.GetValue<double>()));
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

    // A string with no unpaired surrogate (JCS cannot write one).
    private static string Checked(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(text[i]))
            {
                throw new FormatException("it holds a string with an unpaired surrogate, which JCS cannot write");
            }
        }

        return text;
    }

    // RFC 8785 §3.2.2.2 (ECMAScript's JSON.stringify): ", \ and the control characters escaped, the rest literal.
    private static void CanonicalString(StringBuilder output, string text) => String(output, text);

    /// <summary>
    /// A number as the log or the run spells it (the page fixes values, not bytes, R7I-1): a number read from a file
    /// keeps its own text; a number computed here (a duration in seconds, a sum of costs) is written as
    /// <see cref="Shortest"/> writes it.
    /// </summary>
    public static string Number(JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            return element.GetRawText();
        }

        if (value.TryGetValue<int>(out var n))
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l.ToString(CultureInfo.InvariantCulture);
        }

        return value.TryGetValue<double>(out var d) ? Shortest(d) : value.ToJsonString();
    }

    /// <summary>
    /// A finite binary64 value as text from its value alone (R8-4): the shortest decimal that reads back as the same value
    /// ([ENC-4]), spelled as ECMAScript's <c>Number::toString</c> spells it: an integral value with no fraction, plain
    /// digits up to 21 of them, then exponent form (<c>1000</c>, <c>10000000000000000</c>, <c>1e+21</c>,
    /// <c>0.000001</c>, <c>1e-7</c>). So two parsers that read <c>1000.0</c> and <c>1000</c> alike write the same text.
    /// </summary>
    /// <exception cref="ArgumentException">NaN or an infinity.</exception>
    public static string Shortest(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"{value} is not a finite number.", nameof(value));
        }

        if (value == 0)
        {
            return "0";   // -0 too, as Number::toString writes it
        }

        // The shortest round-trip digits (.NET Core 3.0 and later), as d1…dk × 10^(n − k).
        var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var e = r.IndexOfAny(['E', 'e']);
        var exponent = e < 0 ? 0 : int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var mantissa = e < 0 ? r : r[..e];
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var whole = dot < 0 ? mantissa : mantissa[..dot];
        var digits = whole + (dot < 0 ? "" : mantissa[(dot + 1)..]);
        var n = whole.Length + exponent;
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits[lead..].TrimEnd('0');
        n -= lead;
        var k = digits.Length;

        // ECMAScript Number::toString (ECMA-262, 6.1.6.1.20), steps 6 to 10.
        var text = n switch
        {
            _ when k <= n && n <= 21 => digits + new string('0', n - k),
            > 0 and <= 21 => digits[..n] + "." + digits[n..],
            > -6 and <= 0 => "0." + new string('0', -n) + digits,
            _ => (k == 1 ? digits : digits[..1] + "." + digits[1..]) + "e" + (n - 1 < 0 ? "-" : "+")
                 + Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture),
        };
        return value < 0 ? "-" + text : text;
    }

    private static string? Marker(JsonNode? node)
    {
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            return v.GetValue<string>() is NaNMarker or InfinityMarker or MinusInfinityMarker ? v.GetValue<string>() : null;
        }
        catch (InvalidOperationException)
        {
            return null;   // a string System.Text.Json cannot read (an unpaired surrogate) is no marker
        }
    }

    private static bool Token(byte[] utf8, int at, ReadOnlySpan<byte> token) =>
        at + token.Length <= utf8.Length && utf8.AsSpan(at, token.Length).SequenceEqual(token);

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
