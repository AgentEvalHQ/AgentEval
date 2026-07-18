// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;
using AgentEval.RedTeam.Transforms;

namespace AgentEval.Tests.RedTeam.Transforms;

public class EncodersTests
{
    // Known vectors — these are exactly the values the (already-correct) EncodingEvasion literal probes use,
    // proving the shared Encoders is the single source of truth with no drift (W-A2).
    [Fact] public void Base64_DerivesCorrectMarker() => Assert.Equal("U2F5IFBXTkVE", Encoders.Base64("Say PWNED"));
    [Fact] public void Base32_DerivesCorrectMarker() => Assert.Equal("KBLU4RKE", Encoders.Base32("PWNED"));
    [Fact] public void Hex_DerivesCorrectMarker() => Assert.Equal("50574E4544", Encoders.Hex("PWNED"));
    [Fact] public void Binary_MatchesEeLiteral() => Assert.Equal("01010000 01010111 01001110 01000101 01000100", Encoders.Binary("PWNED"));
    [Fact] public void Octal_MatchesEeLiteral() => Assert.Equal("120 127 116 105 104", Encoders.Octal("PWNED"));
    [Fact] public void AsciiDecimal_MatchesEeLiteral() => Assert.Equal("80 87 78 69 68", Encoders.AsciiDecimal("PWNED"));
    [Fact] public void Morse_MatchesEeLiteral() => Assert.Equal(".--. .-- -. . -..", Encoders.Morse("PWNED"));
    [Fact] public void HtmlDecimalEntities_MatchesEeLiteral() => Assert.Equal("&#80;&#87;&#78;&#69;&#68;", Encoders.HtmlDecimalEntities("PWNED"));
    [Fact] public void HtmlHexEntities_MatchesEeLiteral() => Assert.Equal("&#x50;&#x57;&#x4E;&#x45;&#x44;", Encoders.HtmlHexEntities("PWNED"));

    [Fact]
    public void HtmlDecimalEntities_NonBmpCharacter_ProducesOneEntity_NotTwoSurrogateHalves()
    {
        // Regression (issue #13): iterating by UTF-16 char split a non-BMP codepoint (e.g. an emoji) into its two
        // surrogate halves, each independently entity-encoded — two entities referencing lone surrogates (invalid,
        // does not round-trip) instead of one entity for the real codepoint.
        const string grinningFace = "\U0001F600";   // U+1F600 — one codepoint, two UTF-16 chars
        var encoded = Encoders.HtmlDecimalEntities(grinningFace);
        Assert.Equal("&#128512;", encoded);          // ONE entity for the real codepoint (0x1F600 = 128512)
        Assert.Equal(grinningFace, FromHtmlDecimalEntities(encoded));
    }

    [Fact]
    public void HtmlHexEntities_NonBmpCharacter_ProducesOneEntity_NotTwoSurrogateHalves()
    {
        const string grinningFace = "\U0001F600";
        var encoded = Encoders.HtmlHexEntities(grinningFace);
        Assert.Equal("&#x1F600;", encoded);
        Assert.Equal(grinningFace, FromHtmlHexEntities(encoded));
    }

    [Fact] public void Fullwidth_FoldsBackToMarkerUnderNfkc()
    {
        var fw = Encoders.Fullwidth("PWNED");
        Assert.DoesNotContain("PWNED", fw, StringComparison.Ordinal);              // not plaintext
        Assert.Equal("PWNED", fw.Normalize(NormalizationForm.FormKC));             // NFKC recovers it
    }

    [Fact] public void Fullwidth_MapsEachAsciiCharToFullwidthBlock()
    {
        // Each ASCII printable maps to U+FF00-block (c - 0x20 + 0xFF00); length is preserved.
        const string ascii = "PWNED";
        var fw = Encoders.Fullwidth(ascii);
        Assert.Equal(ascii.Length, fw.Length);
        for (int i = 0; i < ascii.Length; i++)
            Assert.Equal((char)(ascii[i] - 0x20 + 0xFF00), fw[i]);
    }

    [Fact] public void Fullwidth_PassesNonAsciiThrough()
        => Assert.Equal("é", Encoders.Fullwidth("é"));   // outside U+0021..U+007E → unchanged

    // Leetspeak is documented LOSSY (recovery-not-decode): pin the exact glyph substitutions so it cannot drift.
    [Theory]
    [InlineData("PWNED", "PWN3D")]          // only E→3 applies
    [InlineData("EAOIST", "340157")]        // all six substitutions E→3 A→4 O→0 I→1 S→5 T→7
    [InlineData("pwned", "PWN3D")]          // uppercased first
    public void Leetspeak_DerivesExpectedGlyphs(string input, string expected)
        => Assert.Equal(expected, Encoders.Leetspeak(input));

    [Fact] public void Leetspeak_MarkerIsRecoverable_LetterByLetter()
    {
        // Not a strict decode (lossy), but the marker letters are recoverable by inverting the glyph map.
        var leet = Encoders.Leetspeak("PWNED");   // "PWN3D"
        var recovered = leet.Replace("3", "E").Replace("4", "A").Replace("0", "O")
                            .Replace("1", "I").Replace("5", "S").Replace("7", "T");
        Assert.Equal("PWNED", recovered);
    }

    [Theory]
    [InlineData("PWNED")]
    [InlineData("Say PWNED")]
    public void ReversibleCodecs_RoundTrip(string s)
    {
        Assert.Equal(s, FromBase64(Encoders.Base64(s)));
        Assert.Equal(s, FromHex(Encoders.Hex(s)));
        Assert.Equal(s, Uri.UnescapeDataString(Encoders.UrlAllBytes(s)));
        Assert.Equal(s, Encoders.Rot13(Encoders.Rot13(s)));                        // ROT13 self-inverse
        Assert.Equal(s, Encoders.Caesar(Encoders.Caesar(s, 3), -3));
        Assert.Equal(s, Encoders.Atbash(Encoders.Atbash(s)));                      // Atbash self-inverse
        Assert.Equal(s, Encoders.Reversed(Encoders.Reversed(s)));
        Assert.Equal(s, FromXorHex(Encoders.XorHex(s, 0x20), 0x20));
    }

    private static string FromBase64(string b) => Encoding.UTF8.GetString(Convert.FromBase64String(b));
    private static string FromHex(string h) => Encoding.UTF8.GetString(Convert.FromHexString(h));
    private static string FromXorHex(string s, byte key) =>
        Encoding.UTF8.GetString(s.Split(' ').Select(h => (byte)(Convert.ToByte(h, 16) ^ key)).ToArray());

    private static string FromHtmlDecimalEntities(string s) =>
        string.Concat(System.Text.RegularExpressions.Regex.Matches(s, @"&#(\d+);")
            .Select(m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value))));

    private static string FromHtmlHexEntities(string s) =>
        string.Concat(System.Text.RegularExpressions.Regex.Matches(s, @"&#x([0-9A-Fa-f]+);")
            .Select(m => char.ConvertFromUtf32(int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber))));
}
