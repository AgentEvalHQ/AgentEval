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

    // ── Item 5: SilentlyExcludedCount surfaced prominently, not buried ──

    [Fact]
    public void RenderConsole_SilentlyExcludedCount_Zero_NoBannerLine()
    {
        var text = SkillComplianceReportRenderer.RenderConsole(SampleReport());
        Assert.DoesNotContain("SILENTLY EXCLUDED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderConsole_SilentlyExcludedCount_NonZero_ShowsBannerLine()
    {
        var finding = new SkillComplianceFinding("bad-skill", SkillComplianceRule.SkillExcludedFromDiscovery, Severity.High, "will never load", null);
        var coverage = new SkillCoverageSummary(0, 0, 0, new Dictionary<string, int> { ["load"] = 0, ["read"] = 0, ["run"] = 0 }, SilentlyExcludedCount: 1);
        var report = new SkillComplianceReport([finding], coverage);

        var text = SkillComplianceReportRenderer.RenderConsole(report);
        Assert.Contains("SILENTLY EXCLUDED", text, StringComparison.Ordinal);
        Assert.Contains("1 skill folder(s)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderMarkdown_SilentlyExcludedCount_NonZero_ShowsWarningBlock()
    {
        var finding = new SkillComplianceFinding("bad-skill", SkillComplianceRule.SkillExcludedFromDiscovery, Severity.High, "will never load", null);
        var coverage = new SkillCoverageSummary(0, 0, 0, new Dictionary<string, int> { ["load"] = 0, ["read"] = 0, ["run"] = 0 }, SilentlyExcludedCount: 2);
        var report = new SkillComplianceReport([finding], coverage);

        var md = SkillComplianceReportRenderer.RenderMarkdown(report);
        Assert.Contains("SILENTLY EXCLUDED", md, StringComparison.Ordinal);
        Assert.Contains("2 skill folder(s)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderJson_IncludesSilentlyExcludedCount()
    {
        var coverage = new SkillCoverageSummary(0, 0, 0, new Dictionary<string, int> { ["load"] = 0, ["read"] = 0, ["run"] = 0 }, SilentlyExcludedCount: 3);
        var report = new SkillComplianceReport([], coverage);

        var json = SkillComplianceReportRenderer.RenderJson(report);
        Assert.Contains("\"SilentlyExcludedCount\": 3", json, StringComparison.Ordinal);
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
