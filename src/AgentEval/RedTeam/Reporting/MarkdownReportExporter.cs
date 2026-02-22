// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text;

namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// Exports red team results to Markdown format for documentation.
/// </summary>
public sealed class MarkdownReportExporter : IReportExporter
{
    /// <inheritdoc />
    public string FormatName => "Markdown";

    /// <inheritdoc />
    public string FileExtension => ".md";

    /// <inheritdoc />
    public string MimeType => "text/markdown";

    /// <inheritdoc />
    public string Export(RedTeamResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();

        // Header
        sb.AppendLine($"# 🛡️ Red Team Report: {result.AgentName}");
        sb.AppendLine();
        sb.AppendLine($"**Generated:** {result.CompletedAt:yyyy-MM-dd HH:mm:ss UTC}  ");
        sb.AppendLine($"**Duration:** {result.Duration.TotalSeconds:F1}s  ");
        sb.AppendLine($"**Tool:** AgentEval RedTeam v0.2.0  ");
        sb.AppendLine();

        // Executive Summary
        sb.AppendLine("## 📊 Executive Summary");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| **Overall Score** | **{result.OverallScore:F1}%** |");
        sb.AppendLine($"| **Verdict** | **{VerdictToEmoji(result.Verdict)} {result.Verdict}** |");
        sb.AppendLine($"| Attack Success Rate | {result.AttackSuccessRate:P1} |");
        sb.AppendLine($"| Total Probes | {result.TotalProbes} |");
        sb.AppendLine($"| ✅ Resisted | {result.ResistedProbes} |");
        sb.AppendLine($"| ❌ Compromised | {result.SucceededProbes} |");
        sb.AppendLine($"| ⚠️ Inconclusive | {result.InconclusiveProbes} |");
        sb.AppendLine();

        // Attack Summary Table
        sb.AppendLine("## 🎯 Attack Results Overview");
        sb.AppendLine();
        sb.AppendLine("| Attack | OWASP | Severity | Score | Resisted | Compromised |");
        sb.AppendLine("|--------|-------|----------|-------|----------|-------------|");

        foreach (var attack in result.AttackResults)
        {
            var score = attack.TotalCount > 0
                ? (attack.ResistedCount * 100.0 / attack.TotalCount)
                : 100;
            var statusIcon = score >= 80 ? "✅" : score >= 50 ? "⚠️" : "❌";
            var severityBadge = SeverityToBadge(attack.Severity);

            sb.AppendLine($"| {statusIcon} {attack.AttackDisplayName} | {attack.OwaspId} | {severityBadge} | {score:F0}% | {attack.ResistedCount} | {attack.SucceededCount} |");
        }
        sb.AppendLine();

        // Detailed Attack Results
        sb.AppendLine("## 📋 Detailed Results");
        sb.AppendLine();

        foreach (var attack in result.AttackResults)
        {
            var score = attack.TotalCount > 0
                ? (attack.ResistedCount * 100.0 / attack.TotalCount)
                : 100;
            var statusIcon = score >= 80 ? "✅" : score >= 50 ? "⚠️" : "❌";

            sb.AppendLine($"### {statusIcon} {attack.AttackDisplayName}");
            sb.AppendLine();
            sb.AppendLine($"**OWASP:** {attack.OwaspId} | **Severity:** {attack.Severity} | **Score:** {score:F0}%");
            sb.AppendLine();

            if (attack.MitreAtlasIds?.Length > 0)
            {
                sb.AppendLine($"**MITRE ATLAS:** {string.Join(", ", attack.MitreAtlasIds)}");
                sb.AppendLine();
            }

            sb.AppendLine($"- **Resisted:** {attack.ResistedCount}/{attack.TotalCount}");
            sb.AppendLine($"- **Compromised:** {attack.SucceededCount}");
            sb.AppendLine($"- **Inconclusive:** {attack.InconclusiveCount}");
            sb.AppendLine();

            // Show compromised probes
            var compromisedProbes = attack.ProbeResults
                .Where(p => p.Outcome == EvaluationOutcome.Succeeded)
                .Take(5)
                .ToList();

            if (compromisedProbes.Count > 0)
            {
                sb.AppendLine("<details>");
                sb.AppendLine($"<summary>❌ Compromised Probes ({attack.SucceededCount})</summary>");
                sb.AppendLine();

                foreach (var probe in compromisedProbes)
                {
                    sb.AppendLine($"**{probe.ProbeId}** ({probe.Technique ?? "unknown"}) - {probe.Difficulty}");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(TruncateString(probe.Prompt, 300));
                    sb.AppendLine("```");
                    sb.AppendLine();
                    sb.AppendLine($"> **Reason:** {probe.Reason}");
                    sb.AppendLine();
                }

                if (attack.SucceededCount > 5)
                {
                    sb.AppendLine($"*...and {attack.SucceededCount - 5} more compromised probes*");
                    sb.AppendLine();
                }

                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        // Recommendations
        if (result.SucceededProbes > 0)
        {
            sb.AppendLine("## 💡 Recommendations");
            sb.AppendLine();
            
            var highSeverityCompromises = result.AttackResults
                .Where(a => (a.Severity == Severity.Critical || a.Severity == Severity.High) && a.SucceededCount > 0)
                .ToList();

            if (highSeverityCompromises.Count > 0)
            {
                sb.AppendLine("### 🚨 Critical/High Priority");
                sb.AppendLine();
                foreach (var attack in highSeverityCompromises)
                {
                    sb.AppendLine($"- **{attack.AttackDisplayName}** ({attack.OwaspId}): {attack.SucceededCount} vulnerabilities found. Review OWASP guidance for {attack.OwaspId}.");
                }
                sb.AppendLine();
            }

            sb.AppendLine("### General Guidance");
            sb.AppendLine();
            sb.AppendLine("1. Review the compromised probes above to understand attack patterns");
            sb.AppendLine("2. Implement input validation and prompt sanitization");
            sb.AppendLine("3. Consider adding content filtering and output guardrails");
            sb.AppendLine("4. Re-run this scan after implementing mitigations");
            sb.AppendLine();
        }

        // Footer
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("*Generated by [AgentEval RedTeam](https://github.com/microsoft/agenteval)*");

        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task ExportToFileAsync(RedTeamResult result, string filePath, CancellationToken cancellationToken = default)
    {
        var markdown = Export(result);
        await File.WriteAllTextAsync(filePath, markdown, cancellationToken);
    }

    private static string VerdictToEmoji(Verdict verdict) => verdict switch
    {
        Verdict.Pass => "✅",
        Verdict.Fail => "❌",
        Verdict.PartialPass => "⚠️",
        _ => "❓"
    };

    private static string SeverityToBadge(Severity severity) => severity switch
    {
        Severity.Critical => "🔴 Critical",
        Severity.High => "🟠 High",
        Severity.Medium => "🟡 Medium",
        Severity.Low => "🟢 Low",
        Severity.Informational => "🔵 Info",
        _ => "⚪ Unknown"
    };

    private static string TruncateString(string input, int maxLength)
    {
        if (string.IsNullOrEmpty(input)) return "";
        if (input.Length <= maxLength) return input;
        return input[..(maxLength - 3)] + "...";
    }
}
