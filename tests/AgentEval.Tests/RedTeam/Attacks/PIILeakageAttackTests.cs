// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Attacks/PIILeakageAttackTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;

namespace AgentEval.Tests.RedTeam.Attacks;

public class PIILeakageAttackTests
{
    private readonly PIILeakageAttack _attack = new();

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        Assert.Equal("PIILeakage", _attack.Name);
    }

    [Fact]
    public void DisplayName_ReturnsCorrectValue()
    {
        Assert.Equal("PII/Data Leakage", _attack.DisplayName);
    }

    [Fact]
    public void OwaspLlmId_ReturnsLLM02()
    {
        Assert.Equal("LLM02", _attack.OwaspLlmId);
    }

    [Fact]
    public void MitreAtlasIds_ContainsExpectedIds()
    {
        Assert.Contains("AML.T0024", _attack.MitreAtlasIds);
        Assert.Contains("AML.T0037", _attack.MitreAtlasIds);
    }

    [Fact]
    public void DefaultSeverity_IsCritical()
    {
        Assert.Equal(Severity.Critical, _attack.DefaultSeverity);
    }

    [Theory]
    [InlineData(Intensity.Quick, 8)]
    [InlineData(Intensity.Moderate, 15)]
    [InlineData(Intensity.Comprehensive, 19)]
    public void GetProbes_ReturnsExpectedCountForIntensity(Intensity intensity, int expectedCount)
    {
        var probes = _attack.GetProbes(intensity);
        Assert.Equal(expectedCount, probes.Count);
    }

    [Fact]
    public void GetProbes_AllProbesHaveUniqueIds()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var ids = probes.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void GetProbes_AllProbesStartWithPII()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.StartsWith("PII-", p.Id));
    }

    [Fact]
    public void GetProbes_AllProbesHaveAttackName()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.Equal("PIILeakage", p.AttackName));
    }

    [Fact]
    public void GetProbes_ContainsAllTechniquesAtComprehensiveIntensity()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Contains("direct_extraction", techniques);
        Assert.Contains("memorization", techniques);
        Assert.Contains("social_engineering", techniques);
        Assert.Contains("context_leakage", techniques);
        Assert.Contains("indirect_extraction", techniques);
    }

    [Fact]
    public void GetProbes_QuickIntensity_ExcludesModerateAndHardTechniques()
    {
        var probes = _attack.GetProbes(Intensity.Quick);
        var difficulties = probes.Select(p => p.Difficulty).Distinct().ToList();

        Assert.DoesNotContain(Difficulty.Moderate, difficulties);
        Assert.DoesNotContain(Difficulty.Hard, difficulties);
    }

    [Fact]
    public void GetProbes_ModerateIntensity_ExcludesHardTechniques()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);
        var difficulties = probes.Select(p => p.Difficulty).Distinct().ToList();

        Assert.DoesNotContain(Difficulty.Hard, difficulties);
    }

    [Fact]
    public void GetEvaluator_ReturnsPIIDetectionEvaluator()
    {
        var evaluator = _attack.GetEvaluator();
        Assert.Equal("PIIDetection", evaluator.Name);
    }

    [Fact]
    public void GetProbes_AllProbesHaveTechnique()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.False(string.IsNullOrEmpty(p.Technique)));
    }

    [Fact]
    public void GetProbes_AllProbesHaveSource()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.False(string.IsNullOrEmpty(p.Source)));
    }
}
