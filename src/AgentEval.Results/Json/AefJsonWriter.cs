// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentEval.Results.Json;

/// <summary>
/// Writes JSON the way an AEF writer must (contracts/aef/1/spec/02-encoding.md): UTF-8 with no byte-order mark, LF line
/// ends on every platform, every number the binary64 value a reader will read ([ENC-4]) with integers in plain digits
/// (<c>2</c>, never <c>2.0</c> or <c>2E0</c>), and nothing a reader must refuse: no NaN or infinity ([ENC-3]), no unpaired
/// surrogate ([ENC-2]), no nesting deeper than 64 and no document or line above 4 MiB (40 MiB for a seal, 56 MiB for an
/// envelope) ([ENC-17]).
/// </summary>
/// <remarks>
/// Written by hand rather than with <see cref="Utf8JsonWriter"/>: its indented form uses <see cref="Environment.NewLine"/>
/// before .NET 9, writes 1E+15 for an integral double, and escapes all non-ASCII text by default. Non-ASCII text is
/// written as UTF-8; only <c>"</c>, <c>\</c> and the C0 controls are escaped.
/// </remarks>
public static class AefJsonWriter
{
    private const double MaxExactInteger = 9_007_199_254_740_991;   // 2^53 − 1 ([ENC-4])

    /// <summary>A JSON document ([ENC-1]): the object indented by two spaces, ending in LF.</summary>
    /// <param name="document">The document.</param>
    /// <param name="maxBytes">
    /// Its size limit ([ENC-17]): <see cref="AefLimits.MaxJsonBytes"/>, or for a seal or an envelope
    /// <see cref="AefLimits.MaxSealBytes"/> or <see cref="AefLimits.MaxEnvelopeBytes"/> (<see cref="AefLimits.MaxBytesOf"/> gives it by path).
    /// </param>
    /// <exception cref="ArgumentException">A value a reader would refuse, or a document beyond the limits.</exception>
    public static byte[] Document(JsonObject document, int maxBytes = AefLimits.MaxJsonBytes)
    {
        ArgumentNullException.ThrowIfNull(document);
        var output = new ArrayBufferWriter<byte>();
        Write(output, document, indent: 0, indented: true, depth: 1);
        output.Write("\n"u8);
        return output.WrittenCount > maxBytes
            ? throw new ArgumentException($"The document is {output.WrittenCount} bytes: this JSON file is at most {maxBytes} ([ENC-17]).", nameof(document))
            : output.WrittenSpan.ToArray();
    }

    /// <summary>One NDJSON line ([ENC-5]): the object on one line, ending in LF.</summary>
    /// <exception cref="ArgumentException">A value a reader would refuse, or a line beyond the limits.</exception>
    public static byte[] Line(JsonObject line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var output = new ArrayBufferWriter<byte>();
        Write(output, line, indent: 0, indented: false, depth: 1);
        if (output.WrittenCount > AefLimits.MaxJsonBytes)
        {
            throw new ArgumentException($"The line is {output.WrittenCount} bytes: an NDJSON line is at most {AefLimits.MaxJsonBytes} ([ENC-17]).", nameof(line));
        }

        output.Write("\n"u8);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Any JSON value on one line, with no LF: the same rules for values, without the limits of an AEF file (a tool's
    /// output, such as the conformance driver's).
    /// </summary>
    /// <exception cref="ArgumentException">A value a reader would refuse.</exception>
    public static byte[] Compact(JsonNode? value)
    {
        var output = new ArrayBufferWriter<byte>();
        Write(output, value, indent: 0, indented: false, depth: int.MinValue);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>A number as AEF writes it: plain digits for an integral value within ±(2^53 − 1), else the shortest text that reads back as the same binary64 value.</summary>
    /// <exception cref="ArgumentException">NaN or an infinity ([ENC-3]).</exception>
    public static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException($"{value} is not a finite number: JSON has no NaN or infinity ([ENC-3]).", nameof(value));
        }

        return Math.Floor(value) == value && Math.Abs(value) <= MaxExactInteger
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void Write(ArrayBufferWriter<byte> output, JsonNode? node, int indent, bool indented, int depth)
    {
        switch (node)
        {
            case null:
                output.Write("null"u8);
                break;
            case JsonObject obj:
                Nested(depth);
                if (obj.Count == 0)
                {
                    output.Write("{}"u8);
                    break;
                }

                output.Write("{"u8);
                var firstMember = true;
                foreach (var (name, value) in obj)
                {
                    if (!firstMember) output.Write(","u8);
                    firstMember = false;
                    NewLine(output, indent + 1, indented);
                    WriteString(output, name);
                    output.Write(indented ? ": "u8 : ":"u8);
                    Write(output, value, indent + 1, indented, depth + 1);
                }

                NewLine(output, indent, indented);
                output.Write("}"u8);
                break;
            case JsonArray array:
                Nested(depth);
                if (array.Count == 0)
                {
                    output.Write("[]"u8);
                    break;
                }

                output.Write("["u8);
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0) output.Write(","u8);
                    NewLine(output, indent + 1, indented);
                    Write(output, array[i], indent + 1, indented, depth + 1);
                }

                NewLine(output, indent, indented);
                output.Write("]"u8);
                break;
            case JsonValue value:
                WriteValue(output, value);
                break;
        }
    }

    private static void Nested(int depth)
    {
        if (depth > AefLimits.MaxDepth)
        {
            throw new ArgumentException($"Nested deeper than {AefLimits.MaxDepth} objects and arrays ([ENC-17]).");
        }
    }

    private static void NewLine(ArrayBufferWriter<byte> output, int indent, bool indented)
    {
        if (!indented) return;
        output.Write("\n"u8);
        for (var i = 0; i < indent; i++) output.Write("  "u8);
    }

    private static void WriteValue(ArrayBufferWriter<byte> output, JsonValue value)
    {
        switch (value.GetValueKind())
        {
            case JsonValueKind.String:
                WriteString(output, value.TryGetValue<string>(out var text)
                    ? text
                    : JsonDocument.Parse(value.ToJsonString()).RootElement.GetString()!);
                break;
            case JsonValueKind.Number:
                output.Write(Encoding.UTF8.GetBytes(FormatNumber(Number(value))));
                break;
            case JsonValueKind.True:
                output.Write("true"u8);
                break;
            case JsonValueKind.False:
                output.Write("false"u8);
                break;
            case JsonValueKind.Null:
                output.Write("null"u8);
                break;
            default:
                throw new ArgumentException($"A value of kind {value.GetValueKind()} is not JSON.");
        }
    }

    // The binary64 value a reader reads ([ENC-4]): a double as it is, any other number through its JSON text.
    private static double Number(JsonValue value)
    {
        if (value.TryGetValue<double>(out var number))
        {
            return number;
        }

        string text;
        try
        {
            text = value.ToJsonString();
        }
        catch (ArgumentException e)
        {
            throw new ArgumentException($"A number JSON cannot hold ([ENC-3]): {e.Message}", e);
        }

        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static void WriteString(ArrayBufferWriter<byte> output, string text)
    {
        output.Write("\""u8);
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                throw new ArgumentException($"A string holds an unpaired surrogate (\\u{(int)c:x4}) at {i} ([ENC-2]).");
            }

            if (c is not ('"' or '\\') && c >= 0x20)
            {
                continue;
            }

            output.Write(Encoding.UTF8.GetBytes(text, start, i - start));
            output.Write(Encoding.ASCII.GetBytes(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => $"\\u{(int)c:x4}",
            }));
            start = i + 1;
        }

        output.Write(Encoding.UTF8.GetBytes(text, start, text.Length - start));
        output.Write("\""u8);
    }
}
