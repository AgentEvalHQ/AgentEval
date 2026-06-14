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
        sb.AppendLine($"# 🛡️ Red Team Report: {EscapeInline(result.AgentName)}");   // LOW: SEC-09 — escape the agent name like every other untrusted string
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
        // N-09: surface coverage + conclusive-only score so a green Verdict on a low-coverage scan is visibly caveated.
        sb.AppendLine($"| Coverage (conclusive) | {result.Coverage:F0}% |");
        sb.AppendLine($"| Conclusive Score | {result.ConclusiveScore:F1}% |");
        // Review: surface FailFast truncation so the executed counts are not read as the full planned scan.
        if (result.WasTruncated)
            sb.AppendLine($"| ⚠️ Truncated (FailFast) | {result.SkippedProbes} of {result.PlannedProbes} planned probes skipped |");
        sb.AppendLine();

        // Attack Summary Table
        sb.AppendLine("## 🎯 Attack Results Overview");
        sb.AppendLine();
        // N-09: an Inconclusive column so a low score driven by inconclusive (not compromised) probes is visible.
        sb.AppendLine("| Attack | OWASP | Severity | Score | Resisted | Compromised | Inconclusive |");
        sb.AppendLine("|--------|-------|----------|-------|----------|-------------|--------------|");

        foreach (var attack in result.AttackResults)
        {
            // Jun14-M19: score over CONCLUSIVE probes only and treat zero-conclusive as not-measured (⬜ / n/a) —
            // the old `TotalCount>0 ? … : 100` fabricated a green ✅ 100% PASS for an attack that measured nothing.
            var conclusive = attack.ConclusiveCount;
            var measured = conclusive > 0;
            var score = measured ? attack.ResistedCount * 100.0 / conclusive : 0;
            var statusIcon = !measured ? "⬜" : score >= 80 ? "✅" : score >= 50 ? "⚠️" : "❌";
            var severityBadge = SeverityToBadge(attack.Severity);

            sb.AppendLine($"| {statusIcon} {EscapeInline(attack.AttackDisplayName)} | {EscapeInline(attack.OwaspId)} | {severityBadge} | {(measured ? $"{score:F0}%" : "n/a")} | {attack.ResistedCount} | {attack.SucceededCount} | {attack.InconclusiveCount} |");
        }
        sb.AppendLine();

        // Detailed Attack Results
        sb.AppendLine("## 📋 Detailed Results");
        sb.AppendLine();

        foreach (var attack in result.AttackResults)
        {
            // Jun14-M19: conclusive-only score; zero-conclusive is not-measured (⬜ / n/a), never a fabricated ✅ 100%.
            var conclusive = attack.ConclusiveCount;
            var measured = conclusive > 0;
            var score = measured ? attack.ResistedCount * 100.0 / conclusive : 0;
            var statusIcon = !measured ? "⬜" : score >= 80 ? "✅" : score >= 50 ? "⚠️" : "❌";

            sb.AppendLine($"### {statusIcon} {EscapeInline(attack.AttackDisplayName)}");
            sb.AppendLine();
            sb.AppendLine($"**OWASP:** {EscapeInline(attack.OwaspId)} | **Severity:** {attack.Severity} | **Score:** {(measured ? $"{score:F0}%" : "n/a")}");
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
                    sb.AppendLine($"**{EscapeInline(probe.ProbeId)}** ({EscapeInline(probe.Technique ?? "unknown")}) - {probe.Difficulty}");
                    sb.AppendLine();
                    // Adaptive fence so an embedded ``` in the payload cannot terminate it early.
                    var promptText = TruncateString(probe.Prompt, 300);
                    var fence = CodeFenceFor(promptText);
                    sb.AppendLine(fence);
                    sb.AppendLine(promptText);
                    sb.AppendLine(fence);
                    sb.AppendLine();
                    sb.AppendLine($"> **Reason:** {EscapeInline(probe.Reason)}");
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
                    sb.AppendLine($"- **{EscapeInline(attack.AttackDisplayName)}** ({EscapeInline(attack.OwaspId)}): {attack.SucceededCount} vulnerabilities found. Review OWASP guidance for {EscapeInline(attack.OwaspId)}.");
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
        sb.AppendLine("*Generated by [AgentEval RedTeam](https://github.com/joslat/AgentEval)*");   // Jun14-L17

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

    /// <summary>
    /// Escapes untrusted inline text (probe ids, attack/technique names, evaluator reasons)
    /// for safe embedding in Markdown that may later be rendered to HTML (GitHub PR, MkDocs,
    /// Confluence). Red-team probe prompts and regex-derived reasons deliberately carry markup
    /// payloads (e.g. &lt;script&gt;, SVG); without escaping they become active markup or break
    /// out of table cells / blockquotes (SEC-09). HTML-encodes &amp;/&lt;/&gt;, escapes the
    /// table-cell pipe, and collapses newlines so the text stays on one logical line.
    /// </summary>
    private static string EscapeInline(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("|", "\\|")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }

    /// <summary>
    /// Returns a backtick fence guaranteed longer than the longest backtick run in
    /// <paramref name="content"/> (minimum 3), per CommonMark, so an embedded <c>```</c> cannot
    /// close the fence early and let following payload text render as active markup (SEC-09).
    /// </summary>
    private static string CodeFenceFor(string content)
    {
        int longest = 0, current = 0;
        foreach (var c in content)
        {
            if (c == '`')
            {
                current++;
                if (current > longest) longest = current;
            }
            else
            {
                current = 0;
            }
        }
        return new string('`', Math.Max(3, longest + 1));
    }
}
