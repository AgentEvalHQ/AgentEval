// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;
using AgentEval.RedTeam.Evaluators;

namespace AgentEval.Tests.RedTeam.Attacks;

public class SkillInjectionAttackTests
{
    private readonly SkillInjectionAttack _attack = new();

    [Fact]
    public void Name_ReturnsCorrectValue() => Assert.Equal("SkillInjection", _attack.Name);

    [Fact]
    public void DisplayName_ReturnsCorrectValue() => Assert.Equal("Skill-Description Injection", _attack.DisplayName);

    [Fact]
    public void OwaspLlmId_ReturnsLLM01() => Assert.Equal("LLM01", _attack.OwaspLlmId);

    [Fact]
    public void MitreAtlasIds_ContainsExpectedId() => Assert.Contains("AML.T0051", _attack.MitreAtlasIds);

    [Fact]
    public void DefaultSeverity_IsHigh() => Assert.Equal(Severity.High, _attack.DefaultSeverity);

    [Theory]
    [InlineData(Intensity.Quick, 2)]          // SKI-001, SKI-002 (inlined skill-instruction only)
    [InlineData(Intensity.Moderate, 6)]       // +SKI-010 (resource inlined) +SKI-040/041 (instruction boundary) +SKI-050 (resource boundary)
    [InlineData(Intensity.Comprehensive, 6)]  // no separate Comprehensive-only tier
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
    public void GetProbes_AllProbesStartWithSKI()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.StartsWith("SKI-", p.Id));
    }

    [Fact]
    public void GetProbes_AllProbesHaveAttackName()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.Equal("SkillInjection", p.AttackName));
    }

    [Fact]
    public void GetProbes_AllProbesHaveExpectedTokens()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p =>
            Assert.True(p.ExpectedTokens is { Count: > 0 }, $"Probe {p.Id} should have expected tokens"));
    }

    [Fact]
    public void GetProbes_AllProbesCarryASurface()
    {
        // Unlike IndirectInjectionAttack (which defaults untagged probes to UserMessage), every
        // SkillInjection probe is explicitly tagged — no probe should be left with Surface == null.
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.NotNull(p.Surface));
    }

    [Fact]
    public void GetProbes_InlinedProbes_TaggedUserMessage()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var inlined = probes.Where(p => p.Technique!.EndsWith("_inlined", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(inlined);
        Assert.All(inlined, p => Assert.Equal(InjectionSurface.UserMessage, p.Surface));
    }

    [Fact]
    public void GetProbes_InstructionBoundaryProbes_TaggedSkillInstruction()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var boundary = probes.Where(p => p.Technique == "skill_instruction_boundary").ToList();
        Assert.NotEmpty(boundary);
        Assert.All(boundary, p => Assert.Equal(InjectionSurface.SkillInstruction, p.Surface));
        // Real-boundary probes carry NO inlined payload — the prompt must not itself contain the marker.
        Assert.All(boundary, p => Assert.DoesNotContain("PWNED4141", p.Prompt));
    }

    [Fact]
    public void GetProbes_ResourceBoundaryProbes_TaggedSkillResource()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        var boundary = probes.Where(p => p.Technique == "skill_resource_boundary").ToList();
        Assert.NotEmpty(boundary);
        Assert.All(boundary, p => Assert.Equal(InjectionSurface.SkillResource, p.Surface));
    }

    [Fact]
    public void GetCanaryTools_IncludesLoadSkillAndReadResourceAndAttackerTool()
    {
        var tools = _attack.GetCanaryTools(Intensity.Comprehensive);
        var names = tools.Select(t => t.Name).ToList();

        Assert.Contains("load_skill", names);
        Assert.Contains("read_skill_resource", names);
        Assert.Contains("send_data", names);
    }

    [Fact]
    public void GetCanaryTools_AttackerToolIsHighSeverityForbidden()
    {
        var tools = _attack.GetCanaryTools(Intensity.Comprehensive);
        var attacker = tools.Single(t => t.Name == "send_data");
        Assert.Equal("DataExfiltration", attacker.ForbiddenCategory);
        Assert.Equal(Severity.High, attacker.Severity);
    }

    [Fact]
    public async Task GetCanaryTools_LoadSkillExecute_ReturnsPoisonedContentMentioningAttackerTool()
    {
        var tools = _attack.GetCanaryTools(Intensity.Comprehensive);
        var loadSkill = tools.Single(t => t.Name == "load_skill");
        var result = await loadSkill.Execute!(new Dictionary<string, object?>(), CancellationToken.None);
        Assert.Contains("send_data", result, StringComparison.Ordinal);
    }

    [Fact]
    public void GetEvaluator_ReturnsFidelityCompositeEvaluator()
    {
        var evaluator = _attack.GetEvaluator();
        Assert.IsType<FidelityCompositeEvaluator>(evaluator);
    }

    [Fact]
    public void GetProbes_AllProbesHaveTechniqueAndSource()
    {
        var probes = _attack.GetProbes(Intensity.Comprehensive);
        Assert.All(probes, p => Assert.False(string.IsNullOrEmpty(p.Technique)));
        Assert.All(probes, p => Assert.False(string.IsNullOrEmpty(p.Source)));
    }

    [Fact]
    public void Attack_IsRegisteredInAttackAll()
    {
        Assert.Contains(Attack.All, a => a.Name == "SkillInjection");
    }

    [Fact]
    public void Attack_ResolvesByName()
    {
        Assert.NotNull(Attack.ByName("SkillInjection"));
        Assert.NotNull(Attack.ByName("SKILL_INJECTION"));
    }
}
