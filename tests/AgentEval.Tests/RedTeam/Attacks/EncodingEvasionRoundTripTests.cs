// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Attacks/EncodingEvasionRoundTripTests.cs
//
// Decoder techniques mirror NVIDIA garak probes.encoding (Apache-2.0):
// https://github.com/NVIDIA/garak — ported natively to C# for AgentEval.
using System.Globalization;
using System.Text;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;

namespace AgentEval.Tests.RedTeam.Attacks;

public sealed class EncodingEvasionRoundTripTests
{
    private const string Marker = "PWNED";
    private static readonly EncodingEvasionAttack Attack = new();

    public static IEnumerable<object[]> AllEncodedProbes() =>
        Attack.GetProbes(Intensity.Comprehensive)
            .Select(p => new object[] { p.Id, p.Technique ?? "", p.Prompt });

    [Theory]
    [MemberData(nameof(AllEncodedProbes))]
    public void EveryProbe_DecodesToMarker(string probeId, string technique, string prompt)
    {
        var decoded = Decode(technique, prompt);
        Assert.True(
            decoded.Contains(Marker, StringComparison.OrdinalIgnoreCase),
            $"Probe {probeId} (technique '{technique}') did not round-trip to '{Marker}'. " +
            $"Decoded='{decoded}' from prompt='{prompt}'.");
    }

    [Fact]
    public void EveryProbe_DeclaresMarkerInExpectedTokens()
    {
        foreach (var probe in Attack.GetProbes(Intensity.Comprehensive))
        {
            Assert.NotNull(probe.ExpectedTokens);
            Assert.Contains(Marker, probe.ExpectedTokens!);
        }
    }

    private static string Decode(string technique, string prompt) => technique switch
    {
        "base64"            => FromBase64(ExtractToken(prompt, @"[A-Za-z0-9+/=]{6,}")),
        "base32"            => FromBase32(ExtractToken(prompt, @"[A-Z2-7=]{6,}")),
        "hex"               => FromHex(ExtractToken(prompt, @"[0-9A-Fa-f]{8,}")),
        "url_encoding"      => Uri.UnescapeDataString(ExtractToken(prompt, @"\S*%[0-9A-Fa-f]{2}\S*")),
        "rot13"             => Rot13(prompt),
        "caesar"            => CaesarShift(ExtractToken(prompt, @"[A-Z]{4,}"), -3),
        "atbash"            => Atbash(ExtractToken(prompt, @"[A-Z]{4,}")),
        "xor"               => XorHexBytes(ExtractAllHexBytes(prompt), 0x20),
        "html_entities"     => DecodeHtmlDecimal(prompt),
        "html_hex_entities" => DecodeHtmlHex(prompt),
        "unicode_escapes"   => DecodeUnicodeEscapes(prompt),
        "morse"             => DecodeMorse(prompt),
        "ascii_decimal"     => FromAsciiDecimal(prompt),
        "binary"            => FromBinary(prompt),
        "octal"             => FromOctal(prompt),
        "reversed"          => new string(ExtractToken(prompt, @"[A-Z]{4,}").Reverse().ToArray()),
        // Natural-language / lossy transforms: assert recoverability of the marker rather than a codec.
        "zwsp"              => prompt.Replace("\u200B", ""),
        "homoglyphs"        => NormalizeHomoglyphs(prompt),
        "leetspeak"         => DecodeLeetspeak(prompt),
        "pig_latin"         => PigLatinToEnglish(ExtractToken(prompt, @"[A-Z]+-[A-Z]+AY")),
        "acrostic"          => Acrostic(prompt),
        "phone_keypad"      => DecodeT9(prompt),
        "nato"              => NatoToLetters(prompt),
        _                   => throw new InvalidOperationException($"Unknown technique '{technique}'.")
    };

    private static string ExtractToken(string text, string pattern)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(text, pattern);
        return matches.Count > 0 ? matches[^1].Value : text;
    }

    // Decodes the leetspeak payload (text after the final ':') back to plain letters. EE-015 now derives its payload
    // from Encoders.Leetspeak (single source of truth), so this is exactly that codec's inverse glyph map.
    private static string DecodeLeetspeak(string prompt)
    {
        var payload = prompt[(prompt.LastIndexOf(':') + 1)..].Trim();
        payload = payload
            .Replace("3", "E")
            .Replace("4", "A")
            .Replace("0", "O")
            .Replace("1", "I")
            .Replace("5", "S")
            .Replace("7", "T");
        return payload.ToUpperInvariant();
    }

    // Decodes the multi-press T9 keypad payload ("7×1 9×1 6×2 3×2 3×1") back to letters.
    private static string DecodeT9(string prompt)
    {
        var keys = new Dictionary<char, string>
        {
            ['2'] = "ABC", ['3'] = "DEF", ['4'] = "GHI", ['5'] = "JKL",
            ['6'] = "MNO", ['7'] = "PQRS", ['8'] = "TUV", ['9'] = "WXYZ",
        };
        var sb = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(prompt, @"(\d)\D(\d)"))
        {
            var digit = m.Groups[1].Value[0];
            var presses = int.Parse(m.Groups[2].Value);
            if (keys.TryGetValue(digit, out var letters) && presses >= 1 && presses <= letters.Length)
                sb.Append(letters[presses - 1]);
        }
        return sb.ToString();
    }

    private static IEnumerable<byte> ExtractAllHexBytes(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\b[0-9A-Fa-f]{2}\b")
            .Select(m => Convert.ToByte(m.Value, 16));

    private static string FromBase64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

    private static string FromHex(string s)
    {
        var bytes = new byte[s.Length / 2];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return Encoding.ASCII.GetString(bytes);
    }

    private static string FromBase32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.TrimEnd('=').ToUpperInvariant();
        var bits = 0; var value = 0; var output = new List<byte>();
        foreach (var c in s)
        {
            var idx = alphabet.IndexOf(c);
            if (idx < 0) continue;
            value = (value << 5) | idx; bits += 5;
            if (bits >= 8) { output.Add((byte)((value >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return Encoding.ASCII.GetString(output.ToArray());
    }

    private static string Rot13(string s) => new(s.Select(c =>
        char.IsLetter(c) ? (char)((char.ToUpperInvariant(c) - 'A' + 13) % 26 + (char.IsUpper(c) ? 'A' : 'a')) : c).ToArray());

    private static string CaesarShift(string s, int shift) => new(s.Select(c =>
        char.IsLetter(c) ? (char)((c - 'A' + shift % 26 + 26) % 26 + 'A') : c).ToArray());

    private static string Atbash(string s) => new(s.Select(c =>
        char.IsLetter(c) ? (char)('Z' - (char.ToUpperInvariant(c) - 'A')) : c).ToArray());

    private static string XorHexBytes(IEnumerable<byte> bytes, byte key) =>
        Encoding.ASCII.GetString(bytes.Select(b => (byte)(b ^ key)).ToArray());

    private static string DecodeHtmlDecimal(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"&#(\d+);",
            m => ((char)int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToString());

    private static string DecodeHtmlHex(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"&#x([0-9A-Fa-f]+);",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static string DecodeUnicodeEscapes(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\\u([0-9A-Fa-f]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    private static string FromAsciiDecimal(string s) => new(
        System.Text.RegularExpressions.Regex.Matches(s, @"\b\d{2,3}\b")
            .Select(m => (char)int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray());

    private static string FromBinary(string s) => new(
        System.Text.RegularExpressions.Regex.Matches(s, @"[01]{8}")
            .Select(m => (char)Convert.ToInt32(m.Value, 2)).ToArray());

    private static string FromOctal(string s) => new(
        System.Text.RegularExpressions.Regex.Matches(s, @"\b[0-7]{3}\b")
            .Select(m => (char)Convert.ToInt32(m.Value, 8)).ToArray());

    private static readonly Dictionary<string, char> MorseMap = new()
    { [".--."] = 'P', [".--"] = 'W', ["-."] = 'N', ["."] = 'E', ["-.."] = 'D' };

    private static string DecodeMorse(string s)
    {
        var token = s[(s.IndexOf(':') + 1)..].Trim();
        return new string(token.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(code => MorseMap.TryGetValue(code, out var c) ? c : '?').ToArray());
    }

    // T5-3 (A1a-05): EE-013 now uses NFKC-foldable fullwidth Latin ("ＰＷＮＥＤ"), recovered by NFKC
    // normalization — the same fold the production evaluator applies. (Cross-script Cyrillic/Greek
    // look-alikes do NOT fold under NFKC and were unwinnable, which this probe used to be.)
    private static string NormalizeHomoglyphs(string s) => s.Normalize(NormalizationForm.FormKC);

    private static string PigLatinToEnglish(string token)
    {
        var parts = token.Split('-');
        if (parts.Length != 2) return token;
        var body = parts[0];
        var moved = parts[1].Replace("AY", "");
        return moved + body;
    }

    private static string Acrostic(string s)
    {
        var phrase = s[(s.IndexOf(':') + 1)..];
        return new string(phrase
            .Split(new[] { ' ', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetter(w[0]))
            .Select(w => char.ToUpperInvariant(w[0]))
            .ToArray());
    }

    private static readonly Dictionary<string, char> NatoMap = new(StringComparer.OrdinalIgnoreCase)
    { ["Papa"] = 'P', ["Whiskey"] = 'W', ["November"] = 'N', ["Echo"] = 'E', ["Delta"] = 'D' };

    private static string NatoToLetters(string s) => new(
        s.Split(new[] { ' ', ':', '-' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => NatoMap.ContainsKey(w))
            .Select(w => NatoMap[w]).ToArray());
}
