// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace AgentEval.Results.Json;

/// <summary>
/// Reads JSON the way every AEF reader must (contracts/aef/1/spec/02-encoding.md, §2.1 and §2.6): one I-JSON value in
/// UTF-8 with no byte-order mark ([ENC-1], [RFC 7493]), no member named twice after unescaping (<c>"a"</c> and
/// <c>"\u0061"</c> are the same name) and no unpaired surrogate ([ENC-2]), every number a finite binary64 value
/// ([ENC-3]: <c>1e400</c> is refused, <c>1e-400</c> reads as 0), at most 4 MiB (40 MiB for a seal, 56 MiB for an envelope)
/// and nested at most 64 deep ([ENC-17]).
/// A text that breaks a rule of §2.1 throws <see cref="AefEncodingException"/>; one beyond a limit throws
/// <see cref="AefLimitException"/>, and is never read in part.
/// </summary>
/// <remarks>
/// The bytes are checked first with <see cref="Utf8JsonReader"/>, which keeps no state beyond the open objects' names,
/// and only then built into nodes: a member named twice is refused rather than resolved (implementations disagree on
/// which one wins, [ENC-2]), and the reader's own depth limit is set above AEF's so the depth is counted here and
/// reported as a limit rather than a syntax error. Numbers are kept as written; <c>(double)node</c> reads them as
/// binary64 ([ENC-4]).
/// </remarks>
public static class AefJsonReader
{
    // EF BB BF: U+FEFF in UTF-8.
    private static ReadOnlySpan<byte> ByteOrderMark => [0xEF, 0xBB, 0xBF];

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = AefLimits.MaxDepth * 2,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        MaxDepth = AefLimits.MaxDepth * 2,
    };

    /// <summary>
    /// A JSON document ([ENC-1]): an I-JSON text whose top-level value is an object.
    /// </summary>
    /// <param name="utf8">The bytes.</param>
    /// <param name="maxBytes">
    /// The size limit of this file ([ENC-17]): <see cref="AefLimits.MaxJsonBytes"/>, or for a seal or an envelope
    /// <see cref="AefLimits.MaxSealBytes"/> or <see cref="AefLimits.MaxEnvelopeBytes"/> (<see cref="AefLimits.MaxBytesOf"/> gives it by path).
    /// </param>
    /// <exception cref="AefEncodingException">Not an I-JSON text, or the top-level value is not an object.</exception>
    /// <exception cref="AefLimitException">Larger than <paramref name="maxBytes"/>, or nested deeper than 64.</exception>
    public static JsonObject ParseDocument(ReadOnlySpan<byte> utf8, int maxBytes = AefLimits.MaxJsonBytes)
    {
        return ParseValue(utf8, maxBytes) as JsonObject
               ?? throw new AefEncodingException("The top-level value is not an object ([ENC-1]).");
    }

    /// <summary>
    /// Any JSON value under the same rules (an input that is not an AEF document: a list of paths, a trial number).
    /// <c>null</c> for the JSON literal <c>null</c>.
    /// </summary>
    /// <exception cref="AefEncodingException">Not an I-JSON text.</exception>
    /// <exception cref="AefLimitException">Larger than <paramref name="maxBytes"/>, or nested deeper than 64.</exception>
    public static JsonNode? ParseValue(ReadOnlySpan<byte> utf8, int maxBytes = AefLimits.MaxJsonBytes)
    {
        Check(utf8, maxBytes);
        return JsonNode.Parse(utf8, nodeOptions: null, DocumentOptions);
    }

    /// <summary>Checks that the bytes are one I-JSON value within the limits, without building it.</summary>
    /// <exception cref="AefEncodingException">Not an I-JSON text.</exception>
    /// <exception cref="AefLimitException">Larger than <paramref name="maxBytes"/>, or nested deeper than 64.</exception>
    public static void Check(ReadOnlySpan<byte> utf8, int maxBytes = AefLimits.MaxJsonBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (utf8.Length > maxBytes)
        {
            throw new AefLimitException($"{utf8.Length} bytes: this JSON text is at most {maxBytes} bytes ([ENC-17]).");
        }

        // [ENC-17]: the limits are checked on the bytes before the content is trusted, so a text both too deep and not
        // I-JSON is refused for the limit, whichever a parser would meet first.
        if (DepthOf(utf8) > AefLimits.MaxDepth)
        {
            throw new AefLimitException($"Nested deeper than {AefLimits.MaxDepth} objects and arrays ([ENC-17]).");
        }

        if (utf8.StartsWith(ByteOrderMark))
        {
            throw new AefEncodingException("A byte-order mark ([ENC-1]).");
        }

        // Overlong forms, encoded surrogates and code points beyond U+10FFFF are not UTF-8 either.
        if (!Utf8.IsValid(utf8))
        {
            throw new AefEncodingException("Not UTF-8 ([ENC-1]).");
        }

        var reader = new Utf8JsonReader(utf8, ReaderOptions);
        var names = new Stack<HashSet<string>>();
        var depth = 0;
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (++depth > AefLimits.MaxDepth)
                        {
                            throw new AefLimitException($"Nested deeper than {AefLimits.MaxDepth} objects and arrays ([ENC-17]).");
                        }

                        if (reader.TokenType == JsonTokenType.StartObject)
                        {
                            names.Push(new HashSet<string>(StringComparer.Ordinal));
                        }

                        break;
                    case JsonTokenType.EndObject:
                        depth--;
                        names.Pop();
                        break;
                    case JsonTokenType.EndArray:
                        depth--;
                        break;
                    case JsonTokenType.PropertyName:
                        var name = reader.ValueIsEscaped ? Unescape(reader.ValueSpan) : Encoding.UTF8.GetString(reader.ValueSpan);
                        if (!names.Peek().Add(name))
                        {
                            throw new AefEncodingException($"The member '{name}' appears twice in one object ([ENC-2]).");
                        }

                        break;
                    case JsonTokenType.String when reader.ValueIsEscaped:
                        Unescape(reader.ValueSpan);   // only escapes can spell an unpaired surrogate: raw UTF-8 cannot
                        break;
                    case JsonTokenType.Number:
                        if (!double.TryParse(reader.ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                            || !double.IsFinite(value))
                        {
                            throw new AefEncodingException(
                                $"{Encoding.UTF8.GetString(reader.ValueSpan[..Math.Min(reader.ValueSpan.Length, 32)])} is not a finite binary64 number ([ENC-3]).");
                        }

                        break;
                }
            }
        }
        catch (JsonException e)
        {
            // Syntax: an unclosed value, a trailing comma, a comment, NaN, a raw control character, a second value.
            throw new AefEncodingException($"Not a JSON text: {e.Message}");
        }
    }

    /// <summary>
    /// The deepest nesting of objects and arrays, scanned on the bytes without parsing ([ENC-17]: a depth scan): every
    /// <c>[</c> or <c>{</c> outside a string opens a level, every <c>]</c> or <c>}</c> outside a string closes one, and a
    /// string runs from a <c>"</c> to the next <c>"</c> not escaped by <c>\</c> (to the end of the text when it is not
    /// closed). For an I-JSON text this is its depth; the top-level value is at depth 1.
    /// </summary>
    public static int DepthOf(ReadOnlySpan<byte> utf8)
    {
        int depth = 0, deepest = 0;
        for (var i = 0; i < utf8.Length; i++)
        {
            switch (utf8[i])
            {
                case (byte)'"':
                    for (i++; i < utf8.Length && utf8[i] != (byte)'"'; i++)
                    {
                        if (utf8[i] == (byte)'\\')
                        {
                            i++;   // the escaped byte, a quote included
                        }
                    }

                    break;
                case (byte)'[' or (byte)'{':
                    deepest = Math.Max(deepest, ++depth);
                    break;
                case (byte)']' or (byte)'}':
                    depth--;
                    break;
            }
        }

        return deepest;
    }

    /// <summary>
    /// The text of a string token written with escapes (the reader has already checked their syntax); an escaped
    /// surrogate that is not half of a high-then-low pair throws.
    /// </summary>
    private static string Unescape(ReadOnlySpan<byte> raw)
    {
        var text = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var slash = raw[i..].IndexOf((byte)'\\');
            if (slash < 0)
            {
                text.Append(Encoding.UTF8.GetString(raw[i..]));
                break;
            }

            text.Append(Encoding.UTF8.GetString(raw.Slice(i, slash)));
            i += slash;
            var escape = (char)raw[i + 1];
            if (escape != 'u')
            {
                text.Append(escape switch
                {
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    _ => escape,   // \" \\ \/
                });
                i += 2;
                continue;
            }

            var unit = Hex4(raw.Slice(i + 2, 4));
            i += 6;
            if (char.IsHighSurrogate(unit)
                && i + 6 <= raw.Length && raw[i] == '\\' && raw[i + 1] == 'u' && Hex4(raw.Slice(i + 2, 4)) is var low
                && char.IsLowSurrogate(low))
            {
                text.Append(unit).Append(low);
                i += 6;
            }
            else if (char.IsSurrogate(unit))
            {
                throw new AefEncodingException($"A string holds an unpaired surrogate (\\u{(int)unit:x4}) ([ENC-2]).");
            }
            else
            {
                text.Append(unit);
            }
        }

        return text.ToString();
    }

    private static char Hex4(ReadOnlySpan<byte> digits)
    {
        var value = 0;
        foreach (var d in digits)
        {
            value = (value << 4) | (d <= '9' ? d - '0' : (d | 0x20) - 'a' + 10);
        }

        return (char)value;
    }
}
