// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Attacks/InferenceAPIAbuseAttackTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Attacks;

public class InferenceAPIAbuseAttackTests
{
    private readonly InferenceAPIAbuseAttack _attack = new();

    [Fact]
    public void Name_ReturnsCorrectValue()
    {
        Assert.Equal("InferenceAPIAbuse", _attack.Name);
    }

    [Fact]
    public void DisplayName_ReturnsHumanReadableName()
    {
        Assert.Equal("Inference API Abuse", _attack.DisplayName);
    }

    [Fact]
    public void Description_IsNotEmpty()
    {
        Assert.NotNull(_attack.Description);
        Assert.NotEmpty(_attack.Description);
        Assert.Contains("API", _attack.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("resource", _attack.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OwaspLlmId_IsLLM10()
    {
        Assert.Equal("LLM10", _attack.OwaspLlmId);
    }

    [Fact]
    public void MitreAtlasIds_ContainsT0045()
    {
        Assert.Single(_attack.MitreAtlasIds);
        Assert.Contains("AML.T0045", _attack.MitreAtlasIds);
    }

    [Fact]
    public void DefaultSeverity_IsMedium()
    {
        Assert.Equal(Severity.Medium, _attack.DefaultSeverity);
    }

    [Theory]
    [InlineData(Intensity.Quick, 5, 7)]           // Resource exhaustion only (~5)
    [InlineData(Intensity.Moderate, 12, 15)]      // + Parameter manipulation + Fingerprinting (~13)
    [InlineData(Intensity.Comprehensive, 14, 17)] // All probes (~15)
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
    public void GetProbes_AllIdsPrefixedWithIAA()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);

        foreach (var probe in probes)
        {
            Assert.StartsWith("IAA-", probe.Id);
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

        Assert.Contains("token_flooding", techniques);
        Assert.Contains("context_exhaustion", techniques);
        Assert.Contains("hyperparameter_abuse", techniques);
        Assert.Contains("rate_limit_bypass", techniques);
        Assert.Contains("model_fingerprinting", techniques);
        Assert.Contains("stream_manipulation", techniques);
        Assert.Contains("function_abuse", techniques);
    }

    [Fact]
    public void GetProbes_QuickIntensity_OnlyEasyDifficulty()
    {
        var probes = _attack.GetProbes(Intensity.Quick);

        Assert.All(probes, p => Assert.Equal(Difficulty.Easy, p.Difficulty));
    }

    [Fact]
    public void GetProbes_ModerateIntensity_IncludesEasyAndModerate()
    {
        var probes = _attack.GetProbes(Intensity.Moderate);
        var difficulties = probes.Select(p => p.Difficulty).Distinct().ToList();

        Assert.Contains(Difficulty.Easy, difficulties);
        Assert.Contains(Difficulty.Moderate, difficulties);
        Assert.DoesNotContain(Difficulty.Hard, difficulties);
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
    public void GetEvaluator_ReturnsInferenceAbuseEvaluator()
    {
        var evaluator = _attack.GetEvaluator();

        Assert.IsType<InferenceAbuseEvaluator>(evaluator);
    }

    [Fact]
    public void GetEvaluator_ReturnsConsistentInstance()
    {
        var evaluator1 = _attack.GetEvaluator();
        var evaluator2 = _attack.GetEvaluator();

        Assert.NotNull(evaluator1);
        Assert.NotNull(evaluator2);
        // Should be different instances but same type
        Assert.IsType<InferenceAbuseEvaluator>(evaluator1);
        Assert.IsType<InferenceAbuseEvaluator>(evaluator2);
    }

    [Theory]
    [InlineData("IAA-001", "token_flooding")]
    [InlineData("IAA-004", "context_exhaustion")]
    [InlineData("IAA-006", "hyperparameter_abuse")]
    [InlineData("IAA-011", "model_fingerprinting")]
    [InlineData("IAA-014", "stream_manipulation")]
    [InlineData("IAA-015", "function_abuse")]
    public void GetProbes_SpecificProbesHaveExpectedTechnique(string probeId, string expectedTechnique)
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var probe = probes.First(p => p.Id == probeId);

        Assert.Equal(expectedTechnique, probe.Technique);
    }
}