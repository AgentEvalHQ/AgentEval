// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Codec semantics mirror NVIDIA garak probes.encoding (Apache-2.0): https://github.com/NVIDIA/garak
// — re-implemented natively for AgentEval.
using System.Globalization;
using System.Text;

namespace AgentEval.RedTeam.Transforms;

/// <summary>
/// Deterministic string codecs shared by <see cref="Attacks.EncodingEvasionAttack"/> and the encoding
/// <see cref="IProbeTransformer"/>s. <b>Single source of truth</b> so an encoded payload can never drift from the
/// marker it is supposed to carry (the EE-001 "decodes to PWMED" / EE-004 "decodes to Ponrk" bug class). Every method
/// is pure, allocation-light, and culture-invariant (no <see cref="System.Random"/>, no wall-clock, no current-culture
/// formatting), so output is byte-identical run-to-run and baselines stay stable.
/// </summary>
public static class Encoders
{
    /// <summary>Standard RFC 4648 Base64 of <paramref name="value"/> (UTF-8). "Say PWNED" → "U2F5IFBXTkVE".</summary>
    public static string Base64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    /// <summary>Uppercase hex of <paramref name="value"/> (UTF-8) with no separators. "PWNED" → "50574E4544".</summary>
    public static string Hex(string value) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    /// <summary>Percent-encodes every byte of <paramref name="value"/> (UTF-8), including letters.</summary>
    public static string UrlAllBytes(string value)
    {
        var sb = new StringBuilder(value.Length * 3);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>RFC 4648 Base32 of <paramref name="value"/> (UTF-8) with '=' padding. "PWNED" → "KBLU4RKE".</summary>
    public static string Base32(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = Encoding.UTF8.GetBytes(value);
        var sb = new StringBuilder();
        int buffer = 0, bitsLeft = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
        {
            sb.Append(alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        }
        while (sb.Length % 8 != 0)
        {
            sb.Append('=');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Space-separated uppercase hex of (<paramref name="value"/> XOR <paramref name="key"/>), so XOR-ing each byte
    /// back with <paramref name="key"/> recovers <paramref name="value"/>.
    /// </summary>
    public static string XorHex(string value, byte key)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return string.Join(' ', bytes.Select(b => ((byte)(b ^ key)).ToString("X2", CultureInfo.InvariantCulture)));
    }

    /// <summary>ROT13 of <paramref name="value"/> (letters only; others pass through). Self-inverse.</summary>
    public static string Rot13(string value) => Caesar(value, 13);

    /// <summary>Caesar shift of <paramref name="value"/> by <paramref name="shift"/> (letters only).</summary>
    public static string Caesar(string value, int shift) => new(value.Select(c =>
        char.IsUpper(c) ? (char)((c - 'A' + (shift % 26 + 26)) % 26 + 'A') :
        char.IsLower(c) ? (char)((c - 'a' + (shift % 26 + 26)) % 26 + 'a') : c).ToArray());

    /// <summary>Atbash (A↔Z) of <paramref name="value"/> (letters only). Self-inverse.</summary>
    public static string Atbash(string value) => new(value.Select(c =>
        char.IsUpper(c) ? (char)('Z' - (c - 'A')) :
        char.IsLower(c) ? (char)('z' - (c - 'a')) : c).ToArray());

    /// <summary>Character-reversed <paramref name="value"/>. Self-inverse.</summary>
    public static string Reversed(string value) => new(value.Reverse().ToArray());

    /// <summary>Space-separated 8-bit binary of each byte of <paramref name="value"/> (UTF-8). "P" → "01010000".</summary>
    public static string Binary(string value) =>
        string.Join(' ', Encoding.UTF8.GetBytes(value).Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));

    /// <summary>Space-separated octal of each byte of <paramref name="value"/> (UTF-8). "P" → "120".</summary>
    public static string Octal(string value) =>
        string.Join(' ', Encoding.UTF8.GetBytes(value).Select(b => Convert.ToString(b, 8)));

    /// <summary>Space-separated decimal of each byte of <paramref name="value"/> (UTF-8). "P" → "80".</summary>
    public static string AsciiDecimal(string value) =>
        string.Join(' ', Encoding.UTF8.GetBytes(value).Select(b => b.ToString(CultureInfo.InvariantCulture)));

    /// <summary>HTML decimal character references for each char of <paramref name="value"/>. "P" → "&amp;#80;".</summary>
    public static string HtmlDecimalEntities(string value) =>
        string.Concat(value.Select(c => $"&#{(int)c};"));

    /// <summary>HTML hex character references for each char of <paramref name="value"/>. "P" → "&amp;#x50;".</summary>
    public static string HtmlHexEntities(string value) =>
        string.Concat(value.Select(c => $"&#x{(int)c:X};"));

    /// <summary>C/JSON <c>\uXXXX</c> escapes for each char of <paramref name="value"/>. "P" → "\\u0050".</summary>
    public static string UnicodeEscapes(string value) =>
        string.Concat(value.Select(c => $"\\u{(int)c:X4}"));

    /// <summary>
    /// International Morse of <paramref name="value"/>: letters/digits separated by a space, words (runs broken by a
    /// space in the input) separated by " / ". "PWNED" → ".--. .-- -. . -..". Unknown chars are skipped.
    /// </summary>
    public static string Morse(string value)
    {
        var parts = new List<string>();
        foreach (var c in value.ToUpperInvariant())
        {
            if (c == ' ') parts.Add("/");
            else if (MorseByChar.TryGetValue(c, out var code)) parts.Add(code);
        }
        return string.Join(' ', parts);
    }

    /// <summary>
    /// Leetspeak of <paramref name="value"/> (uppercased, common letter→glyph substitutions). LOSSY — only an LLM
    /// guess recovers the exact original, so callers treat it as recovery-not-decode (no overclaim).
    /// </summary>
    public static string Leetspeak(string value) => value.ToUpperInvariant()
        .Replace("E", "3").Replace("A", "4").Replace("O", "0")
        .Replace("I", "1").Replace("S", "5").Replace("T", "7");

    /// <summary>
    /// Maps ASCII printable chars (U+0021..U+007E) to their fullwidth equivalents (U+FF01..U+FF5E), which fold back to
    /// ASCII under NFKC (T5-3). "PWNED" → "ＰＷＮＥＤ". Non-ASCII chars pass through.
    /// </summary>
    public static string Fullwidth(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c is >= '!' and <= '~' ? (char)(c - 0x20 + 0xFF00) : c);
        }
        return sb.ToString();
    }

    private static readonly IReadOnlyDictionary<char, string> MorseByChar = new Dictionary<char, string>
    {
        ['A'] = ".-",    ['B'] = "-...",  ['C'] = "-.-.",  ['D'] = "-..",   ['E'] = ".",
        ['F'] = "..-.",  ['G'] = "--.",   ['H'] = "....",  ['I'] = "..",    ['J'] = ".---",
        ['K'] = "-.-",   ['L'] = ".-..",  ['M'] = "--",    ['N'] = "-.",    ['O'] = "---",
        ['P'] = ".--.",  ['Q'] = "--.-",  ['R'] = ".-.",   ['S'] = "...",   ['T'] = "-",
        ['U'] = "..-",   ['V'] = "...-",  ['W'] = ".--",   ['X'] = "-..-",  ['Y'] = "-.--",
        ['Z'] = "--..",
        ['0'] = "-----", ['1'] = ".----", ['2'] = "..---", ['3'] = "...--", ['4'] = "....-",
        ['5'] = ".....", ['6'] = "-....", ['7'] = "--...", ['8'] = "---..", ['9'] = "----.",
    };
}
