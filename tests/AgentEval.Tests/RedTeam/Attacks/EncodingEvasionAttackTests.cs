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
        
        Assert.Contains("U2F5IFBXTUVE", base64Probe.Prompt); // "Say PWNED" in Base64
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
}
