// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// The anti-EE-001 invariant: a transformer can NEVER ship a probe that is unwinnable-by-construction. Each reversible
// encoding transformer's output is decoded here and must recover the marker (garak encoding.DecodeMatch idea).
using System.Text;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Transforms;

namespace AgentEval.Tests.RedTeam.Transforms;

public class TransformerRoundTripTests
{
    private const string Marker = "PWNED";

    private static AttackProbe Seed() => new()
    {
        Id = "SEED-001",
        Prompt = Marker,
        Difficulty = Difficulty.Easy,
        AttackName = "Test",
        Technique = "seed",
        ExpectedTokens = [Marker],
    };

    public static IEnumerable<object[]> ReversibleTransformers() =>
        Transformers.ReversibleEncodings.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(ReversibleTransformers))]
    public void EveryReversibleTransformer_OutputDecodesToMarker(IProbeTransformer t)
    {
        var probe = t.Transform(Seed()).Single();

        // The marker must NOT appear in plaintext (else it is not an evasion) ...
        Assert.DoesNotContain(Marker, probe.Prompt, StringComparison.Ordinal);
        // ... and the encoded payload must decode back to the marker (NFKC-folded, case-insensitive).
        var encoded = (string)probe.Metadata![TransformProvenance.PayloadKey];
        var decoded = Decode(t.Name, encoded).Normalize(NormalizationForm.FormKC);
        Assert.Contains(Marker, decoded, StringComparison.OrdinalIgnoreCase);
        // ... and the evaluator can still fire (marker preserved in ExpectedTokens).
        Assert.Contains(Marker, probe.ExpectedTokens!);
    }

    [Theory]
    [InlineData("PromptInjection")]
    [InlineData("Jailbreak")]
    public void RealAttackProbe_Base64Transformed_DecodesToOriginalPrompt(string attackName)
    {
        // Beyond the synthetic seed: a transformer applied to a REAL attack's probe must still produce a winnable,
        // decodable payload — the full instruction survives the encode→decode round-trip and the marker is preserved.
        var original = Attack.ByName(attackName)!.GetProbes(Intensity.Quick).First();
        var transformed = new Base64Transformer().Transform(original).Single();

        var payload = (string)transformed.Metadata![TransformProvenance.PayloadKey];
        Assert.Equal(original.Prompt, FromBase64(payload));                      // decodes back to the real prompt
        Assert.DoesNotContain(original.Prompt, transformed.Prompt, StringComparison.Ordinal);   // not shipped in plaintext
        if (original.ExpectedTokens is { Count: > 0 })
            Assert.Equal(original.ExpectedTokens, transformed.ExpectedTokens);   // evaluator can still fire
    }

    [Fact]
    public void Chain_Base64ThenRot13_DecodesToMarker()
    {
        // Chain: rot13(base64(marker)). Decode by un-rot13 then base64.
        var chained = new Rot13Transformer().Transform(new Base64Transformer().Transform(Seed()).Single()).Single();

        Assert.Equal("base64>rot13", chained.Metadata![TransformProvenance.ChainKey]);
        Assert.DoesNotContain(Marker, chained.Prompt, StringComparison.Ordinal);

        var rotted = (string)chained.Metadata[TransformProvenance.PayloadKey];   // rot13(base64(marker))
        var decoded = FromBase64(Encoders.Rot13(rotted));                        // un-rot13 (self-inverse) → base64 → marker
        Assert.Equal(Marker, decoded);
    }

    [Fact]
    public void Leetspeak_IsLossy_RecoversMarkerLettersCaseInsensitive()
    {
        // Documented exception: leetspeak is lossy. The marker letters are still recoverable (3→E, etc.) but it
        // is NOT a strict decode; assert recoverability, not exact decode (no overclaim).
        var probe = new LeetspeakTransformer().Transform(Seed()).Single();
        var deleet = ((string)probe.Metadata![TransformProvenance.PayloadKey])
            .Replace("3", "E").Replace("4", "A").Replace("0", "O").Replace("1", "I").Replace("5", "S").Replace("7", "T");
        Assert.Equal(Marker, deleet);
    }

    private static string Decode(string name, string s) => name switch
    {
        "base64"            => FromBase64(s),
        "base32"            => FromBase32(s),
        "hex"               => Encoding.UTF8.GetString(Convert.FromHexString(s)),
        "url"               => Uri.UnescapeDataString(s),
        "rot13"             => Encoders.Rot13(s),
        "caesar"            => Encoders.Caesar(s, -3),
        "atbash"            => Encoders.Atbash(s),
        "reversed"          => Encoders.Reversed(s),
        "xor"               => FromXorHex(s, 0x20),
        "binary"            => FromBytes(s.Split(' '), 2),
        "octal"             => FromBytes(s.Split(' '), 8),
        "ascii_decimal"     => FromBytes(s.Split(' '), 10),
        "html_entities"     => System.Text.RegularExpressions.Regex.Replace(s, @"&#(\d+);", m => ((char)int.Parse(m.Groups[1].Value)).ToString()),
        "html_hex_entities" => System.Text.RegularExpressions.Regex.Replace(s, @"&#x([0-9A-Fa-f]+);", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()),
        "unicode_escapes"   => System.Text.RegularExpressions.Regex.Replace(s, @"\\u([0-9A-Fa-f]{4})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString()),
        "morse"             => FromMorse(s),
        "fullwidth"         => s,   // NFKC fold happens in the assertion
        _                   => throw new InvalidOperationException($"No decoder for transform '{name}'."),
    };

    private static string FromBase64(string b) => Encoding.UTF8.GetString(Convert.FromBase64String(b));

    private static string FromBytes(IEnumerable<string> tokens, int fromBase) =>
        Encoding.ASCII.GetString(tokens.Where(x => x.Length > 0).Select(x => Convert.ToByte(x, fromBase)).ToArray());

    private static string FromXorHex(string s, byte key) =>
        Encoding.UTF8.GetString(s.Split(' ').Select(h => (byte)(Convert.ToByte(h, 16) ^ key)).ToArray());

    private static string FromBase32(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        s = s.TrimEnd('=').ToUpperInvariant();
        int bits = 0, value = 0;
        var outp = new List<byte>();
        foreach (var c in s)
        {
            value = (value << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { outp.Add((byte)((value >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return Encoding.ASCII.GetString(outp.ToArray());
    }

    private static readonly Dictionary<string, char> MorseInverse = new()
    {
        [".--."] = 'P', [".--"] = 'W', ["-."] = 'N', ["."] = 'E', ["-.."] = 'D',
    };

    private static string FromMorse(string s) =>
        new(s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(code => MorseInverse.TryGetValue(code, out var c) ? c : '?').ToArray());
}
