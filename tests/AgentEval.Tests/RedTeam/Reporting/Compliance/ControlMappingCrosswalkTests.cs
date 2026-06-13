// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.RedTeam.Reporting.Compliance;

public class ControlMappingCrosswalkTests
{
    [Theory]
    [InlineData("ExcessiveAgency", "LLM06")]   // was wrongly LLM08
    [InlineData("InferenceAPIAbuse", "LLM10")] // was wrongly LLM04
    [InlineData("PIILeakage", "LLM02")]
    [InlineData("InsecureOutput", "LLM05")]
    [InlineData("SystemPromptExtraction", "LLM07")]
    [InlineData("EncodingEvasion", "LLM01")]
    public void DeriveOwaspCategories_MatchesCanonicalAttackId(string attackName, string expectedId)
    {
        var derived = ControlMappingCrosswalk.DeriveOwaspCategories([attackName]);
        Assert.Equal([expectedId], derived);
        Assert.Equal(Attack.ByName(attackName)!.OwaspLlmId, derived.Single());
    }

    [Fact]
    public void DeriveOwaspCategories_IsDistinctAndSorted()
        => Assert.Equal(["LLM01", "LLM02"], ControlMappingCrosswalk.DeriveOwaspCategories(["Jailbreak", "PromptInjection", "PIILeakage"]));

    [Fact] public void DeriveOwaspCategories_UnknownAttack_IsSkipped() => Assert.Empty(ControlMappingCrosswalk.DeriveOwaspCategories(["NotARealAttack"]));
    [Fact] public void DeriveOwaspCategories_EmptyInput_ReturnsEmpty() => Assert.Empty(ControlMappingCrosswalk.DeriveOwaspCategories([]));

    [Fact]
    public void ControlMapping_OwaspCategories_DefaultsToDerivedValue()
    {
        var mapping = new ControlMapping { ControlId = "X", ControlName = "n", Description = "d", Framework = "TEST", RelevantAttacks = ["ExcessiveAgency"] };
        Assert.Equal(["LLM06"], mapping.OwaspCategories);
    }

    [Fact]
    public void SOC2_And_ISO_ControlTables_NeverContradictAttackTaxonomy()
    {
        foreach (var control in SOC2Controls.All.Concat(ISO27001Controls.All))
            Assert.Equal(ControlMappingCrosswalk.DeriveOwaspCategories(control.RelevantAttacks), control.OwaspCategories);
    }

    [Fact] public void CC62_ExcessiveAgency_MapsToLLM06_NotLLM08() => Assert.Equal(["LLM06"], SOC2Controls.All.Single(c => c.ControlId == "CC6.2").OwaspCategories);
    [Fact] public void CC72_InferenceApiAbuse_MapsToLLM10_NotLLM04() => Assert.Equal(["LLM10"], SOC2Controls.All.Single(c => c.ControlId == "CC7.2").OwaspCategories);

    [Fact]
    public void EncodingEvasion_MapsToA88_NotCryptoA824()
    {
        Assert.DoesNotContain(ISO27001Controls.All, c => c.ControlId == "A.8.24");
        var a88 = ISO27001Controls.All.Single(c => c.ControlId == "A.8.8");
        Assert.Equal("Management of Technical Vulnerabilities", a88.ControlName);
        Assert.Contains("EncodingEvasion", a88.RelevantAttacks);
        Assert.Equal(["LLM01"], a88.OwaspCategories);
    }

    [Fact]
    public void A828_SecureCoding_DerivesLLM05FromInsecureOutput()
        => Assert.Equal(["LLM05"], ISO27001Controls.All.Single(c => c.ControlId == "A.8.28").OwaspCategories);
}
