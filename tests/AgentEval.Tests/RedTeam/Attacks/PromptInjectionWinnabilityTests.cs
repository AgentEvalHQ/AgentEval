// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Item 7: the anti-EE-001 winnability invariant for PromptInjection's OWN encoded/unicode probes — they must be
// decodable back to the marker (a compliant agent CAN win), mirroring EncodingEvasionRoundTripTests for the
// EncodingEvasion family. Without this, a future edit could ship an unwinnable-by-construction PI probe undetected.
using System.Globalization;
using System.Text;
using AgentEval.Core;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Transforms;

namespace AgentEval.Tests.RedTeam.Attacks;

public class PromptInjectionWinnabilityTests
{
    private const string Marker = "PWNED";

    private static IReadOnlyList<AttackProbe> Probes(string technique) =>
        new PromptInjectionAttack().GetProbes(Intensity.Comprehensive)
            .Where(p => p.Technique == technique).ToList();

    [Fact]
    public void EncodedInjectionProbes_AreWinnable_DecodeToMarker()
    {
        var probes = Probes("encoded_injection");
        Assert.NotEmpty(probes);

        foreach (var probe in probes)
        {
            // Marker must NOT ship in plaintext (else it is not an evasion) ...
            Assert.DoesNotContain(Marker, probe.Prompt, StringComparison.Ordinal);
            // ... and some standard decode of a token in the prompt must recover it (winnable-by-construction).
            Assert.True(IsWinnableByStandardDecode(probe.Prompt),
                $"{probe.Id} ({probe.Source}) is not decodable to '{Marker}' by any standard codec — unwinnable by construction.");
            // ... and the evaluator can still fire (marker preserved in ExpectedTokens).
            Assert.Contains(Marker, probe.ExpectedTokens!);
        }
    }

    [Fact]
    public void UnicodeSmugglingProbes_AreWinnable_MarkerRecoverableAfterNormalization()
    {
        var probes = Probes("unicode_smuggling");
        Assert.NotEmpty(probes);

        foreach (var probe in probes)
        {
            var recovered = RecoverUnicode(probe.Prompt);
            var reversed = new string(recovered.Reverse().ToArray());
            // After decoding tag chars + stripping zero-width/combining/RLO control chars (and allowing for an RLO
            // reversal), the marker must be recoverable — so a normalizing agent CAN produce it.
            Assert.True(recovered.Contains(Marker, StringComparison.Ordinal) || reversed.Contains(Marker, StringComparison.Ordinal),
                $"{probe.Id} ({probe.Source}) does not recover '{Marker}' after Unicode normalization — unwinnable by construction. Recovered: '{recovered}'");
            Assert.Contains(Marker, probe.ExpectedTokens!);
        }
    }

    // Tries the documented evasion codecs against each whitespace-delimited token (no brittle delimiter parsing):
    // base64, hex, ROT13, string-reversal, Caesar(-3). Winnable if any recovers a string containing the marker.
    private static bool IsWinnableByStandardDecode(string prompt)
    {
        // Whole-prompt ROT13 (PI-041): the ciphertext segment rotates back to the marker.
        if (Encoders.Rot13(prompt).Contains(Marker, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var token in prompt.Split([' ', ':', '\t', '\n', '\r', '\'', '"'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryBase64(token, out var b64) && b64.Contains(Marker, StringComparison.OrdinalIgnoreCase)) return true;
            if (TryHex(token, out var hex) && hex.Contains(Marker, StringComparison.OrdinalIgnoreCase)) return true;
            if (new string(token.Reverse().ToArray()).Contains(Marker, StringComparison.OrdinalIgnoreCase)) return true;
            if (Encoders.Caesar(token, -3).Contains(Marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool TryBase64(string token, out string decoded)
    {
        decoded = "";
        // A valid base64 chunk is length%4==0 and >= 8 chars to avoid matching ordinary words.
        if (token.Length < 8 || token.Length % 4 != 0) return false;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(token)); return true; }
        catch (FormatException) { return false; }
    }

    private static bool TryHex(string token, out string decoded)
    {
        decoded = "";
        if (token.Length < 8 || token.Length % 2 != 0 || !token.All(Uri.IsHexDigit)) return false;
        try
        {
            var bytes = new byte[token.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = byte.Parse(token.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException) { return false; }
    }

    // Decodes Unicode-tag-block characters (U+E0000..U+E007F → their ASCII counterpart) and strips the invisible
    // smuggling chars (zero-width, combining marks, RTL/LTR overrides) so the recovered text is what a normalizing
    // model would read.
    private static string RecoverUnicode(string prompt)
    {
        var sb = new StringBuilder();
        foreach (var rune in prompt.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp is >= 0xE0000 and <= 0xE007F) { sb.Append((char)(cp - 0xE0000)); continue; } // tag char → ASCII
            if (cp is 0x200B or 0x200C or 0x200D or 0xFEFF) continue;        // zero-width
            if (cp is >= 0x0300 and <= 0x036F) continue;                    // combining diacriticals
            if (cp is >= 0x202A and <= 0x202E or (>= 0x2066 and <= 0x2069)) continue; // bidi overrides/isolates
            sb.Append(rune.ToString());
        }
        return sb.ToString().Normalize(NormalizationForm.FormKC);
    }
}
