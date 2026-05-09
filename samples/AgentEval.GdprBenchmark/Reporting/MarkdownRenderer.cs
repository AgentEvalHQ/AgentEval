// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.GdprBenchmark.Reporting;

/// <summary>
/// Renders a <see cref="GdprComplianceEvidence"/> as a PR-friendly Markdown report.
/// The report opens with the required disclaimer, then covers overall status,
/// per-pillar and per-article tables, critical findings, and recommendations.
/// </summary>
public sealed class MarkdownRenderer
{
    /// <summary>
    /// Renders <paramref name="evidence"/> to a Markdown string.
    /// </summary>
    /// <param name="evidence">The GDPR evidence to render.</param>
    /// <returns>A Markdown document as a string.</returns>
    public string Render(GdprComplianceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var sb = new StringBuilder();

        var presetTitle = evidence.Preset.Length > 0
            ? char.ToUpperInvariant(evidence.Preset[0]) + evidence.Preset[1..]
            : evidence.Preset;

        sb.AppendLine($"# GDPR Compliance — {presetTitle} Preset");
        sb.AppendLine();
        sb.AppendLine($"> {evidence.Disclaimer}");
        sb.AppendLine();

        sb.AppendLine($"**Subject**: `{evidence.Base.Subject.Name}` ({evidence.Base.Subject.Kind})");
        sb.AppendLine($"**Run**: `{evidence.Base.SourceRun.RunId}`");
        sb.AppendLine($"**Generated**: {evidence.Base.GeneratedAt:O}");
        sb.AppendLine($"**Overall**: **{evidence.Summary.OverallStatus}** (score {evidence.Summary.OverallScore:P0})");
        sb.AppendLine();

        // Smoke and other flat presets produce an empty PerPillar dict; suppress the section entirely
        // to avoid a misleading empty table.
        if (evidence.Summary.PerPillar.Count > 0)
        {
            sb.AppendLine("## Per-pillar");
            sb.AppendLine();
            sb.AppendLine("| Pillar | Score | Status | Critical fails |");
            sb.AppendLine("|---|---:|---|---|");
            foreach (var (key, p) in evidence.Summary.PerPillar)
                sb.AppendLine($"| {key} | {p.Score:P0} | **{p.Status}** | {(p.CriticalFails.Count == 0 ? "—" : string.Join(", ", p.CriticalFails))} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Per-article");
        sb.AppendLine();
        sb.AppendLine("| Control | Score | Status | Severity | Failed/Total |");
        sb.AppendLine("|---|---:|---|---|---:|");
        foreach (var (key, a) in evidence.Summary.PerArticle.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine($"| `{key}` | {a.Score:P0} | **{a.Status}** | {a.Severity} | {a.ScenariosFailed}/{a.ScenarioCount} |");
        sb.AppendLine();

        if (evidence.CriticalFindings.Count > 0)
        {
            sb.AppendLine("## Critical findings");
            sb.AppendLine();
            foreach (var f in evidence.CriticalFindings)
                sb.AppendLine($"- **{f.Metric.Key}** — score {f.Score.Value:P0}, severity `{f.Score.Severity}`");
            sb.AppendLine();
        }

        if (evidence.Recommendations.Count > 0)
        {
            sb.AppendLine("## Recommendations");
            sb.AppendLine();
            foreach (var r in evidence.Recommendations)
                sb.AppendLine($"- {r}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
