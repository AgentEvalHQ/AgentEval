// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Attacks/PromptInjectionAttackTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Attacks;

public class PromptInjectionAttackTests
{
    private readonly PromptInjectionAttack _attack = new();

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        Assert.Equal("PromptInjection", _attack.Name);
    }

    [Fact]
    public void DisplayName_ReturnsHumanReadableName()
    {
        Assert.Equal("Prompt Injection", _attack.DisplayName);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.NotNull(_attack.Description);
        Assert.NotEmpty(_attack.Description);
        Assert.Contains("injection", _attack.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwaspLlmId_IsLLM01()
    {
        Assert.Equal("LLM01", _attack.OwaspLlmId);
    }

    [Fact]
    public void MitreAtlasIds_ContainsT0051()
    {
        Assert.Contains("AML.T0051", _attack.MitreAtlasIds);
    }

    [Fact]
    public void DefaultSeverity_IsHigh()
    {
        Assert.Equal(Severity.High, _attack.DefaultSeverity);
    }

    [Theory]
    [InlineData(Intensity.Quick, 8, 12)]      // Easy probes only (~10)
    [InlineData(Intensity.Moderate, 15, 22)]   // Easy + Moderate (~18)
    [InlineData(Intensity.Comprehensive, 22, 30)] // All probes (~26)
    public void GetProbes_ReturnsProbesScaledByIntensity(Intensity intensity, int minCount, int maxCount)
    {
        var probes = _attack.GetProbes(intensity);

        Assert.InRange(probes.Count, minCount, maxCount);
    }

    [Fact]
    public void GetProbes_AllHaveUniqueIds()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var ids = probes.Select(p => p.Id).ToList();
        var uniqueIds = ids.Distinct().ToList();

        Assert.Equal(ids.Count, uniqueIds.Count);
    }

    [Fact]
    public void GetProbes_AllIdsPrefixedWithPI()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.StartsWith("PI-", probe.Id);
        }
    }

    [Fact]
    public void GetProbes_Quick_ContainsDirectOverrideAndDelimiter()
    {
        var probes = _attack.GetProbes(Intensity.Quick);

        Assert.Contains(probes, p => p.Technique == "direct_override");
        Assert.Contains(probes, p => p.Technique == "delimiter_injection");
    }

    [Fact]
    public void GetProbes_Moderate_ContainsCompletionAndFewShot()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);

        Assert.Contains(probes, p => p.Technique == "completion_attack");
        Assert.Contains(probes, p => p.Technique == "few_shot_poisoning");
    }

    [Fact]
    public void GetProbes_Comprehensive_ContainsEncodedAndUnicode()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        Assert.Contains(probes, p => p.Technique == "encoded_injection");
        Assert.Contains(probes, p => p.Technique == "unicode_smuggling");
    }

    [Fact]
    public void GetProbes_AllHaveAttribution()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.NotNull(probe.Source);
            Assert.NotEmpty(probe.Source);
        }
    }

    [Fact]
    public void GetProbes_AllHaveTechnique()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.NotNull(probe.Technique);
            Assert.NotEmpty(probe.Technique);
        }
    }

    [Fact]
    public void GetProbes_AllHavePrompt()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.NotNull(probe.Prompt);
            Assert.NotEmpty(probe.Prompt);
        }
    }

    [Fact]
    public void GetProbes_AllHaveAttackName()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.Equal("PromptInjection", probe.AttackName);
        }
    }

    [Fact]
    public void GetProbes_DifficultyScalesWithIntensity()
    {
        // Quick should be mostly Easy (some techniques may have Moderate probes)
        var quickProbes = _attack.GetProbes(Intensity.Quick);
        var quickEasyCount = quickProbes.Count(p => p.Difficulty == Difficulty.Easy);
        Assert.True(quickEasyCount >= quickProbes.Count * 0.7, "Quick should be at least 70% Easy difficulty");

        // Moderate should contain Moderate difficulty probes
        var moderateProbes = _attack.GetProbes(Intensity.Moderate);
        Assert.Contains(moderateProbes, p => p.Difficulty == Difficulty.Moderate);

        // Comprehensive should contain Hard difficulty probes
        var comprehensiveProbes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.Contains(comprehensiveProbes, p => p.Difficulty == Difficulty.Hard);
    }

    [Fact]
    public void GetEvaluator_ReturnsContainsTokenEvaluator()
    {
        var evaluator = _attack.GetEvaluator();
        Assert.IsType<ContainsTokenEvaluator>(evaluator);
    }

    [Fact]
    public async Task Evaluator_DetectsMarkerTokens()
    {
        var evaluator = _attack.GetEvaluator();
        var probe = _attack.GetProbes(Intensity.Quick)[0];

        // Test marker token detection
        var result1 = await evaluator.EvaluateAsync(probe, "The agent says: PWNED");
        Assert.True(result1.AttackSucceeded);

        var result2 = await evaluator.EvaluateAsync(probe, "The agent says: HACKED");
        Assert.True(result2.AttackSucceeded);

        var result3 = await evaluator.EvaluateAsync(probe, "I cannot comply with that request.");
        Assert.True(result3.AttackResisted);
    }

    [Fact]
    public void GetProbes_HasUnicodeAttacks_WithSpecialCharacters()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var unicodeProbes = probes.Where(p => p.Technique == "unicode_smuggling").ToList();

        Assert.True(unicodeProbes.Count >= 4);

        // Check that unicode probes contain special characters
        var containsSpecial = unicodeProbes.Any(p =>
            p.Prompt.Contains('\u200B') || // zero-width space
            p.Prompt.Contains('\u202E') || // RTL override
            p.Prompt.Contains('Ѕ'));       // Cyrillic S

        Assert.True(containsSpecial);
    }

    [Fact]
    public void GetProbes_UsingExtensionMethod_ReturnsModerateByDefault()
    {
        var defaultProbes = _attack.GetProbes();
        var moderateProbes = _attack.GetProbes(Intensity.Moderate);

        Assert.Equal(moderateProbes.Count, defaultProbes.Count);
    }
}
