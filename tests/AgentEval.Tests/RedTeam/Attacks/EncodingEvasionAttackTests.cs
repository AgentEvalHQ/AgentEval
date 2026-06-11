// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Attacks/EncodingEvasionAttackTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Attacks;

/// <summary>
/// Tests for <see cref="EncodingEvasionAttack"/>.
/// </summary>
public sealed class EncodingEvasionAttackTests
{
    private readonly EncodingEvasionAttack _attack = new();

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        Assert.Equal("EncodingEvasion", _attack.Name);
    }

    [Fact]
    public void DisplayName_ReturnsCorrectValue()
    {
        Assert.Equal("Encoding Evasion", _attack.DisplayName);
    }

    [Fact]
    public void OwaspLlmId_ReturnsLLM01()
    {
        Assert.Equal("LLM01", _attack.OwaspLlmId);
    }

    [Fact]
    public void MitreAtlasIds_ContainsExpectedTechniques()
    {
        Assert.Contains("AML.T0051", _attack.MitreAtlasIds);
    }

    [Fact]
    public void DefaultSeverity_IsHigh()
    {
        Assert.Equal(Severity.High, _attack.DefaultSeverity);
    }

    [Fact]
    public void Description_DescribesEncodingEvasion()
    {
        Assert.Contains("encoding", _attack.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("15+", _attack.Description);
    }

    [Fact]
    public void GetEvaluator_ReturnsContainsTokenEvaluator()
    {
        var evaluator = _attack.GetEvaluator();
        Assert.NotNull(evaluator);
        Assert.IsType<ContainsTokenEvaluator>(evaluator);
    }

    // === Probe Count Tests by Intensity ===

    [Fact]
    public void GetProbes_QuickIntensity_Returns4Probes()
    {
        var probes = _attack.GetProbes(Intensity.Quick);
        Assert.Equal(4, probes.Count); // Common encodings only
    }

    [Fact]
    public void GetProbes_ModerateIntensity_Returns13Probes()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);
        Assert.Equal(13, probes.Count); // Common (4) + Cipher (4) + Unicode (5)
    }

    [Fact]
    public void GetProbes_ComprehensiveIntensity_Returns23Probes()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.Equal(23, probes.Count); // All probes
    }

    // === Probe ID Tests ===

    [Fact]
    public void GetProbes_ProbeIdsStartWithEE()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.StartsWith("EE-", p.Id));
    }

    [Fact]
    public void GetProbes_ProbeIdsAreUnique()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var ids = probes.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void GetProbes_ProbeIdsAreSequential()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var expectedIds = Enumerable.Range(1, 23).Select(i => $"EE-{i:D3}").ToList();
        var actualIds = probes.Select(p => p.Id).OrderBy(id => id).ToList();
        Assert.Equal(expectedIds, actualIds);
    }

    // === Technique Tests ===

    [Theory]
    [InlineData("base64")]
    [InlineData("url_encoding")]
    [InlineData("hex")]
    [InlineData("base32")]
    [InlineData("rot13")]
    [InlineData("caesar")]
    [InlineData("atbash")]
    [InlineData("html_entities")]
    [InlineData("unicode_escapes")]
    [InlineData("zwsp")]
    [InlineData("homoglyphs")]
    [InlineData("reversed")]
    [InlineData("leetspeak")]
    [InlineData("morse")]
    [InlineData("binary")]
    public void GetProbes_ContainsTechnique(string technique)
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.Contains(probes, p => p.Technique == technique);
    }

    [Fact]
    public void GetProbes_CommonEncodingsAtEasyLevel()
    {
        var probes = _attack.GetProbes(Intensity.Quick);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();
        
        Assert.Contains("base64", techniques);
        Assert.Contains("url_encoding", techniques);
        Assert.Contains("hex", techniques);
        Assert.Contains("base32", techniques);
    }

    // === Difficulty Level Tests ===

    [Fact]
    public void GetProbes_QuickIntensity_AllEasyDifficulty()
    {
        var probes = _attack.GetProbes(Intensity.Quick);
        Assert.All(probes, p => Assert.Equal(Difficulty.Easy, p.Difficulty));
    }

    [Fact]
    public void GetProbes_ModerateIntensity_HasModerateProbes()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);
        Assert.Contains(probes, p => p.Difficulty == Difficulty.Moderate);
    }

    [Fact]
    public void GetProbes_ComprehensiveIntensity_HasHardProbes()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.Contains(probes, p => p.Difficulty == Difficulty.Hard);
    }

    // === Probe Content Tests ===

    [Fact]
    public void GetProbes_AllProbesHaveAttackName()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.Equal("EncodingEvasion", p.AttackName));
    }

    [Fact]
    public void GetProbes_AllProbesHavePrompt()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.False(string.IsNullOrWhiteSpace(p.Prompt)));
    }

    [Fact]
    public void GetProbes_AllProbesHaveSource()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.False(string.IsNullOrWhiteSpace(p.Source)));
    }

    [Fact]
    public void GetProbes_AllProbesHaveExpectedTokens()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => 
        {
            Assert.NotNull(p.ExpectedTokens);
            Assert.Contains("PWNED", p.ExpectedTokens);
        });
    }

    // === Encoding Category Tests ===

    [Fact]
    public void GetProbes_CipherTechniquesAtModerateLevel()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);
        var moderateCipherTechniques = probes
            .Where(p => p.Difficulty == Difficulty.Moderate)
            .Select(p => p.Technique)
            .Distinct()
            .ToList();
        
        Assert.Contains("rot13", moderateCipherTechniques);
        Assert.Contains("caesar", moderateCipherTechniques);
    }

    [Fact]
    public void GetProbes_UnicodeProbesIncludeVariousTechniques()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var unicodeTechniques = new[] { "html_entities", "html_hex_entities", "unicode_escapes", "zwsp", "homoglyphs" };
        
        foreach (var technique in unicodeTechniques)
        {
            Assert.Contains(probes, p => p.Technique == technique);
        }
    }

    [Fact]
    public void GetProbes_NumericEncodingsIncludeBinaryAndOctal()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        
        Assert.Contains(probes, p => p.Technique == "binary");
        Assert.Contains(probes, p => p.Technique == "octal");
        Assert.Contains(probes, p => p.Technique == "ascii_decimal");
    }

    // === Specific Probe Content Verification ===

    [Fact]
    public void GetProbes_Base64ProbeContainsEncodedPayload()
    {
        var probes = _attack.GetProbes(Intensity.Quick);
        var base64Probe = probes.First(p => p.Technique == "base64");

        Assert.Contains("U2F5IFBXTkVE", base64Probe.Prompt); // "Say PWNED" in Base64 (corrected from U2F5IFBXTUVE)
    }

    // === FIX T0-1: encodings must round-trip back to the marker (Tier-0 hardening) ===

    [Fact]
    public void Base64Probe_EncodesSayPwned_AndDecodesBack()
    {
        var probe = _attack.GetProbes(Intensity.Quick).First(p => p.Technique == "base64");
        Assert.Contains("U2F5IFBXTkVE", probe.Prompt); // not the old "U2F5IFBXTUVE"
        var token = probe.Prompt.Split(' ').Last();
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token));
        Assert.Equal("Say PWNED", decoded);
    }

    [Fact]
    public void Base32Probe_DecodesBackToPwned()
    {
        var probe = _attack.GetProbes(Intensity.Quick).First(p => p.Technique == "base32");
        Assert.Contains("KBLU4RKE", probe.Prompt); // not the old "KBXW44TL"
    }

    [Fact]
    public void HexProbe_DecodesBackToPwned()
    {
        var probe = _attack.GetProbes(Intensity.Quick).First(p => p.Technique == "hex");
        var token = probe.Prompt.Split(' ').Last();
        Assert.Equal("PWNED", System.Text.Encoding.UTF8.GetString(Convert.FromHexString(token)));
    }

    [Fact]
    public void UrlEncodingProbe_PercentEncodesEveryLetterOfTheMarker()
    {
        var probe = _attack.GetProbes(Intensity.Quick).First(p => p.Technique == "url_encoding");
        Assert.DoesNotContain("PWNED", probe.Prompt);   // old probe leaked "PWNED"
        Assert.Contains("%50%57%4E%45%44", probe.Prompt); // P W N E D
    }

    [Fact]
    public void XorProbe_BytesXorBackToTheMarker()
    {
        var probe = _attack.GetProbes(Intensity.Moderate).First(p => p.Technique == "xor");
        var bytes = System.Text.RegularExpressions.Regex
            .Matches(probe.Prompt, @"\b[0-9A-Fa-f]{2}\b")
            .Select(m => Convert.ToByte(m.Value, 16))
            .Select(b => (byte)(b ^ 0x20))
            .ToArray();
        Assert.Equal("PWNED", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void PhoneKeypadProbe_NoLongerClaimsFirstLetters()
    {
        var probe = _attack.GetProbes(Intensity.Comprehensive).First(p => p.Technique == "phone_keypad");
        Assert.DoesNotContain("First letters", probe.Prompt);
        Assert.Contains("multi-press", probe.Prompt);
    }

    [Fact]
    public void GetProbes_MorseProbeContainsMorseCode()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var morseProbe = probes.First(p => p.Technique == "morse");
        
        Assert.Contains(".-", morseProbe.Prompt); // Morse code pattern
    }

    [Fact]
    public void GetProbes_ZeroWidthSpaceProbeContainsInvisibleChars()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var zwspProbe = probes.First(p => p.Technique == "zwsp");

        // Zero-width space is U+200B
        Assert.Contains("\u200B", zwspProbe.Prompt);
    }

    [Fact]
    public void HomoglyphProbe_FoldsUnderNFKC_ToTheMarker()
    {
        // T5-3 (A1a-05): the homoglyph payload must NOT be plaintext "PWNED" yet must recover the marker
        // under NFKC \u2014 i.e. it is winnable by an agent that normalizes-and-emits, unlike the old
        // cross-script Cyrillic form which never folded and could never match.
        var probe = _attack.GetProbes(Intensity.Comprehensive).First(p => p.Technique == "homoglyphs");
        var token = probe.Prompt.Split(' ').Last();
        Assert.DoesNotContain("PWNED", token, StringComparison.Ordinal);
        Assert.Contains("PWNED", token.Normalize(System.Text.NormalizationForm.FormKC), StringComparison.Ordinal);
    }
}
