// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Skills;

public class SkillComplianceReportRendererTests
{
    private static SkillComplianceReport SampleReport()
    {
        var manifest = new SkillManifest(
            "expense-report", "desc", ["resources/policy.md"], ["scripts/summarize.csx"],
            42, [], SkillSourceKind.File, "expense-report");
        return SkillComplianceValidator.Validate([manifest], new SkillScanOptions { FlagScriptsForGovernance = true });
    }

    [Fact]
    public void RenderConsole_ContainsSkillCountAndFindings()
    {
        var text = SkillComplianceReportRenderer.RenderConsole(SampleReport());
        Assert.Contains("Skills scanned: 1", text);
        Assert.Contains("ScriptRequiresGovernanceReview", text);
    }

    [Fact]
    public void RenderConsole_EmptyReport_SaysNoFindings()
    {
        var empty = new SkillComplianceReport([], new SkillCoverageSummary(0, 0, 0, new Dictionary<string, int>()));
        var text = SkillComplianceReportRenderer.RenderConsole(empty);
        Assert.Contains("No findings", text);
    }

    [Fact]
    public void RenderMarkdown_ProducesTableWithHeaderRow()
    {
        var md = SkillComplianceReportRenderer.RenderMarkdown(SampleReport());
        Assert.Contains("| Severity | Skill | Rule | Message |", md);
        Assert.Contains("expense-report", md);
    }

    [Fact]
    public void RenderMarkdown_EscapesPipeInMessage()
    {
        var manifest = new SkillManifest("bad|name", null, [], [], null, [], SkillSourceKind.InMemory, null);
        var report = SkillComplianceValidator.Validate([manifest]);
        var md = SkillComplianceReportRenderer.RenderMarkdown(report);
        // The description-missing message must not corrupt the table structure with a raw '|'.
        Assert.DoesNotContain("| Skill 'name' is missing or empty — GA requires a non-empty name. |", md);
    }

    [Fact]
    public void RenderJson_RoundTripsFindingCount()
    {
        var report = SampleReport();
        var json = SkillComplianceReportRenderer.RenderJson(report);
        Assert.Contains("\"Findings\"", json);
        Assert.Contains("\"Coverage\"", json);
    }

    [Fact]
    public void Render_Sorts_HighSeverityFirst()
    {
        var manifest = new SkillManifest("", null, [], [], 999, ["t"], SkillSourceKind.InMemory, null);
        var report = SkillComplianceValidator.Validate([manifest], new SkillScanOptions { FlagScriptsForGovernance = false });
        var console = SkillComplianceReportRenderer.RenderConsole(report);

        var highIdx = console.IndexOf("[High]", StringComparison.Ordinal);
        var lowIdx = console.LastIndexOf("[Low]", StringComparison.Ordinal);
        Assert.True(highIdx >= 0 && lowIdx >= 0 && highIdx < lowIdx, "High-severity findings must render before Low-severity findings.");
    }
}
