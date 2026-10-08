// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;

namespace AgentEval.Results.Signatures;

/// <summary>
/// Base64 as a DSSE envelope carries it (contracts/aef/1/spec/04-integrity.md, [SIG-1]): written in the standard alphabet
/// with padding; read in the standard or the URL-safe alphabet (RFC 4648 §4 and §5), with or without padding. A reader
/// refuses whitespace, any character outside the alphabet, padding that is not exactly what the length needs, set
/// unused bits in the last character, and a text that mixes the two alphabets. Unlike
/// <see cref="Convert.FromBase64String(string)"/>, which skips whitespace, nothing is ignored.
/// </summary>
public static class Base64Strict
{
    /// <summary>The bytes in the standard alphabet with padding, as [SIG-1] writes them.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes);

    /// <summary>The bytes of a base64 text, or an exception when [SIG-1] refuses it.</summary>
    /// <exception cref="FormatException">The text is not base64 as [SIG-1] reads it.</exception>
    public static byte[] Decode(string text) =>
        TryDecode(text, out var bytes) ? bytes : throw new FormatException("Not base64 as AEF [SIG-1] reads it.");

    /// <summary>
    /// Reads a base64 text as [SIG-1] does. False for null, whitespace, a character outside both alphabets, both
    /// alphabets in one text, a length no base64 text has, padding that is wrong for the length, or set unused bits.
    /// </summary>
    public static bool TryDecode(string? text, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (text is null)
        {
            return false;
        }

        // The data characters, then the padding: '=' only at the end.
        var dataLength = text.Length;
        while (dataLength > 0 && text[dataLength - 1] == '=')
        {
            dataLength--;
        }
        var padding = text.Length - dataLength;

        // A final group of one character carries 6 bits, not a byte: no base64 text ends that way.
        var rest = dataLength % 4;
        if (rest == 1)
        {
            return false;
        }

        // Padded, the text fills its last group of four exactly; unpadded, it stops after the last data character.
        if (padding != 0 && (rest == 0 || padding != 4 - rest))
        {
            return false;
        }

        var standard = false;
        var urlSafe = false;
        var output = new byte[dataLength * 6 / 8];
        int accumulator = 0, bits = 0, written = 0;
        for (var i = 0; i < dataLength; i++)
        {
            var c = text[i];
            int value;
            if (c is >= 'A' and <= 'Z') value = c - 'A';
            else if (c is >= 'a' and <= 'z') value = c - 'a' + 26;
            else if (c is >= '0' and <= '9') value = c - '0' + 52;
            else if (c == '+') { value = 62; standard = true; }
            else if (c == '/') { value = 63; standard = true; }
            else if (c == '-') { value = 62; urlSafe = true; }
            else if (c == '_') { value = 63; urlSafe = true; }
            else return false;   // whitespace, '=' before the end, anything else

            accumulator = (accumulator << 6) | value;
            bits += 6;
            if (bits >= 8)
            {
                bits -= 8;
                output[written++] = (byte)(accumulator >> bits);
                accumulator &= (1 << bits) - 1;
            }
        }

        // Mixed alphabets, and the bits after the last whole byte, which a canonical encoder leaves at zero.
        if ((standard && urlSafe) || accumulator != 0)
        {
            return false;
        }

        bytes = output;
        return true;
    }
}
