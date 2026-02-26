// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Exporters;

/// <summary>
/// Exports evaluation results as Markdown.
/// Ideal for GitHub PR comments, Azure DevOps discussions, and documentation.
/// </summary>
public class MarkdownExporter : IResultExporter
{
    /// <inheritdoc />
    public ExportFormat Format => ExportFormat.Markdown;
    
    /// <inheritdoc />
    public string FileExtension => ".md";
    
    /// <inheritdoc />
    public string ContentType => "text/markdown";

    /// <summary>
    /// Options for Markdown export.
    /// </summary>
    public MarkdownExportOptions Options { get; set; } = new();

    /// <inheritdoc />
    public async Task ExportAsync(EvaluationReport report, Stream output, CancellationToken ct = default)
    {
        var markdown = ExportToString(report);
        var bytes = Encoding.UTF8.GetBytes(markdown);
        await output.WriteAsync(bytes, ct);
    }
    
    /// <summary>
    /// Export to a string (useful for PR comments).
    /// </summary>
    public string ExportToString(EvaluationReport report)
    {
        var sb = new StringBuilder();
        
        // Header
        var statusEmoji = report.FailedTests == 0 ? "✅" : "❌";
        var statusText = report.FailedTests == 0 ? "PASSED" : "FAILED";
        
        sb.AppendLine("## 🤖 AgentEval Results");
        sb.AppendLine();
        sb.AppendLine($"**Status:** {statusEmoji} {statusText}");
        sb.AppendLine($"**Score:** {report.OverallScore:F1}/100");
        sb.AppendLine($"**Tests:** {report.PassedTests}/{report.TotalTests} passed ({report.PassRate:F0}%)");
        sb.AppendLine($"**Duration:** {FormatDuration(report.Duration)}");
        
        if (report.Agent?.Model != null)
        {
            sb.AppendLine($"**Model:** {report.Agent.Model}");
        }
        
        sb.AppendLine();
        
        // Results table
        sb.AppendLine("### Test Results");
        sb.AppendLine();
        sb.AppendLine("| Test | Score | Status | Time |");
        sb.AppendLine("|------|-------|--------|------|");
        
        var orderedResults = Options.FailuresFirst
            ? report.TestResults.OrderBy(t => t.Passed).ThenBy(t => t.Name)
            : report.TestResults.OrderByDescending(t => t.Passed).ThenBy(t => t.Name);
        
        foreach (var test in orderedResults)
        {
            var status = test.Skipped ? "⏭️" : (test.Passed ? "✅" : "❌");
            var time = FormatDuration(TimeSpan.FromMilliseconds(test.DurationMs));
            
            sb.AppendLine($"| {EscapeMarkdown(test.Name)} | {test.Score:F1} | {status} | {time} |");
        }
        
        // Failures section
        var failures = report.TestResults.Where(t => !t.Passed && !t.Skipped).ToList();
        if (failures.Count > 0 && Options.IncludeFailureDetails)
        {
            sb.AppendLine();
            sb.AppendLine("### ❌ Failures");
            sb.AppendLine();
            
            foreach (var failure in failures)
            {
                sb.AppendLine($"**{EscapeMarkdown(failure.Name)}** (Score: {failure.Score:F1}/100)");
                if (!string.IsNullOrEmpty(failure.Error))
                {
                    sb.AppendLine($"- {EscapeMarkdown(failure.Error)}");
                }
                sb.AppendLine();
            }
        }
        
        // Metric breakdown (if available)
        var testsWithMetrics = report.TestResults.Where(t => t.MetricScores.Count > 0).ToList();
        if (testsWithMetrics.Count > 0 && Options.IncludeMetricBreakdown)
        {
            sb.AppendLine("### 📊 Metric Breakdown");
            sb.AppendLine();
            
            // Get all unique metric names
            var metricNames = testsWithMetrics
                .SelectMany(t => t.MetricScores.Keys)
                .Distinct()
                .OrderBy(m => m)
                .ToList();
            
            sb.Append("| Test |");
            foreach (var metric in metricNames)
            {
                sb.Append($" {metric} |");
            }
            sb.AppendLine();
            
            sb.Append("|------|");
            foreach (var _ in metricNames)
            {
                sb.Append("------|");
            }
            sb.AppendLine();
            
            foreach (var test in testsWithMetrics)
            {
                sb.Append($"| {EscapeMarkdown(test.Name)} |");
                foreach (var metric in metricNames)
                {
                    var score = test.MetricScores.TryGetValue(metric, out var s) ? $"{s:F1}" : "-";
                    sb.Append($" {score} |");
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }
        
        // Footer
        if (Options.IncludeFooter)
        {
            sb.AppendLine("---");
            sb.AppendLine($"*Run ID: `{report.RunId}` | {report.StartTime:yyyy-MM-dd HH:mm:ss} UTC*");
        }
        
        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMilliseconds < 1000)
            return $"{duration.TotalMilliseconds:F0}ms";
        if (duration.TotalSeconds < 60)
            return $"{duration.TotalSeconds:F1}s";
        return $"{duration.TotalMinutes:F1}m";
    }

    private static string EscapeMarkdown(string text)
    {
        return text
            .Replace("|", "\\|")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("`", "\\`");
    }
}

/// <summary>
/// Options for Markdown export.
/// </summary>
public class MarkdownExportOptions
{
    /// <summary>Show failures at the top of the results table.</summary>
    public bool FailuresFirst { get; set; } = false;
    
    /// <summary>Include detailed failure information.</summary>
    public bool IncludeFailureDetails { get; set; } = true;
    
    /// <summary>Include metric breakdown table.</summary>
    public bool IncludeMetricBreakdown { get; set; } = true;
    
    /// <summary>Include footer with run ID and timestamp.</summary>
    public bool IncludeFooter { get; set; } = true;
}
