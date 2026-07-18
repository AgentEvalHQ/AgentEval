// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Renders a <see cref="ReplayReport"/> as Markdown — reuses the Markdown-table convention already standard across this CLI's <c>--out</c> flags (<c>bench calibrate</c>, <c>compliance render</c>), not a new report format.</summary>
public static class ReplayReportRenderer
{
    /// <summary>Renders the full report: a summary line, then one row per replayed entry.</summary>
    public static string Render(ReplayReport report, string capturedPath, string against)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();

        sb.AppendLine("# Log-File Replay Report");
        sb.AppendLine();
        sb.AppendLine($"- Capture: `{capturedPath}`");
        sb.AppendLine($"- Against: `{against}`");
        sb.AppendLine($"- Comparison: structural/behavioral (tool calls, finish reason, response shape) — never exact text unless `--strict-text` was passed.");
        sb.AppendLine();
        sb.AppendLine($"**Summary:** {report.Rows.Count} replayed ({report.PassCount} pass, {report.FlagCount} flag, {report.FailCount} fail)" +
            (report.SkippedNonResponseCount > 0 ? $", {report.SkippedNonResponseCount} skipped (no captured response to diff against)" : "") + ".");
        sb.AppendLine();

        if (report.Rows.Count == 0)
        {
            sb.AppendLine("No \"response\"-kind entries were found in the capture — nothing to replay.");
            return sb.ToString();
        }

        sb.AppendLine("| # | Verdict | Summary |");
        sb.AppendLine("|---|---|---|");
        foreach (var row in report.Rows)
        {
            sb.AppendLine($"| {row.Index} | {VerdictLabel(row.Verdict)} | {EscapePipes(row.Summary)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Details");
        sb.AppendLine();

        foreach (var row in report.Rows)
        {
            sb.AppendLine($"### [{row.Index}] {VerdictLabel(row.Verdict)} — {row.Summary}");
            sb.AppendLine();
            sb.AppendLine($"- Latency: captured={row.CapturedElapsedMs}ms, actual={row.ActualElapsedMs}ms");
            foreach (var detail in row.Details)
            {
                sb.AppendLine($"- {detail}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string VerdictLabel(ReplayVerdict verdict) => verdict switch
    {
        ReplayVerdict.Pass => "✅ PASS",
        ReplayVerdict.Flag => "⚠️ FLAG",
        ReplayVerdict.Fail => "❌ FAIL",
        _ => verdict.ToString(),
    };

    private static string EscapePipes(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
