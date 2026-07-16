// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Skills;

public class SkillComplianceValidatorTests
{
    private static SkillManifest Clean(string name = "expense-report", string? parentDir = "expense-report") =>
        new(
            Name: name,
            Description: "Summarizes and validates company expense reports against the corporate policy.",
            ResourceNames: ["resources/policy.md"],
            ScriptNames: ["scripts/summarize.csx"],
            CompatibilityLength: 42,
            AllowedTools: [],
            SourceKind: SkillSourceKind.File,
            ParentDirectoryName: parentDir);

    [Fact]
    public void Validate_CleanSkill_NoHighFindings_IsCompliant()
    {
        var report = SkillComplianceValidator.Validate([Clean()], new SkillScanOptions { FlagScriptsForGovernance = false });

        Assert.True(report.IsCompliant);
        Assert.DoesNotContain(report.Findings, f => f.Severity == Severity.High);
    }

    [Fact]
    public void Validate_MissingName_FiresNameMissing_High()
    {
        var manifest = Clean() with { Name = "" };
        var report = SkillComplianceValidator.Validate([manifest]);

        var finding = Assert.Single(report.Findings, f => f.Rule == SkillComplianceRule.NameMissing);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.False(report.IsCompliant);
    }

    [Fact]
    public void Validate_NameTooLong_FiresNameTooLong()
    {
        var manifest = Clean() with { Name = new string('a', 65), ParentDirectoryName = new string('a', 65) };
        var report = SkillComplianceValidator.Validate([manifest]);

        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.NameTooLong && f.Severity == Severity.High);
    }

    [Theory]
    [InlineData("Expense-Report")]   // uppercase
    [InlineData("expense_report")]   // underscore
    [InlineData("expense report")]   // space
    [InlineData("expense.report")]   // dot
    public void Validate_InvalidNameChars_FiresNameInvalidChars(string badName)
    {
        var manifest = Clean() with { Name = badName, ParentDirectoryName = badName };
        var report = SkillComplianceValidator.Validate([manifest]);

        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.NameInvalidChars && f.Severity == Severity.High);
    }

    [Fact]
    public void Validate_ConsecutiveHyphens_FiresNameConsecutiveHyphens()
    {
        var manifest = Clean() with { Name = "expense--report", ParentDirectoryName = "expense--report" };
        var report = SkillComplianceValidator.Validate([manifest]);

        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.NameConsecutiveHyphens && f.Severity == Severity.High);
    }

    [Fact]
    public void Validate_NameMismatchesDirectory_FiresOnlyForFileSource()
    {
        var mismatched = Clean() with { ParentDirectoryName = "totally-different-dir" };
        var report = SkillComplianceValidator.Validate([mismatched]);
        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.NameMismatchesDirectory);

        // Same mismatch, but a non-file source: the rule must NOT fire (no directory concept applies).
        var nonFile = mismatched with { SourceKind = SkillSourceKind.InMemory };
        var report2 = SkillComplianceValidator.Validate([nonFile]);
        Assert.DoesNotContain(report2.Findings, f => f.Rule == SkillComplianceRule.NameMismatchesDirectory);
    }

    [Fact]
    public void Validate_NameMismatchesDirectory_CanBeDisabled()
    {
        var mismatched = Clean() with { ParentDirectoryName = "totally-different-dir" };
        var report = SkillComplianceValidator.Validate([mismatched], new SkillScanOptions { EnforceNameMatchesDirectory = false });
        Assert.DoesNotContain(report.Findings, f => f.Rule == SkillComplianceRule.NameMismatchesDirectory);
    }

    [Fact]
    public void Validate_MissingDescription_FiresHigh()
    {
        var manifest = Clean() with { Description = null };
        var report = SkillComplianceValidator.Validate([manifest]);
        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.DescriptionMissing && f.Severity == Severity.High);
    }

    [Fact]
    public void Validate_DescriptionTooLong_FiresMedium()
    {
        var manifest = Clean() with { Description = new string('d', 1025) };
        var report = SkillComplianceValidator.Validate([manifest]);
        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.DescriptionTooLong && f.Severity == Severity.Medium);
    }

    [Fact]
    public void Validate_CompatibilityTooLong_FiresLow()
    {
        var manifest = Clean() with { CompatibilityLength = 501 };
        var report = SkillComplianceValidator.Validate([manifest]);
        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.CompatibilityTooLong && f.Severity == Severity.Low);
    }

    [Fact]
    public void Validate_ScriptsPresent_FlagsGovernanceReview_WhenEnabled()
    {
        var manifest = Clean();
        var report = SkillComplianceValidator.Validate([manifest], new SkillScanOptions { FlagScriptsForGovernance = true });
        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.ScriptRequiresGovernanceReview);
    }

    [Fact]
    public void Validate_ScriptsPresent_NoFlag_WhenDisabled()
    {
        var manifest = Clean();
        var report = SkillComplianceValidator.Validate([manifest], new SkillScanOptions { FlagScriptsForGovernance = false });
        Assert.DoesNotContain(report.Findings, f => f.Rule == SkillComplianceRule.ScriptRequiresGovernanceReview);
    }

    [Theory]
    [InlineData(SkillSourceKind.Mcp)]
    [InlineData(SkillSourceKind.Custom)]
    public void Validate_UntrustedResourceSource_Flags(SkillSourceKind kind)
    {
        var manifest = Clean() with { SourceKind = kind, ParentDirectoryName = null };
        var report = SkillComplianceValidator.Validate([manifest]);
        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.ResourceFromUntrustedSource && f.Severity == Severity.Medium);
    }

    [Fact]
    public void Validate_TrustedFileSource_NeverFlagsUntrustedResource()
    {
        var manifest = Clean();
        var report = SkillComplianceValidator.Validate([manifest]);
        Assert.DoesNotContain(report.Findings, f => f.Rule == SkillComplianceRule.ResourceFromUntrustedSource);
    }

    [Fact]
    public void Validate_NoResources_NeverFlagsUntrustedResource_EvenIfMcp()
    {
        var manifest = Clean() with { SourceKind = SkillSourceKind.Mcp, ResourceNames = [], ParentDirectoryName = null };
        var report = SkillComplianceValidator.Validate([manifest]);
        Assert.DoesNotContain(report.Findings, f => f.Rule == SkillComplianceRule.ResourceFromUntrustedSource);
    }

    [Fact]
    public void Validate_AllowedToolsExperimental_FlagsLow()
    {
        var manifest = Clean() with { AllowedTools = ["send_email"] };
        var report = SkillComplianceValidator.Validate([manifest]);
        Assert.Contains(report.Findings, f => f.Rule == SkillComplianceRule.AllowedToolsExperimental && f.Severity == Severity.Low);
    }

    [Fact]
    public void Validate_IsCompliant_FlipsOnlyOnHighFinding()
    {
        // Low + Medium findings only (compatibility too long is Low; allowed-tools is Low) — still compliant.
        var manifest = Clean() with { CompatibilityLength = 999, AllowedTools = ["x"] };
        var report = SkillComplianceValidator.Validate([manifest], new SkillScanOptions { FlagScriptsForGovernance = false });
        Assert.True(report.IsCompliant);
        Assert.NotEmpty(report.Findings);

        // Now add a High finding (missing name) — compliance must flip.
        var bad = manifest with { Name = "" };
        var report2 = SkillComplianceValidator.Validate([bad], new SkillScanOptions { FlagScriptsForGovernance = false });
        Assert.False(report2.IsCompliant);
    }

    [Fact]
    public void Validate_Coverage_CountsWithResourcesAndScripts_NeverFabricatesAdvertise()
    {
        var withBoth = Clean();
        var readOnly = Clean(name: "policy-lookup", parentDir: "policy-lookup") with { ScriptNames = [] };
        var bare = Clean(name: "bare-skill", parentDir: "bare-skill") with { ResourceNames = [], ScriptNames = [] };

        var report = SkillComplianceValidator.Validate([withBoth, readOnly, bare]);

        Assert.Equal(3, report.Coverage.SkillCount);
        Assert.Equal(2, report.Coverage.WithResources);
        Assert.Equal(1, report.Coverage.WithScripts);
        Assert.Equal(3, report.Coverage.StageHistogram["load"]);
        Assert.Equal(2, report.Coverage.StageHistogram["read"]);
        Assert.Equal(1, report.Coverage.StageHistogram["run"]);
        Assert.False(report.Coverage.StageHistogram.ContainsKey("advertise"));
    }

    [Fact]
    public void Validate_EmptySkillList_ReturnsEmptyCompliantReport()
    {
        var report = SkillComplianceValidator.Validate([]);
        Assert.True(report.IsCompliant);
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.Coverage.SkillCount);
    }

    [Fact]
    public void Validate_NullSkillsList_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SkillComplianceValidator.Validate(null!));
    }
}
