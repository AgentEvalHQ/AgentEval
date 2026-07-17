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
    /// <param name="report">The report to render.</param>
    /// <param name="provenanceByName">
    /// Agent Skills Wave 1 (§4.5 tier 1) — optional, keyed by skill name. When a finding's skill has a
    /// <see cref="SkillBaselineEntry.Source"/>/<see cref="SkillBaselineEntry.SourceUrl"/>, prints it inline
    /// next to the finding (e.g. <c>"→ from chillicream/agent-skills@a1b2c3d"</c>). <see langword="null"/>-safe
    /// and backward compatible — every existing call site keeps working unchanged when omitted. No lock-file
    /// parser exists yet to populate this from a real source (Wave 1 scope) — the wiring is real and tested,
    /// the data source is a Wave 2/3 follow-on.
    /// </param>
    public static string RenderConsole(SkillComplianceReport report, IReadOnlyDictionary<string, SkillBaselineEntry>? provenanceByName = null)
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
                sb.AppendLine($"[{f.Severity}] {f.SkillName} — {f.Rule}: {f.Message}{ProvenanceSuffix(f.SkillName, provenanceByName)}");
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
    /// <param name="report">The report to render.</param>
    /// <param name="provenanceByName">See <see cref="RenderConsole"/>'s parameter of the same name.</param>
    public static string RenderMarkdown(SkillComplianceReport report, IReadOnlyDictionary<string, SkillBaselineEntry>? provenanceByName = null)
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

        sb.AppendLine("| Severity | Skill | Rule | Message | Source |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var f in SortedBySeverity(report.Findings))
        {
            var escapedMessage = f.Message.Replace("|", "\\|", StringComparison.Ordinal);
            var provenance = ProvenanceCell(f.SkillName, provenanceByName);
            sb.AppendLine($"| {f.Severity} | {f.SkillName} | {f.Rule} | {escapedMessage} | {provenance} |");
        }

        return sb.ToString();
    }

    /// <summary>Renders the report as indented JSON (machine-readable, for CI).</summary>
    /// <param name="report">The report to render.</param>
    /// <param name="provenanceByName">
    /// See <see cref="RenderConsole"/>'s parameter of the same name. When supplied, each finding's JSON
    /// object gains a <c>provenance</c> field (<c>source</c>/<c>sourceUrl</c>/<c>ref</c>) whenever a
    /// matching entry exists — omitted (not <c>null</c>-padded) for a skill with no provenance data, so a
    /// <see langword="null"/> <paramref name="provenanceByName"/> produces byte-identical output to the
    /// pre-Wave-1 shape (backward compatible).
    /// </param>
    public static string RenderJson(SkillComplianceReport report, IReadOnlyDictionary<string, SkillBaselineEntry>? provenanceByName = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (provenanceByName is null or { Count: 0 })
        {
            return JsonSerializer.Serialize(report, JsonOptions);
        }

        // PascalCase field names throughout, matching the exact casing System.Text.Json's DEFAULT (no
        // naming-policy override — see JsonOptions above) produces for the plain SkillComplianceReport
        // shape the no-provenance branch above still serializes as-is — so the two branches differ only in
        // the ADDED Provenance field, not in casing convention.
        var findingsWithProvenance = report.Findings.Select(f =>
        {
            provenanceByName.TryGetValue(f.SkillName, out var entry);
            object? provenance = entry is { Source: not null } or { SourceUrl: not null } or { Ref: not null }
                ? new { Source = entry.Source, SourceUrl = entry.SourceUrl, Ref = entry.Ref }
                : null;
            return new
            {
                SkillName = f.SkillName,
                Rule = f.Rule,
                Severity = f.Severity,
                Message = f.Message,
                Field = f.Field,
                Provenance = provenance,
            };
        });

        var shape = new { Findings = findingsWithProvenance, Coverage = report.Coverage, IsCompliant = report.IsCompliant };
        return JsonSerializer.Serialize(shape, JsonOptions);
    }

    // "→ from chillicream/agent-skills@a1b2c3d" — printed inline next to a console finding when the
    // finding's skill has a Source in provenanceByName. Empty string (no-op) when absent, so the caller
    // never has to special-case a missing entry.
    private static string ProvenanceSuffix(string skillName, IReadOnlyDictionary<string, SkillBaselineEntry>? provenanceByName)
    {
        if (provenanceByName is null || !provenanceByName.TryGetValue(skillName, out var entry))
        {
            return string.Empty;
        }

        var label = ProvenanceLabel(entry);
        return label is null ? string.Empty : $" → from {label}";
    }

    private static string ProvenanceCell(string skillName, IReadOnlyDictionary<string, SkillBaselineEntry>? provenanceByName)
    {
        if (provenanceByName is null || !provenanceByName.TryGetValue(skillName, out var entry))
        {
            return string.Empty;
        }

        return ProvenanceLabel(entry) ?? string.Empty;
    }

    private static string? ProvenanceLabel(SkillBaselineEntry entry)
    {
        if (entry.Source is null && entry.SourceUrl is null)
        {
            return null;
        }

        var label = entry.Source ?? entry.SourceUrl!;
        return entry.Ref is null ? label : $"{label}@{entry.Ref}";
    }

    // High severity first, then by skill name for a stable, scannable ordering.
    private static IEnumerable<SkillComplianceFinding> SortedBySeverity(IReadOnlyList<SkillComplianceFinding> findings) =>
        findings.OrderByDescending(f => f.Severity).ThenBy(f => f.SkillName, StringComparer.Ordinal);
}
