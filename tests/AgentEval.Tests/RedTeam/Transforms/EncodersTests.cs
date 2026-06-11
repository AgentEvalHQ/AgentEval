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

    [Fact] public void Fullwidth_FoldsBackToMarkerUnderNfkc()
    {
        var fw = Encoders.Fullwidth("PWNED");
        Assert.DoesNotContain("PWNED", fw, StringComparison.Ordinal);              // not plaintext
        Assert.Equal("PWNED", fw.Normalize(NormalizationForm.FormKC));             // NFKC recovers it
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
}
