// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AgentEval.Models;

/// <summary>
/// Represents a single reason why an evaluation or agent execution failed.
/// </summary>
public sealed record FailureReason
{
    /// <summary>
    /// Category of the failure (e.g., "ToolError", "Timeout", "ValidationFailed", "LLMError").
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Human-readable description of what went wrong.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The specific component or metric that reported this failure.
    /// </summary>
    public string? Source { get; init; }

    /// <summary>
    /// Severity level: Critical, Error, Warning.
    /// </summary>
    public FailureSeverity Severity { get; init; } = FailureSeverity.Error;

    /// <summary>
    /// Stack trace or additional technical details (optional, for debugging).
    /// </summary>
    public string? TechnicalDetails { get; init; }

    /// <summary>
    /// Reference to related tool invocation if applicable.
    /// </summary>
    public ToolInvocation? RelatedToolCall { get; init; }
}

/// <summary>
/// Severity level of a failure reason.
/// </summary>
public enum FailureSeverity
{
    /// <summary>
    /// Warning - non-blocking issue that should be reviewed.
    /// </summary>
    Warning,

    /// <summary>
    /// Error - significant problem that affected the result.
    /// </summary>
    Error,

    /// <summary>
    /// Critical - complete failure that prevented evaluation.
    /// </summary>
    Critical
}

/// <summary>
/// A suggestion for how to fix or investigate a failure.
/// </summary>
public sealed record FixSuggestion
{
    /// <summary>
    /// Short title for the suggestion.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Detailed description of what to do.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Category: "Configuration", "Code", "Prompt", "Infrastructure", "Model".
    /// </summary>
    public string Category { get; init; } = "General";

    /// <summary>
    /// Confidence that this suggestion will help (0.0 to 1.0).
    /// </summary>
    public double Confidence { get; init; } = 0.5;

    /// <summary>
    /// Documentation or resource link for more information.
    /// </summary>
    public string? DocumentationUrl { get; init; }
}

/// <summary>
/// Comprehensive failure report that makes "why did it fail?" obvious.
/// Designed for trace-first debugging without stepping through agent code.
/// </summary>
public sealed class FailureReport
{
    private readonly List<FailureReason> _reasons = new();
    private readonly List<FixSuggestion> _suggestions = new();

    /// <summary>
    /// One-line summary of what failed - the "headline" for logs and CI.
    /// </summary>
    public required string WhyItFailed { get; init; }

    /// <summary>
    /// The metric or component that generated this failure report.
    /// </summary>
    public string? MetricName { get; init; }

    /// <summary>
    /// Score achieved (if applicable), often 0.0 for failures.
    /// </summary>
    public double? Score { get; init; }

    /// <summary>
    /// When the failure occurred.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Correlation ID for linking to traces/logs.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// The tool call timeline if available.
    /// </summary>
    public ToolCallTimeline? Timeline { get; init; }

    /// <summary>
    /// All reasons that contributed to the failure.
    /// </summary>
    public IReadOnlyList<FailureReason> Reasons => _reasons.AsReadOnly();

    /// <summary>
    /// Suggested fixes ordered by confidence.
    /// </summary>
    public IReadOnlyList<FixSuggestion> Suggestions => _suggestions.OrderByDescending(s => s.Confidence).ToList();

    /// <summary>
    /// The most critical failure reason.
    /// </summary>
    public FailureReason? PrimaryReason => _reasons
        .OrderByDescending(r => r.Severity)
        .ThenBy(r => _reasons.IndexOf(r))
        .FirstOrDefault();

    /// <summary>
    /// Whether this is a critical failure (any Critical severity reason).
    /// </summary>
    public bool IsCritical => _reasons.Any(r => r.Severity == FailureSeverity.Critical);

    /// <summary>
    /// Add a failure reason to the report.
    /// </summary>
    public FailureReport AddReason(FailureReason reason)
    {
        _reasons.Add(reason);
        return this;
    }

    /// <summary>
    /// Add a failure reason with simple parameters.
    /// </summary>
    public FailureReport AddReason(string category, string description, FailureSeverity severity = FailureSeverity.Error)
    {
        _reasons.Add(new FailureReason
        {
            Category = category,
            Description = description,
            Severity = severity,
            Source = MetricName
        });
        return this;
    }

    /// <summary>
    /// Add a fix suggestion to the report.
    /// </summary>
    public FailureReport AddSuggestion(FixSuggestion suggestion)
    {
        _suggestions.Add(suggestion);
        return this;
    }

    /// <summary>
    /// Add a fix suggestion with simple parameters.
    /// </summary>
    public FailureReport AddSuggestion(string title, string description, double confidence = 0.5)
    {
        _suggestions.Add(new FixSuggestion
        {
            Title = title,
            Description = description,
            Confidence = confidence
        });
        return this;
    }

    /// <summary>
    /// Generates a formatted report suitable for console output or logs.
    /// </summary>
    public string ToFormattedReport(bool includeTimeline = true)
    {
        var sb = new StringBuilder();

        // Header
        var icon = IsCritical ? "🔴" : "🟠";
        sb.AppendLine($"{icon} FAILURE REPORT: {WhyItFailed}");
        sb.AppendLine(new string('═', 70));

        if (!string.IsNullOrEmpty(MetricName))
        {
            sb.AppendLine($"Metric: {MetricName}");
        }

        if (Score.HasValue)
        {
            sb.AppendLine($"Score: {Score:F2}");
        }

        sb.AppendLine($"Time: {Timestamp:yyyy-MM-dd HH:mm:ss} UTC");

        if (!string.IsNullOrEmpty(CorrelationId))
        {
            sb.AppendLine($"Correlation ID: {CorrelationId}");
        }

        // Reasons section
        sb.AppendLine();
        sb.AppendLine("─── Failure Reasons ───");
        foreach (var reason in _reasons)
        {
            var severityIcon = reason.Severity switch
            {
                FailureSeverity.Critical => "🔴",
                FailureSeverity.Error => "🟠",
                FailureSeverity.Warning => "🟡",
                _ => "⚪"
            };

            sb.AppendLine($"  {severityIcon} [{reason.Category}] {reason.Description}");

            if (reason.RelatedToolCall != null)
            {
                sb.AppendLine($"      └─ Related tool: {reason.RelatedToolCall.ToolName}");
            }
        }

        // Suggestions section
        if (_suggestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("─── Suggested Fixes ───");
            var orderedSuggestions = _suggestions.OrderByDescending(s => s.Confidence).ToList();
            for (int i = 0; i < orderedSuggestions.Count; i++)
            {
                var suggestion = orderedSuggestions[i];
                var confidence = suggestion.Confidence switch
                {
                    >= 0.8 => "⭐⭐⭐",
                    >= 0.5 => "⭐⭐",
                    _ => "⭐"
                };

                sb.AppendLine($"  {i + 1}. {suggestion.Title} {confidence}");
                sb.AppendLine($"     {suggestion.Description}");

                if (!string.IsNullOrEmpty(suggestion.DocumentationUrl))
                {
                    sb.AppendLine($"     📚 {suggestion.DocumentationUrl}");
                }
            }
        }

        // Timeline section
        if (includeTimeline && Timeline != null)
        {
            sb.AppendLine();
            sb.AppendLine("─── Tool Call Timeline ───");
            sb.AppendLine(Timeline.ToAsciiDiagram());
        }

        sb.AppendLine(new string('═', 70));
        return sb.ToString();
    }

    /// <summary>
    /// Generates a compact one-line summary for CI output.
    /// </summary>
    public string ToCompactSummary()
    {
        var severity = IsCritical ? "CRITICAL" : "FAILED";
        var reasonCount = _reasons.Count;
        var metric = !string.IsNullOrEmpty(MetricName) ? $"[{MetricName}] " : "";
        return $"{severity}: {metric}{WhyItFailed} ({reasonCount} reason{(reasonCount != 1 ? "s" : "")})";
    }

    /// <summary>
    /// Creates a failure report for a tool call failure.
    /// </summary>
    public static FailureReport FromToolCallFailure(ToolInvocation failedTool, string metricName)
    {
        var report = new FailureReport
        {
            WhyItFailed = $"Tool '{failedTool.ToolName}' failed during execution",
            MetricName = metricName,
            Score = 0.0
        };

        report.AddReason(new FailureReason
        {
            Category = "ToolError",
            Description = failedTool.ErrorMessage ?? "Unknown tool error",
            Severity = FailureSeverity.Error,
            Source = metricName,
            RelatedToolCall = failedTool
        });

        report.AddSuggestion(new FixSuggestion
        {
            Title = "Check tool implementation",
            Description = $"Review the '{failedTool.ToolName}' tool's implementation for errors or edge cases.",
            Category = "Code",
            Confidence = 0.7
        });

        report.AddSuggestion(new FixSuggestion
        {
            Title = "Verify tool arguments",
            Description = $"Check if the agent is passing correct arguments to '{failedTool.ToolName}'.",
            Category = "Prompt",
            Confidence = 0.6
        });

        return report;
    }

    /// <summary>
    /// Creates a failure report for a timeout.
    /// </summary>
    public static FailureReport FromTimeout(TimeSpan elapsed, TimeSpan limit, string? metricName = null)
    {
        var report = new FailureReport
        {
            WhyItFailed = $"Execution timed out after {elapsed.TotalSeconds:F1}s (limit: {limit.TotalSeconds:F1}s)",
            MetricName = metricName,
            Score = 0.0
        };

        report.AddReason("Timeout", $"Exceeded time limit of {limit.TotalSeconds:F1} seconds", FailureSeverity.Critical);

        report.AddSuggestion(new FixSuggestion
        {
            Title = "Increase timeout limit",
            Description = "If the operation is expected to take longer, consider increasing the timeout threshold.",
            Category = "Configuration",
            Confidence = 0.8
        });

        report.AddSuggestion(new FixSuggestion
        {
            Title = "Optimize tool performance",
            Description = "Review tool implementations for performance bottlenecks.",
            Category = "Code",
            Confidence = 0.5
        });

        return report;
    }

    /// <summary>
    /// Creates a failure report for a validation failure.
    /// </summary>
    public static FailureReport FromValidationFailure(string validationMessage, string metricName, double? score = null)
    {
        var report = new FailureReport
        {
            WhyItFailed = validationMessage,
            MetricName = metricName,
            Score = score
        };

        report.AddReason("ValidationFailed", validationMessage, FailureSeverity.Error);

        return report;
    }

    /// <summary>
    /// Creates a failure report for an LLM error.
    /// </summary>
    public static FailureReport FromLLMError(string errorMessage, string? metricName = null)
    {
        var report = new FailureReport
        {
            WhyItFailed = $"LLM evaluation failed: {errorMessage}",
            MetricName = metricName,
            Score = 0.0
        };

        report.AddReason("LLMError", errorMessage, FailureSeverity.Critical);

        report.AddSuggestion(new FixSuggestion
        {
            Title = "Check API connectivity",
            Description = "Verify that the LLM service is accessible and credentials are valid.",
            Category = "Infrastructure",
            Confidence = 0.7
        });

        report.AddSuggestion(new FixSuggestion
        {
            Title = "Review rate limits",
            Description = "Check if you've exceeded API rate limits.",
            Category = "Infrastructure",
            Confidence = 0.5
        });

        return report;
    }
}
