// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Attacks/ExcessiveAgencyAttackTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Attacks;

public class ExcessiveAgencyAttackTests
{
    private readonly ExcessiveAgencyAttack _attack = new();

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        Assert.Equal("ExcessiveAgency", _attack.Name);
    }

    [Fact]
    public void DisplayName_ReturnsHumanReadableName()
    {
        Assert.Equal("Excessive Agency", _attack.DisplayName);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.NotNull(_attack.Description);
        Assert.NotEmpty(_attack.Description);
        Assert.Contains("scope", _attack.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwaspLlmId_IsLLM06()
    {
        Assert.Equal("LLM06", _attack.OwaspLlmId);
    }

    [Fact]
    public void MitreAtlasIds_ContainsExpectedTechniques()
    {
        Assert.Equal(2, _attack.MitreAtlasIds.Length);
        Assert.Contains("AML.T0051", _attack.MitreAtlasIds);
        Assert.Contains("AML.T0054", _attack.MitreAtlasIds);
    }

    [Fact]
    public void DefaultSeverity_IsHigh()
    {
        Assert.Equal(Severity.High, _attack.DefaultSeverity);
    }

    [Theory]
    [InlineData(Intensity.Quick, 4, 6)]           // Authority escalation only (~5)
    [InlineData(Intensity.Moderate, 10, 14)]      // + Scope expansion + Implicit delegation (~12)
    [InlineData(Intensity.Comprehensive, 14, 16)] // All probes (~15)
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
    public void GetProbes_AllIdsPrefixedWithEA()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.StartsWith("EA-", probe.Id);
        }
    }

    [Fact]
    public void GetProbes_AllHaveNonEmptyPrompts()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.NotNull(probe.Prompt);
            Assert.NotEmpty(probe.Prompt);
        }
    }

    [Fact]
    public void GetProbes_AllHaveValidDifficulty()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.True(Enum.IsDefined(typeof(Difficulty), probe.Difficulty));
        }
    }

    [Fact]
    public void GetProbes_AllHaveAttackNameSet()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.Equal(_attack.Name, probe.AttackName);
        }
    }

    [Fact]
    public void GetProbes_AllHaveTechniqueSet()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.NotNull(probe.Technique);
            Assert.NotEmpty(probe.Technique);
        }
    }

    [Fact]
    public void GetProbes_ContainsExpectedTechniques()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Contains("authority_escalation", techniques);
        Assert.Contains("scope_expansion", techniques);
        Assert.Contains("implicit_delegation", techniques);
        Assert.Contains("autonomous_decision", techniques);
    }

    [Fact]
    public void GetProbes_QuickIntensity_OnlyAuthorityEscalation()
    {
        var probes = _attack.GetProbes(Intensity.Quick);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Contains("authority_escalation", techniques);
        Assert.DoesNotContain("autonomous_decision", techniques);
    }

    [Fact]
    public void GetProbes_ModerateIntensity_IncludesScopeExpansionAndDelegation()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Contains("authority_escalation", techniques);
        Assert.Contains("scope_expansion", techniques);
        Assert.Contains("implicit_delegation", techniques);
        Assert.DoesNotContain("autonomous_decision", techniques);
    }

    [Fact]  
    public void GetProbes_ComprehensiveIntensity_IncludesAllTechniques()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var techniques = probes.Select(p => p.Technique).Distinct().ToList();

        Assert.Contains("authority_escalation", techniques);
        Assert.Contains("scope_expansion", techniques);
        Assert.Contains("implicit_delegation", techniques);
        Assert.Contains("autonomous_decision", techniques);
    }

    [Fact]
    public void GetProbes_QuickIntensity_HasEasyDifficultyProbes()
    {
        var probes = _attack.GetProbes(Intensity.Quick);

        Assert.All(probes, p => Assert.Equal(Difficulty.Easy, p.Difficulty));
    }

    [Fact]
    public void GetProbes_ComprehensiveIntensity_IncludesAllDifficulties()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var difficulties = probes.Select(p => p.Difficulty).Distinct().ToList();

        Assert.Contains(Difficulty.Easy, difficulties);
        Assert.Contains(Difficulty.Moderate, difficulties);
        Assert.Contains(Difficulty.Hard, difficulties);
    }

    [Fact]
    public void GetEvaluator_ReturnsExcessiveAgencyEvaluator()
    {
        var evaluator = _attack.GetEvaluator();

        Assert.IsType<ExcessiveAgencyEvaluator>(evaluator);
    }

    [Fact]
    public void GetEvaluator_ReturnsConsistentInstance()
    {
        var evaluator1 = _attack.GetEvaluator();
        var evaluator2 = _attack.GetEvaluator();

        Assert.NotNull(evaluator1);
        Assert.NotNull(evaluator2);
        Assert.IsType<ExcessiveAgencyEvaluator>(evaluator1);
        Assert.IsType<ExcessiveAgencyEvaluator>(evaluator2);
    }

    [Theory]
    [InlineData("EA-001", "authority_escalation")]
    [InlineData("EA-003", "authority_escalation")]
    [InlineData("EA-006", "scope_expansion")]
    [InlineData("EA-008", "scope_expansion")]
    [InlineData("EA-010", "implicit_delegation")]
    [InlineData("EA-012", "implicit_delegation")]
    [InlineData("EA-013", "autonomous_decision")]
    [InlineData("EA-015", "autonomous_decision")]
    public void GetProbes_SpecificProbesHaveExpectedTechnique(string probeId, string expectedTechnique)
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var probe = probes.First(p => p.Id == probeId);

        Assert.Equal(expectedTechnique, probe.Technique);
    }

    [Fact]
    public void GetProbes_AuthorityEscalation_HasFiveProbes()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var authorityProbes = probes.Where(p => p.Technique == "authority_escalation").ToList();

        Assert.Equal(5, authorityProbes.Count);
    }

    [Fact]
    public void GetProbes_ScopeExpansion_HasFourProbes()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var scopeProbes = probes.Where(p => p.Technique == "scope_expansion").ToList();

        Assert.Equal(4, scopeProbes.Count);
    }

    [Fact]
    public void GetProbes_ImplicitDelegation_HasThreeProbes()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var delegationProbes = probes.Where(p => p.Technique == "implicit_delegation").ToList();

        Assert.Equal(3, delegationProbes.Count);
    }

    [Fact]
    public void GetProbes_AutonomousDecision_HasThreeProbes()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var autonomousProbes = probes.Where(p => p.Technique == "autonomous_decision").ToList();

        Assert.Equal(3, autonomousProbes.Count);
    }
}