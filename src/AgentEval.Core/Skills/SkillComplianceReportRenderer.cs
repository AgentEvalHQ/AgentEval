// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;

namespace AgentEval.Skills;

/// <summary>
/// Renders a <see cref="SkillComplianceReport"/> to console text, Markdown, or JSON — severity-sorted
/// findings plus a coverage table, mirroring the repo's existing reporter conventions (e.g. the RedTeam
/// compliance reporters). No new dependency: <see cref="System.Text.Json"/> only.
/// </summary>
public static class SkillComplianceReportRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Renders a plain-text console report (severity-sorted findings + a coverage summary).</summary>
    public static string RenderConsole(SkillComplianceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("Skill Compliance Report");
        sb.AppendLine("========================");
        sb.AppendLine($"Skills scanned: {report.Coverage.SkillCount} | with resources: {report.Coverage.WithResources} | with scripts: {report.Coverage.WithScripts}");
        sb.AppendLine($"Compliant: {(report.IsCompliant ? "YES" : "NO")} ({report.Findings.Count} finding(s))");
        if (report.Coverage.SilentlyExcludedCount > 0)
        {
            sb.AppendLine($"!! SILENTLY EXCLUDED BY MAF: {report.Coverage.SilentlyExcludedCount} skill folder(s) on disk will NEVER load — see SkillExcludedFromDiscovery findings below.");
        }

        sb.AppendLine();

        if (report.Findings.Count == 0)
        {
            sb.AppendLine("No findings — every scanned skill is clean.");
        }
        else
        {
            foreach (var f in SortedBySeverity(report.Findings))
            {
                sb.AppendLine($"[{f.Severity}] {f.SkillName} — {f.Rule}: {f.Message}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Stage reachability (static — 'advertise' is not a tool call, never counted):");
        foreach (var kv in report.Coverage.StageHistogram)
        {
            sb.AppendLine($"  {kv.Key}: {kv.Value}");
        }

        return sb.ToString();
    }

    /// <summary>Renders a Markdown report (severity-sorted findings table + a coverage table).</summary>
    public static string RenderMarkdown(SkillComplianceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine("# Skill Compliance Report");
        sb.AppendLine();
        sb.AppendLine($"**Skills scanned:** {report.Coverage.SkillCount} · **with resources:** {report.Coverage.WithResources} · **with scripts:** {report.Coverage.WithScripts}");
        sb.AppendLine();
        sb.AppendLine($"**Compliant:** {(report.IsCompliant ? "✅ YES" : "❌ NO")} ({report.Findings.Count} finding(s))");
        sb.AppendLine();
        if (report.Coverage.SilentlyExcludedCount > 0)
        {
            sb.AppendLine($"> ⚠️ **SILENTLY EXCLUDED BY MAF:** {report.Coverage.SilentlyExcludedCount} skill folder(s) on disk will NEVER load — see `SkillExcludedFromDiscovery` findings below.");
            sb.AppendLine();
        }

        sb.AppendLine("## Coverage");
        sb.AppendLine();
        sb.AppendLine("| Stage | Reachable skills |");
        sb.AppendLine("|---|---:|");
        foreach (var kv in report.Coverage.StageHistogram)
        {
            sb.AppendLine($"| {kv.Key} | {kv.Value} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Findings");
        sb.AppendLine();

        if (report.Findings.Count == 0)
        {
            sb.AppendLine("No findings — every scanned skill is clean.");
            return sb.ToString();
        }

        sb.AppendLine("| Severity | Skill | Rule | Message |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var f in SortedBySeverity(report.Findings))
        {
            var escapedMessage = f.Message.Replace("|", "\\|", StringComparison.Ordinal);
            sb.AppendLine($"| {f.Severity} | {f.SkillName} | {f.Rule} | {escapedMessage} |");
        }

        return sb.ToString();
    }

    /// <summary>Renders the report as indented JSON (machine-readable, for CI).</summary>
    public static string RenderJson(SkillComplianceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    // High severity first, then by skill name for a stable, scannable ordering.
    private static IEnumerable<SkillComplianceFinding> SortedBySeverity(IReadOnlyList<SkillComplianceFinding> findings) =>
        findings.OrderByDescending(f => f.Severity).ThenBy(f => f.SkillName, StringComparer.Ordinal);
}
