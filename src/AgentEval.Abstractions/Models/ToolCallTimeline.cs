// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AgentEval.Models;

/// <summary>
/// Represents a single tool invocation in the agent's execution timeline.
/// </summary>
public sealed record ToolInvocation
{
    /// <summary>
    /// The name of the tool that was called.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// When the tool call started (relative to conversation start).
    /// </summary>
    public required TimeSpan StartTime { get; init; }

    /// <summary>
    /// Duration of the tool execution.
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// Whether the tool call succeeded.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Error message if the tool call failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// The arguments passed to the tool (serialized as JSON).
    /// </summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// The result returned by the tool (truncated if too long).
    /// </summary>
    public string? Result { get; init; }

    /// <summary>
    /// Index of this invocation in the overall sequence (0-based).
    /// </summary>
    public int SequenceIndex { get; init; }

    /// <summary>
    /// End time of this invocation.
    /// </summary>
    public TimeSpan EndTime => StartTime + Duration;
}

/// <summary>
/// Represents the complete timeline of an agent's execution, including
/// all tool calls, latencies, and performance metrics for trace-first debugging.
/// </summary>
public sealed class ToolCallTimeline
{
    private readonly List<ToolInvocation> _invocations = new();

    /// <summary>
    /// Time to First Token - how long until the agent started responding.
    /// </summary>
    public TimeSpan TimeToFirstToken { get; set; }

    /// <summary>
    /// Total duration of the agent's execution.
    /// </summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>
    /// When the timeline recording started.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The conversation/session ID this timeline belongs to.
    /// </summary>
    public string? ConversationId { get; set; }

    /// <summary>
    /// All tool invocations in chronological order.
    /// </summary>
    public IReadOnlyList<ToolInvocation> Invocations => _invocations.AsReadOnly();

    /// <summary>
    /// Total number of tool calls made.
    /// </summary>
    public int TotalToolCalls => _invocations.Count;

    /// <summary>
    /// Number of successful tool calls.
    /// </summary>
    public int SuccessfulToolCalls => _invocations.Count(i => i.Succeeded);

    /// <summary>
    /// Number of failed tool calls.
    /// </summary>
    public int FailedToolCalls => _invocations.Count(i => !i.Succeeded);

    /// <summary>
    /// Total time spent in tool execution.
    /// </summary>
    public TimeSpan TotalToolTime => TimeSpan.FromTicks(_invocations.Sum(i => i.Duration.Ticks));

    /// <summary>
    /// Percentage of time spent in tool execution vs total duration.
    /// </summary>
    public double ToolTimePercentage => TotalDuration.TotalMilliseconds > 0
        ? (TotalToolTime.TotalMilliseconds / TotalDuration.TotalMilliseconds) * 100
        : 0;

    /// <summary>
    /// Average tool call duration.
    /// </summary>
    public TimeSpan AverageToolDuration => _invocations.Count > 0
        ? TimeSpan.FromTicks(_invocations.Sum(i => i.Duration.Ticks) / _invocations.Count)
        : TimeSpan.Zero;

    /// <summary>
    /// The slowest tool call.
    /// </summary>
    public ToolInvocation? SlowestTool => _invocations.MaxBy(i => i.Duration);

    /// <summary>
    /// Adds a tool invocation to the timeline.
    /// </summary>
    public void AddInvocation(ToolInvocation invocation)
    {
        _invocations.Add(invocation with { SequenceIndex = _invocations.Count });
    }

    /// <summary>
    /// Gets tool calls grouped by tool name with aggregated statistics.
    /// </summary>
    public IReadOnlyDictionary<string, ToolStatistics> GetToolStatistics()
    {
        return _invocations
            .GroupBy(i => i.ToolName)
            .ToDictionary(
                g => g.Key,
                g => new ToolStatistics
                {
                    ToolName = g.Key,
                    CallCount = g.Count(),
                    SuccessCount = g.Count(i => i.Succeeded),
                    FailureCount = g.Count(i => !i.Succeeded),
                    TotalDuration = TimeSpan.FromTicks(g.Sum(i => i.Duration.Ticks)),
                    AverageDuration = TimeSpan.FromTicks((long)g.Average(i => i.Duration.Ticks)),
                    MinDuration = g.Min(i => i.Duration),
                    MaxDuration = g.Max(i => i.Duration)
                });
    }

    /// <summary>
    /// Generates an ASCII timeline diagram for console/log output.
    /// Makes "why did it fail?" obvious without debugging.
    /// </summary>
    public string ToAsciiDiagram(int width = 80)
    {
        if (_invocations.Count == 0)
        {
            return "No tool calls recorded.";
        }

        var sb = new StringBuilder();
        var timelineWidth = Math.Max(40, width - 30); // Reserve space for labels
        var totalMs = TotalDuration.TotalMilliseconds;
        if (totalMs <= 0) totalMs = 1;

        sb.AppendLine($"┌─ Tool Call Timeline ({TotalToolCalls} calls, {TotalDuration.TotalMilliseconds:F0}ms total) ─┐");
        sb.AppendLine($"│ TTFT: {TimeToFirstToken.TotalMilliseconds:F0}ms | Tool Time: {TotalToolTime.TotalMilliseconds:F0}ms ({ToolTimePercentage:F1}%)");
        sb.AppendLine("├" + new string('─', width - 2) + "┤");

        foreach (var inv in _invocations)
        {
            var startPos = (int)(inv.StartTime.TotalMilliseconds / totalMs * timelineWidth);
            var length = Math.Max(1, (int)(inv.Duration.TotalMilliseconds / totalMs * timelineWidth));

            // Ensure we don't overflow
            startPos = Math.Min(startPos, timelineWidth - 1);
            length = Math.Min(length, timelineWidth - startPos);

            var bar = new string(inv.Succeeded ? '█' : '░', length);
            var padding = new string(' ', startPos);

            var status = inv.Succeeded ? "✓" : "✗";
            var toolLabel = TruncateWithEllipsis(inv.ToolName, 20);
            var duration = $"{inv.Duration.TotalMilliseconds:F0}ms";

            sb.AppendLine($"│ {status} {toolLabel,-20} │{padding}{bar}│ {duration,6}");

            if (!inv.Succeeded && !string.IsNullOrEmpty(inv.ErrorMessage))
            {
                var errorMsg = TruncateWithEllipsis(inv.ErrorMessage, width - 10);
                sb.AppendLine($"│   └─ Error: {errorMsg}");
            }
        }

        sb.AppendLine("├" + new string('─', width - 2) + "┤");

        // Summary section
        if (FailedToolCalls > 0)
        {
            sb.AppendLine($"│ ⚠ {FailedToolCalls} tool call(s) failed");
        }

        if (SlowestTool != null && _invocations.Count > 1)
        {
            sb.AppendLine($"│ 🐢 Slowest: {SlowestTool.ToolName} ({SlowestTool.Duration.TotalMilliseconds:F0}ms)");
        }

        sb.AppendLine("└" + new string('─', width - 2) + "┘");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a compact single-line summary for logs.
    /// </summary>
    public string ToCompactSummary()
    {
        var failures = FailedToolCalls > 0 ? $", {FailedToolCalls} failed" : "";
        return $"Timeline: {TotalToolCalls} tools, {TotalDuration.TotalMilliseconds:F0}ms total, TTFT {TimeToFirstToken.TotalMilliseconds:F0}ms{failures}";
    }

    /// <summary>
    /// Creates an empty timeline with the current timestamp.
    /// </summary>
    public static ToolCallTimeline Create(string? conversationId = null)
    {
        return new ToolCallTimeline
        {
            StartedAt = DateTimeOffset.UtcNow,
            ConversationId = conversationId
        };
    }

    private static string TruncateWithEllipsis(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;

        return text.Substring(0, maxLength - 3) + "...";
    }
}

/// <summary>
/// Aggregated statistics for a specific tool across all its invocations.
/// </summary>
public sealed record ToolStatistics
{
    public required string ToolName { get; init; }
    public required int CallCount { get; init; }
    public required int SuccessCount { get; init; }
    public required int FailureCount { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public required TimeSpan AverageDuration { get; init; }
    public required TimeSpan MinDuration { get; init; }
    public required TimeSpan MaxDuration { get; init; }

    public double SuccessRate => CallCount > 0 ? (double)SuccessCount / CallCount * 100 : 0;
}
