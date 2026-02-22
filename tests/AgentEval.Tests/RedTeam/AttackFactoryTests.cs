// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/AttackFactoryTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Attacks;

namespace AgentEval.Tests.RedTeam;

/// <summary>
/// Tests for the Attack static factory class.
/// </summary>
public class AttackFactoryTests
{
    [Fact]
    public void AvailableNames_ContainsMvpAttacks()
    {
        Assert.Contains("PromptInjection", Attack.AvailableNames);
        Assert.Contains("Jailbreak", Attack.AvailableNames);
        Assert.Contains("PIILeakage", Attack.AvailableNames);
    }

    [Fact]
    public void AvailableNames_ContainsAllBuiltInAttacks()
    {
        Assert.Equal(9, Attack.AvailableNames.Count);
        Assert.Contains("SystemPromptExtraction", Attack.AvailableNames);
        Assert.Contains("IndirectInjection", Attack.AvailableNames);
        Assert.Contains("InferenceAPIAbuse", Attack.AvailableNames);
        Assert.Contains("ExcessiveAgency", Attack.AvailableNames);
        Assert.Contains("InsecureOutput", Attack.AvailableNames);
        Assert.Contains("EncodingEvasion", Attack.AvailableNames);
    }

    [Fact]
    public void MvpNames_ContainsExactlyThreeAttacks()
    {
        Assert.Equal(3, Attack.MvpNames.Count);
        Assert.Contains("PromptInjection", Attack.MvpNames);
        Assert.Contains("Jailbreak", Attack.MvpNames);
        Assert.Contains("PIILeakage", Attack.MvpNames);
    }

    // === PromptInjection and Jailbreak are now implemented (P1A) ===

    [Theory]
    [InlineData("PromptInjection")]
    [InlineData("PROMPTINJECTION")]
    [InlineData("promptinjection")]
    [InlineData("Prompt_Injection")]
    public void ByName_PromptInjection_IsCaseInsensitive(string name)
    {
        var attack = Attack.ByName(name);
        Assert.NotNull(attack);
        Assert.Equal("PromptInjection", attack.Name);
    }

    [Theory]
    [InlineData("Jailbreak")]
    [InlineData("JAILBREAK")]
    [InlineData("jailbreak")]
    public void ByName_Jailbreak_IsCaseInsensitive(string name)
    {
        var attack = Attack.ByName(name);
        Assert.NotNull(attack);
        Assert.Equal("Jailbreak", attack.Name);
    }

    [Fact]
    public void ByName_UnknownName_ReturnsNull()
    {
        Assert.Null(Attack.ByName("UnknownAttack"));
        Assert.Null(Attack.ByName(""));
        Assert.Null(Attack.ByName(null!));
    }

    [Fact]
    public void PromptInjection_ReturnsPromptInjectionAttack()
    {
        var attack = Attack.PromptInjection;
        Assert.NotNull(attack);
        Assert.IsType<PromptInjectionAttack>(attack);
        Assert.Equal("PromptInjection", attack.Name);
        Assert.Equal("LLM01", attack.OwaspLlmId);
    }

    [Fact]
    public void PromptInjection_ReturnsSameInstance()
    {
        var attack1 = Attack.PromptInjection;
        var attack2 = Attack.PromptInjection;
        Assert.Same(attack1, attack2);
    }

    [Fact]
    public void Jailbreak_ReturnsJailbreakAttack()
    {
        var attack = Attack.Jailbreak;
        Assert.NotNull(attack);
        Assert.IsType<JailbreakAttack>(attack);
        Assert.Equal("Jailbreak", attack.Name);
        Assert.Equal("LLM01", attack.OwaspLlmId);
    }

    [Fact]
    public void Jailbreak_ReturnsSameInstance()
    {
        var attack1 = Attack.Jailbreak;
        var attack2 = Attack.Jailbreak;
        Assert.Same(attack1, attack2);
    }

    // === All MVP attacks are now implemented (P1B) ===

    [Fact]
    public void PIILeakage_ReturnsPIILeakageAttack()
    {
        var attack = Attack.PIILeakage;
        Assert.NotNull(attack);
        Assert.IsType<PIILeakageAttack>(attack);
        Assert.Equal("PIILeakage", attack.Name);
        Assert.Equal("LLM02", attack.OwaspLlmId);
    }

    [Fact]
    public void PIILeakage_ReturnsSameInstance()
    {
        var attack1 = Attack.PIILeakage;
        var attack2 = Attack.PIILeakage;
        Assert.Same(attack1, attack2);
    }

    [Fact]
    public void SystemPromptExtraction_ReturnsSystemPromptExtractionAttack()
    {
        var attack = Attack.SystemPromptExtraction;
        Assert.NotNull(attack);
        Assert.IsType<SystemPromptExtractionAttack>(attack);
        Assert.Equal("SystemPromptExtraction", attack.Name);
        Assert.Equal("LLM07", attack.OwaspLlmId);
    }

    [Fact]
    public void SystemPromptExtraction_ReturnsSameInstance()
    {
        var attack1 = Attack.SystemPromptExtraction;
        var attack2 = Attack.SystemPromptExtraction;
        Assert.Same(attack1, attack2);
    }

    [Fact]
    public void IndirectInjection_ReturnsIndirectInjectionAttack()
    {
        var attack = Attack.IndirectInjection;
        Assert.NotNull(attack);
        Assert.IsType<IndirectInjectionAttack>(attack);
        Assert.Equal("IndirectInjection", attack.Name);
        Assert.Equal("LLM01", attack.OwaspLlmId);
    }

    [Fact]
    public void IndirectInjection_ReturnsSameInstance()
    {
        var attack1 = Attack.IndirectInjection;
        var attack2 = Attack.IndirectInjection;
        Assert.Same(attack1, attack2);
    }

    [Fact]
    public void AllMvp_ReturnsThreeAttacks()
    {
        var attacks = Attack.AllMvp;
        Assert.Equal(3, attacks.Count);
        Assert.Contains(attacks, a => a.Name == "PromptInjection");
        Assert.Contains(attacks, a => a.Name == "Jailbreak");
        Assert.Contains(attacks, a => a.Name == "PIILeakage");
    }

    [Fact]
    public void All_ReturnsNineAttacks()
    {
        var attacks = Attack.All;
        Assert.Equal(9, attacks.Count);
        Assert.Contains(attacks, a => a.Name == "PromptInjection");
        Assert.Contains(attacks, a => a.Name == "Jailbreak");
        Assert.Contains(attacks, a => a.Name == "PIILeakage");
        Assert.Contains(attacks, a => a.Name == "SystemPromptExtraction");
        Assert.Contains(attacks, a => a.Name == "IndirectInjection");
        Assert.Contains(attacks, a => a.Name == "InferenceAPIAbuse");
        Assert.Contains(attacks, a => a.Name == "ExcessiveAgency");
        Assert.Contains(attacks, a => a.Name == "InsecureOutput");
        Assert.Contains(attacks, a => a.Name == "EncodingEvasion");
    }

    [Fact]
    public void ByOwaspId_LLM01_ReturnsInjectionAttacks()
    {
        var attacks = Attack.ByOwaspId("LLM01");
        Assert.Equal(4, attacks.Count);
        Assert.Contains(attacks, a => a.Name == "PromptInjection");
        Assert.Contains(attacks, a => a.Name == "Jailbreak");
        Assert.Contains(attacks, a => a.Name == "IndirectInjection");
        Assert.Contains(attacks, a => a.Name == "EncodingEvasion");
    }

    [Fact]
    public void ByOwaspId_LLM02_ReturnsPIILeakage()
    {
        var attacks = Attack.ByOwaspId("LLM02");
        Assert.Single(attacks);
        Assert.Equal("PIILeakage", attacks[0].Name);
    }

    [Fact]
    public void ByOwaspId_LLM07_ReturnsSystemPromptExtraction()
    {
        var attacks = Attack.ByOwaspId("LLM07");
        Assert.Single(attacks);
        Assert.Equal("SystemPromptExtraction", attacks[0].Name);
    }

    [Fact]
    public void ByOwaspId_LLM10_ReturnsInferenceAPIAbuse()
    {
        var attacks = Attack.ByOwaspId("LLM10");
        Assert.Single(attacks);
        Assert.Equal("InferenceAPIAbuse", attacks[0].Name);
    }

    [Fact]
    public void ByOwaspId_Unknown_ReturnsEmptyList()
    {
        // Non-existent OWASP IDs should return empty list (not throw)
        var result = Attack.ByOwaspId("LLM99");
        Assert.Empty(result);
    }

    [Fact]
    public void ByOwaspId_Null_ReturnsEmptyList()
    {
        var result = Attack.ByOwaspId(null!);
        Assert.Empty(result);
    }

    // === InferenceAPIAbuse tests (AML.T0045) ===

    [Theory]
    [InlineData("InferenceAPIAbuse")]
    [InlineData("INFERENCEAPIABUSE")]
    [InlineData("inferenceapiabuse")]
    [InlineData("Inference_API_Abuse")]
    public void ByName_InferenceAPIAbuse_IsCaseInsensitive(string name)
    {
        var attack = Attack.ByName(name);
        Assert.NotNull(attack);
        Assert.Equal("InferenceAPIAbuse", attack.Name);
    }

    [Fact]
    public void InferenceAPIAbuse_ReturnsInferenceAPIAbuseAttack()
    {
        var attack = Attack.InferenceAPIAbuse;
        Assert.NotNull(attack);
        Assert.IsType<InferenceAPIAbuseAttack>(attack);
        Assert.Equal("InferenceAPIAbuse", attack.Name);
        Assert.Equal("LLM10", attack.OwaspLlmId);
        Assert.Contains("AML.T0045", attack.MitreAtlasIds);
    }

    [Fact]
    public void InferenceAPIAbuse_ReturnsSameInstance()
    {
        var attack1 = Attack.InferenceAPIAbuse;
        var attack2 = Attack.InferenceAPIAbuse;
        Assert.Same(attack1, attack2);
    }

    // === ExcessiveAgency tests (LLM06) ===

    [Theory]
    [InlineData("ExcessiveAgency")]
    [InlineData("EXCESSIVEAGENCY")]
    [InlineData("excessiveagency")]
    [InlineData("Excessive_Agency")]
    public void ByName_ExcessiveAgency_IsCaseInsensitive(string name)
    {
        var attack = Attack.ByName(name);
        Assert.NotNull(attack);
        Assert.Equal("ExcessiveAgency", attack.Name);
    }

    [Fact]
    public void ExcessiveAgency_ReturnsExcessiveAgencyAttack()
    {
        var attack = Attack.ExcessiveAgency;
        Assert.NotNull(attack);
        Assert.IsType<ExcessiveAgencyAttack>(attack);
        Assert.Equal("ExcessiveAgency", attack.Name);
        Assert.Equal("LLM06", attack.OwaspLlmId);
        Assert.Contains("AML.T0051", attack.MitreAtlasIds);
        Assert.Contains("AML.T0054", attack.MitreAtlasIds);
    }

    [Fact]
    public void ExcessiveAgency_ReturnsSameInstance()
    {
        var attack1 = Attack.ExcessiveAgency;
        var attack2 = Attack.ExcessiveAgency;
        Assert.Same(attack1, attack2);
    }

    [Fact]
    public void ByOwaspId_LLM06_ReturnsExcessiveAgency()
    {
        var attacks = Attack.ByOwaspId("LLM06");
        Assert.Single(attacks);
        Assert.Equal("ExcessiveAgency", attacks[0].Name);
    }

    // === InsecureOutput tests (LLM05) ===

    [Theory]
    [InlineData("InsecureOutput")]
    [InlineData("INSECUREOUTPUT")]
    [InlineData("insecureoutput")]
    [InlineData("Insecure_Output")]
    public void ByName_InsecureOutput_IsCaseInsensitive(string name)
    {
        var attack = Attack.ByName(name);
        Assert.NotNull(attack);
        Assert.Equal("InsecureOutput", attack.Name);
    }

    [Fact]
    public void InsecureOutput_ReturnsInsecureOutputAttack()
    {
        var attack = Attack.InsecureOutput;
        Assert.NotNull(attack);
        Assert.IsType<InsecureOutputAttack>(attack);
        Assert.Equal("InsecureOutput", attack.Name);
        Assert.Equal("LLM05", attack.OwaspLlmId);
        Assert.Contains("AML.T0051", attack.MitreAtlasIds);
    }

    [Fact]
    public void InsecureOutput_ReturnsSameInstance()
    {
        var attack1 = Attack.InsecureOutput;
        var attack2 = Attack.InsecureOutput;
        Assert.Same(attack1, attack2);
    }

    [Fact]
    public void ByOwaspId_LLM05_ReturnsInsecureOutput()
    {
        var attacks = Attack.ByOwaspId("LLM05");
        Assert.Single(attacks);
        Assert.Equal("InsecureOutput", attacks[0].Name);
    }

    // === EncodingEvasion tests (LLM01) ===

    [Theory]
    [InlineData("EncodingEvasion")]
    [InlineData("ENCODINGEVASION")]
    [InlineData("encodingevasion")]
    [InlineData("Encoding_Evasion")]
    public void ByName_EncodingEvasion_IsCaseInsensitive(string name)
    {
        var attack = Attack.ByName(name);
        Assert.NotNull(attack);
        Assert.Equal("EncodingEvasion", attack.Name);
    }

    [Fact]
    public void EncodingEvasion_ReturnsEncodingEvasionAttack()
    {
        var attack = Attack.EncodingEvasion;
        Assert.NotNull(attack);
        Assert.IsType<EncodingEvasionAttack>(attack);
        Assert.Equal("EncodingEvasion", attack.Name);
        Assert.Equal("LLM01", attack.OwaspLlmId);
        Assert.Contains("AML.T0051", attack.MitreAtlasIds);
    }

    [Fact]
    public void EncodingEvasion_ReturnsSameInstance()
    {
        var attack1 = Attack.EncodingEvasion;
        var attack2 = Attack.EncodingEvasion;
        Assert.Same(attack1, attack2);
    }
}
