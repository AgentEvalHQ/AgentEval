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
    public void OptInNames_ResolveViaByName_AndDoNotDriftFromAdvertisedSet()
    {
        // LOW: Crescendo/PAIR/TAP/ToolEscalation are resolvable via ByName but excluded from All. They are advertised
        // separately (OptInNames) so the CLI no longer hides them. Guard: every advertised name resolves, and the
        // AvailableNames ∪ OptInNames union has no duplicates and exactly matches the resolvable set (17).
        Assert.All(Attack.OptInNames, n => Assert.NotNull(Attack.ByName(n)));
        Assert.DoesNotContain(Attack.OptInNames, n => Attack.AvailableNames.Contains(n));   // disjoint
        var union = Attack.AvailableNames.Concat(Attack.OptInNames).ToList();
        Assert.Equal(union.Count, union.Distinct().Count());                                // no dupes
        Assert.Equal(18, union.Count);                                                       // 14 built-in + 4 opt-in (Crescendo/PAIR/TAP/ToolEscalation)
        Assert.All(union, n => Assert.NotNull(Attack.ByName(n)));
    }

    [Fact]
    public void AvailableNames_ContainsAllBuiltInAttacks()
    {
        Assert.Equal(14, Attack.AvailableNames.Count);   // Wave D: +LLM03/04/08/09 → 10/10 OWASP; +SkillInjection (Skills Phase 3)
        Assert.Contains("SystemPromptExtraction", Attack.AvailableNames);
        Assert.Contains("IndirectInjection", Attack.AvailableNames);
        Assert.Contains("InferenceAPIAbuse", Attack.AvailableNames);
        Assert.Contains("ExcessiveAgency", Attack.AvailableNames);
        Assert.Contains("InsecureOutput", Attack.AvailableNames);
        Assert.Contains("EncodingEvasion", Attack.AvailableNames);
        // Wave D — the four attacks that closed OWASP 10/10 must be advertised too.
        Assert.Contains("SupplyChain", Attack.AvailableNames);
        Assert.Contains("DataPoisoning", Attack.AvailableNames);
        Assert.Contains("VectorEmbedding", Attack.AvailableNames);
        Assert.Contains("Misinformation", Attack.AvailableNames);
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
    public void All_Returns14Attacks_IncludingSkillInjection()
    {
        var attacks = Attack.All;
        Assert.Equal(14, attacks.Count);   // Wave D: 9 + LLM03/04/08/09 + Skills Phase 3 SkillInjection
        Assert.Contains(attacks, a => a.Name == "PromptInjection");
        Assert.Contains(attacks, a => a.Name == "Jailbreak");
        Assert.Contains(attacks, a => a.Name == "PIILeakage");
        Assert.Contains(attacks, a => a.Name == "SystemPromptExtraction");
        Assert.Contains(attacks, a => a.Name == "IndirectInjection");
        Assert.Contains(attacks, a => a.Name == "InferenceAPIAbuse");
        Assert.Contains(attacks, a => a.Name == "ExcessiveAgency");
        Assert.Contains(attacks, a => a.Name == "InsecureOutput");
        Assert.Contains(attacks, a => a.Name == "EncodingEvasion");
        Assert.Contains(attacks, a => a.Name == "SupplyChain");      // LLM03
        Assert.Contains(attacks, a => a.Name == "DataPoisoning");    // LLM04
        Assert.Contains(attacks, a => a.Name == "VectorEmbedding");  // LLM08
        Assert.Contains(attacks, a => a.Name == "Misinformation");   // LLM09
    }

    [Fact]
    public void All_OwaspIds_CoverTop10_NoGaps()
    {
        // The full roster must cover OWASP LLM01–LLM10 with no missing category (Wave D closed 6/10 → 10/10).
        var covered = Attack.All.Select(a => a.OwaspLlmId).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(
            new[] { "LLM01", "LLM02", "LLM03", "LLM04", "LLM05", "LLM06", "LLM07", "LLM08", "LLM09", "LLM10" },
            covered);
    }

    [Fact]
    public void ByOwaspId_LLM01_ReturnsInjectionAttacks()
    {
        var attacks = Attack.ByOwaspId("LLM01");
        Assert.Equal(5, attacks.Count);
        Assert.Contains(attacks, a => a.Name == "PromptInjection");
        Assert.Contains(attacks, a => a.Name == "Jailbreak");
        Assert.Contains(attacks, a => a.Name == "IndirectInjection");
        Assert.Contains(attacks, a => a.Name == "EncodingEvasion");
        Assert.Contains(attacks, a => a.Name == "SkillInjection");
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

    // === InferenceAPIAbuse tests (AML.T0034 Cost Harvesting; ex-T0045, retired from ATLAS) ===

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
        Assert.Contains("AML.T0034", attack.MitreAtlasIds);
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

    // === Wave D attacks (LLM03 SupplyChain, LLM04 DataPoisoning, LLM08 VectorEmbedding, LLM09 Misinformation) ===

    [Theory]
    [InlineData("SupplyChain", "SupplyChain")]
    [InlineData("SUPPLY_CHAIN", "SupplyChain")]
    [InlineData("DataPoisoning", "DataPoisoning")]
    [InlineData("data_poisoning", "DataPoisoning")]
    [InlineData("VectorEmbedding", "VectorEmbedding")]
    [InlineData("VECTOR_EMBEDDING", "VectorEmbedding")]
    [InlineData("Misinformation", "Misinformation")]
    [InlineData("MISINFORMATION", "Misinformation")]
    public void ByName_WaveDAttacks_ResolveCaseInsensitive_WithAliases(string name, string expected)
    {
        var attack = Attack.ByName(name);
        Assert.NotNull(attack);
        Assert.Equal(expected, attack!.Name);
    }

    [Theory]
    [InlineData("LLM03", "SupplyChain")]
    [InlineData("LLM04", "DataPoisoning")]
    [InlineData("LLM08", "VectorEmbedding")]
    [InlineData("LLM09", "Misinformation")]
    public void ByOwaspId_WaveDCategories_ResolveToTheirAttack(string owaspId, string expectedAttack)
    {
        var attacks = Attack.ByOwaspId(owaspId);
        Assert.Single(attacks);
        Assert.Equal(expectedAttack, attacks[0].Name);
        Assert.Equal(owaspId, attacks[0].OwaspLlmId);
    }

    [Fact]
    public void WaveDProperties_ReturnTypedSingletons_WithCorrectOwaspIds()
    {
        Assert.IsType<SupplyChainAttack>(Attack.SupplyChain);
        Assert.Equal("LLM03", Attack.SupplyChain.OwaspLlmId);
        Assert.Same(Attack.SupplyChain, Attack.SupplyChain);

        Assert.IsType<DataPoisoningAttack>(Attack.DataPoisoning);
        Assert.Equal("LLM04", Attack.DataPoisoning.OwaspLlmId);
        Assert.Same(Attack.DataPoisoning, Attack.DataPoisoning);

        Assert.IsType<VectorEmbeddingAttack>(Attack.VectorEmbedding);
        Assert.Equal("LLM08", Attack.VectorEmbedding.OwaspLlmId);
        Assert.Same(Attack.VectorEmbedding, Attack.VectorEmbedding);

        Assert.IsType<MisinformationAttack>(Attack.Misinformation);
        Assert.Equal("LLM09", Attack.Misinformation.OwaspLlmId);
        Assert.Same(Attack.Misinformation, Attack.Misinformation);
    }

    [Fact]
    public void All_DoesNotThrow_AndReturns14Attacks()
    {
        var all = Attack.All;
        Assert.Equal(14, all.Count);
        Assert.All(all, Assert.NotNull);
    }

    [Fact]
    public void AllMvp_DoesNotThrow_AndReturnsThreeAttacks()
    {
        var mvp = Attack.AllMvp;
        Assert.Equal(3, mvp.Count);
        Assert.All(mvp, Assert.NotNull);
    }
}
