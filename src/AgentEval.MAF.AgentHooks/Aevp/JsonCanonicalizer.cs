// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.MAF.AgentHooks.Aevp;

/// <summary>
/// RFC 8785 JSON Canonicalization Scheme (JCS): the byte form an AEVP content address is computed over.
/// </summary>
/// <remarks>
/// <para>
/// A content address only resolves for someone else if both sides hash the same bytes. Serializer output follows
/// declaration order, so a verifier that canonicalizes the same document with the AGENT-HOOKS reference core
/// would compute a different <c>sha256:</c>. JCS fixes the bytes:
/// </para>
/// <list type="bullet">
/// <item>object members sorted by their names' UTF-16 code units;</item>
/// <item>no insignificant whitespace;</item>
/// <item>strings escaped as ECMAScript <c>JSON.stringify</c> does: only <c>"</c>, <c>\</c> and control characters,
/// with lone surrogates as <c>\uXXXX</c>;</item>
/// <item>numbers in ECMAScript <c>Number.prototype.toString</c> form (shortest round-trip, exponent only outside
/// [1e-6, 1e21)).</item>
/// </list>
/// <para>
/// The test suite checks this implementation against the reference core's own canonicalizer whenever that native
/// library loads.
/// </para>
/// </remarks>
internal static class JsonCanonicalizer
{
    /// <summary>Canonicalizes a JSON document.</summary>
    /// <exception cref="JsonException">The document is not valid JSON.</exception>
    /// <exception cref="ArgumentException">A number is not finite (JCS has no form for NaN or infinity).</exception>
    public static string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var node = JsonNode.Parse(json);
        var sb = new StringBuilder(json.Length);
        Write(sb, node);
        return sb.ToString();
    }

    /// <summary>Canonicalizes a parsed node.</summary>
    public static string Canonicalize(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        if (node is null)
        {
            sb.Append("null");
            return;
        }

        switch (node.GetValueKind())
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var (name, value) in node.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, name);
                    sb.Append(':');
                    Write(sb, value);
                }
                sb.Append('}');
                return;

            case JsonValueKind.Array:
                sb.Append('[');
                var array = node.AsArray();
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, array[i]);
                }
                sb.Append(']');
                return;

            case JsonValueKind.String:
                WriteString(sb, node.GetValue<string>());
                return;

            case JsonValueKind.Number:
                // Read the number from its JSON text, not from the CLR storage type, so a node built from a long,
                // a double or a JsonElement canonicalizes identically.
                sb.Append(FormatNumber(double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)));
                return;

            case JsonValueKind.True:
                sb.Append("true");
                return;

            case JsonValueKind.False:
                sb.Append("false");
                return;

            default:
                sb.Append("null");
                return;
        }
    }

    private static void WriteString(StringBuilder sb, string value)
    {
        sb.Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        AppendUnicodeEscape(sb, c);
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c).Append(value[++i]);   // a well-formed pair is written as-is
                    }
                    else if (char.IsSurrogate(c))
                    {
                        AppendUnicodeEscape(sb, c);         // a lone surrogate cannot be UTF-8 encoded
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    private static void AppendUnicodeEscape(StringBuilder sb, char c) =>
        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));

    /// <summary>ECMAScript <c>Number.prototype.toString</c> for a finite double (ECMA-262 Number::toString).</summary>
    internal static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentException("JCS cannot represent NaN or infinity.", nameof(value));
        if (value == 0)
            return "0";   // covers -0, which ECMAScript prints as "0"

        var negative = value < 0;
        // "R" gives the shortest digits that round-trip; parse them into (digits, exponent) and lay them out per
        // ECMAScript, whose notation thresholds differ from .NET's.
        var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var ePos = r.IndexOfAny(['E', 'e']);
        var mantissa = ePos >= 0 ? r[..ePos] : r;
        var exp = ePos >= 0 ? int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : 0;

        var dot = mantissa.IndexOf('.');
        var intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        var fracPart = dot >= 0 ? mantissa[(dot + 1)..] : string.Empty;
        var digits = (intPart + fracPart).TrimStart('0');
        // n: the position of the decimal point relative to the first significant digit (value = 0.d1d2… × 10^n).
        var leadingZeros = (intPart + fracPart).Length - digits.Length;
        var n = intPart.Length - leadingZeros + exp;
        digits = digits.TrimEnd('0');
        var k = digits.Length;

        var sb = new StringBuilder();
        if (negative) sb.Append('-');

        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            var e = n - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(e < 0 ? '-' : '+').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
