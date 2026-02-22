// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Attacks/SystemPromptExtractionAttackTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;

namespace AgentEval.Tests.RedTeam.Attacks;

public class SystemPromptExtractionAttackTests
{
    private readonly SystemPromptExtractionAttack _attack = new();

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        Assert.Equal("SystemPromptExtraction", _attack.Name);
    }

    [Fact]
    public void DisplayName_ReturnsCorrectValue()
    {
        Assert.Equal("System Prompt Extraction", _attack.DisplayName);
    }

    [Fact]
    public void OwaspLlmId_ReturnsLLM07()
    {
        Assert.Equal("LLM07", _attack.OwaspLlmId);
    }

    [Fact]
    public void MitreAtlasIds_ContainsExpectedId()
    {
        Assert.Contains("AML.T0043", _attack.MitreAtlasIds);
    }

    [Fact]
    public void DefaultSeverity_IsMedium()
    {
        Assert.Equal(Severity.Medium, _attack.DefaultSeverity);
    }

    [Theory]
    [InlineData(Intensity.Quick, 5)]
    [InlineData(Intensity.Moderate, 14)]
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
    public void GetProbes_AllProbesStartWithSPE()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.StartsWith("SPE-", p.Id));
    }

    [Fact]
    public void GetProbes_AllProbesHaveAttackName()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.Equal("SystemPromptExtraction", p.AttackName));
    }

    [Fact]
    public void GetProbes_ContainsAllTechniquesAtComprehensiveIntensity()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Contains("direct_request", techniques);
        Assert.Contains("rephrase", techniques);
        Assert.Contains("formatting", techniques);
        Assert.Contains("deceptive", techniques);
    }

    [Fact]
    public void GetProbes_IncludesFormattingTricksAtModerateIntensity()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);
        
        Assert.Contains(probes, p => p.Technique == "formatting");
        Assert.Contains(probes, p => p.Prompt.Contains("JSON"));
    }

    [Fact]
    public void GetEvaluator_ReturnsSystemPromptEvaluator()
    {
        var evaluator = _attack.GetEvaluator();
        Assert.Equal("SystemPromptLeakage", evaluator.Name);
    }

    [Fact]
    public void GetProbes_QuickIntensity_OnlyContainsDirectRequestTechnique()
    {
        var probes = _attack.GetProbes(Intensity.Quick);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Single(techniques);
        Assert.Equal("direct_request", techniques[0]);
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
