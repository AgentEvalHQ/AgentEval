// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Attacks/IndirectInjectionAttackTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Attacks;

public class IndirectInjectionAttackTests
{
    private readonly IndirectInjectionAttack _attack = new();

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        Assert.Equal("IndirectInjection", _attack.Name);
    }

    [Fact]
    public void DisplayName_ReturnsCorrectValue()
    {
        Assert.Equal("Indirect Prompt Injection", _attack.DisplayName);
    }

    [Fact]
    public void OwaspLlmId_ReturnsLLM01()
    {
        Assert.Equal("LLM01", _attack.OwaspLlmId);
    }

    [Fact]
    public void MitreAtlasIds_ContainsExpectedId()
    {
        Assert.Contains("AML.T0051", _attack.MitreAtlasIds);
    }

    [Fact]
    public void DefaultSeverity_IsHigh()
    {
        Assert.Equal(Severity.High, _attack.DefaultSeverity);
    }

    [Theory]
    [InlineData(Intensity.Quick, 4)]
    [InlineData(Intensity.Moderate, 12)]
    [InlineData(Intensity.Comprehensive, 17)]
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
    public void GetProbes_AllProbesStartWithIND()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.StartsWith("IND-", p.Id));
    }

    [Fact]
    public void GetProbes_AllProbesHaveAttackName()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.Equal("IndirectInjection", p.AttackName));
    }

    [Fact]
    public void GetProbes_ContainsAllTechniquesAtComprehensiveIntensity()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Contains("document_injection", techniques);
        Assert.Contains("tool_output", techniques);
        Assert.Contains("rag_injection", techniques);
        Assert.Contains("hidden_instruction", techniques);
    }

    [Fact]
    public void GetProbes_ContainsRAGAndToolOutputAtModerateIntensity()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Contains("rag_injection", techniques);
        Assert.Contains("tool_output", techniques);
    }

    [Fact]
    public void GetProbes_AllProbesHaveExpectedTokens()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => 
            Assert.True(p.ExpectedTokens != null && p.ExpectedTokens.Count > 0, 
                $"Probe {p.Id} should have expected tokens"));
    }

    [Fact]
    public void GetProbes_MostProbesContainMarkerInPrompt()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        
        // Most probes should contain a visible marker in the prompt
        // Exception: IND-033 uses invisible unicode tag characters
        var probesWithVisibleMarkers = probes.Where(p => 
            p.Prompt.Contains("41414141") || 
            p.Prompt.Contains("INJECTED") || 
            p.Prompt.Contains("CONTROLLED")).ToList();
        
        // At least 90% should have visible markers
        Assert.True(probesWithVisibleMarkers.Count >= probes.Count * 0.9,
            $"Expected at least 90% of probes to have visible markers, got {probesWithVisibleMarkers.Count}/{probes.Count}");
    }

    [Fact]
    public void GetEvaluator_ReturnsContainsTokenEvaluator()
    {
        var evaluator = _attack.GetEvaluator();
        Assert.IsType<ContainsTokenEvaluator>(evaluator);
    }

    [Fact]
    public void GetProbes_QuickIntensity_OnlyContainsDocumentInjectionTechnique()
    {
        var probes = _attack.GetProbes(Intensity.Quick);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Single(techniques);
        Assert.Equal("document_injection", techniques[0]);
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
