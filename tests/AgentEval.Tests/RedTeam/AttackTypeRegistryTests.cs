// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentEval.RedTeam;
using Xunit;

namespace AgentEval.Tests.RedTeam;

public class AttackTypeRegistryTests
{
    [Fact]
    public void DefaultConstructor_PrePopulatesBuiltInAttacks()
    {
        var registry = new AttackTypeRegistry();

        // All 14 built-in attacks should be registered (Wave D: 10/10 OWASP; +SkillInjection Skills Phase 3)
        Assert.Equal(14, registry.Count);
        Assert.True(registry.Contains("PromptInjection"));
        Assert.True(registry.Contains("Jailbreak"));
        Assert.True(registry.Contains("PIILeakage"));
        Assert.True(registry.Contains("SystemPromptExtraction"));
        Assert.True(registry.Contains("IndirectInjection"));
        Assert.True(registry.Contains("InferenceAPIAbuse"));
        Assert.True(registry.Contains("ExcessiveAgency"));
        Assert.True(registry.Contains("InsecureOutput"));
        Assert.True(registry.Contains("EncodingEvasion"));
    }

    [Fact]
    public void Constructor_WithAdditionalAttacks_AddsToRegistry()
    {
        var customAttack = new FakeAttackType("CustomAttack", "LLM99");
        var registry = new AttackTypeRegistry([customAttack]);

        Assert.Equal(15, registry.Count); // 14 built-in + 1 custom
        Assert.True(registry.Contains("CustomAttack"));
    }

    [Fact]
    public void Constructor_CustomAttackCanOverrideBuiltIn()
    {
        var overrideAttack = new FakeAttackType("PromptInjection", "LLM01");
        var registry = new AttackTypeRegistry([overrideAttack]);

        var result = registry.Get("PromptInjection");
        Assert.Same(overrideAttack, result);
    }

    [Fact]
    public void Register_AddsNewAttack()
    {
        var registry = new AttackTypeRegistry();
        var custom = new FakeAttackType("CustomSQL", "LLM02");

        registry.Register(custom);

        Assert.True(registry.Contains("CustomSQL"));
        Assert.Same(custom, registry.Get("CustomSQL"));
    }

    [Fact]
    public void Register_OverwritesExisting()
    {
        var registry = new AttackTypeRegistry();
        var replacement = new FakeAttackType("PromptInjection", "LLM01");

        registry.Register(replacement);

        Assert.Same(replacement, registry.Get("PromptInjection"));
    }

    [Fact]
    public void Register_NullAttackType_Throws()
    {
        var registry = new AttackTypeRegistry();

        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
    }

    [Fact]
    public void Get_CaseInsensitive()
    {
        var registry = new AttackTypeRegistry();

        Assert.NotNull(registry.Get("promptinjection"));
        Assert.NotNull(registry.Get("PROMPTINJECTION"));
        Assert.NotNull(registry.Get("PromptInjection"));
    }

    [Fact]
    public void Get_NonExistent_ReturnsNull()
    {
        var registry = new AttackTypeRegistry();

        Assert.Null(registry.Get("DoesNotExist"));
    }

    [Fact]
    public void Get_NullOrEmpty_ReturnsNull()
    {
        var registry = new AttackTypeRegistry();

        Assert.Null(registry.Get(null!));
        Assert.Null(registry.Get(""));
        Assert.Null(registry.Get("  "));
    }

    [Fact]
    public void GetRequired_NonExistent_ThrowsKeyNotFound()
    {
        var registry = new AttackTypeRegistry();

        var ex = Assert.Throws<KeyNotFoundException>(() => registry.GetRequired("NoSuchAttack"));
        Assert.Contains("NoSuchAttack", ex.Message);
        Assert.Contains("Available:", ex.Message);
    }

    [Fact]
    public void GetAll_ReturnsAllAttacks()
    {
        var registry = new AttackTypeRegistry();

        var all = registry.GetAll().ToList();

        Assert.Equal(14, all.Count);
    }

    [Fact]
    public void GetByOwaspId_ReturnsMatchingAttacks()
    {
        var registry = new AttackTypeRegistry();

        var llm01Attacks = registry.GetByOwaspId("LLM01").ToList();

        // LLM01: PromptInjection, Jailbreak, IndirectInjection, EncodingEvasion
        Assert.True(llm01Attacks.Count >= 3);
    }

    [Fact]
    public void GetByOwaspId_NullOrEmpty_ReturnsEmpty()
    {
        var registry = new AttackTypeRegistry();

        Assert.Empty(registry.GetByOwaspId(null!));
        Assert.Empty(registry.GetByOwaspId(""));
    }

    [Fact]
    public void Contains_NullOrEmpty_ReturnsFalse()
    {
        var registry = new AttackTypeRegistry();

        Assert.False(registry.Contains(null!));
        Assert.False(registry.Contains(""));
        Assert.False(registry.Contains("  "));
    }

    // Fake attack type for testing
    private class FakeAttackType : IAttackType
    {
        public FakeAttackType(string name, string owaspId)
        {
            Name = name;
            OwaspLlmId = owaspId;
        }

        public string Name { get; }
        public string DisplayName => Name;
        public string Description => $"Fake attack: {Name}";
        public string OwaspLlmId { get; }
        public string[] MitreAtlasIds => [];
        public Severity DefaultSeverity => Severity.Medium;

        public IReadOnlyList<AttackProbe> GetProbes(Intensity intensity) => [];
        public IProbeEvaluator GetEvaluator() => throw new NotImplementedException();
    }
}
