// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Models;

namespace AgentEval.Samples;

// Shared by the live C3 sample and offline rendering checks.
internal static class WorkflowWithToolsReport
{
    internal static void WriteStep(TextWriter writer, ExecutorStep step)
    {
        var startMs = step.StartOffset.TotalMilliseconds;
        var duration = step.TimingNotMeasuredReason is { } reason
            ? $"time not measured: {reason}"
            : $"{step.Duration.TotalMilliseconds:F0}ms";

        writer.WriteLine($"      {step.StepIndex + 1}. [{startMs:F0}ms] {step.ExecutorId} ({duration})");
    }

    internal static void WriteToolSummary(TextWriter writer, ToolUsageReport report)
    {
        writer.WriteLine($"   📊 ToolUsage report: {report.Count} total tool calls");
        writer.WriteLine($"   🔧 Unique tools: {string.Join(", ", report.UniqueToolNames)}");
        writer.WriteLine($"   ⏱️ {FormatToolTime(report.Count, report.Calls.Count(call => !call.HasTiming), report.TotalToolTime)}");
        writer.WriteLine($"   ❌ Has errors: {report.HasErrors}\n");
    }

    internal static void WriteToolCall(TextWriter writer, ToolCallRecord call, string resultSnippet)
    {
        var duration = call.Duration is { } measured ? $"{measured.TotalMilliseconds:F0}ms" : "time not measured";
        writer.WriteLine($"      🔧 {call.Name} ({duration}) → {resultSnippet}");
    }

    internal static void WriteTimeline(TextWriter writer, ToolCallTimeline timeline)
    {
        writer.WriteLine($"\n   📈 TOOL TIMELINE:");
        writer.WriteLine($"      Total: {timeline.TotalToolCalls} calls, {timeline.TotalDuration.TotalMilliseconds:F0}ms total");
        writer.WriteLine($"      {FormatToolTime(timeline.TotalToolCalls, timeline.UnmeasuredDurationCount, timeline.TotalToolTime, timeline.TotalDuration)}");
        // The diagram's percentage requires a positive whole-workflow duration.
        if (timeline.TotalDuration > TimeSpan.Zero)
            writer.WriteLine(timeline.ToAsciiDiagram(80));
        else
            writer.WriteLine("      Timeline diagram unavailable: total duration must be positive.");
    }

    private static string FormatToolTime(int count, int unknown, TimeSpan measuredSubtotal, TimeSpan? totalDuration = null)
    {
        if (count == 0)
            return "Tool time: no tool calls";

        var measured = count - unknown;
        if (measured == 0)
            return $"Tool time: not measured (0 measured, {unknown} unknown)";
        if (unknown > 0)
            return $"Known tool time subtotal: {measuredSubtotal.TotalMilliseconds:F0}ms ({measured} measured, {unknown} unknown)";

        var percentage = totalDuration is { } elapsed && elapsed > TimeSpan.Zero
            ? $" ({measuredSubtotal.TotalMilliseconds / elapsed.TotalMilliseconds * 100:F1}%)"
            : "";
        return $"Total tool time: {measuredSubtotal.TotalMilliseconds:F0}ms{percentage}";
    }
}
