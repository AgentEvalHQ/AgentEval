// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Tracing;

namespace AgentEval.Tests.Core;

/// <summary>
/// Glass Box Part 2 (P2.A2): <see cref="ToolUsageExtractor.EnrichFromTrace"/> back-fills real per-tool
/// execution timing from a trace's <see cref="TraceEntryScope.ToolExecution"/> entries, fixing the
/// long-inert duration assertions.
/// </summary>
public class ToolUsageEnrichFromTraceTests
{
    private static ToolCallRecord Call(string name, int order) =>
        new() { Name = name, CallId = $"c{order}", Order = order };

    private static AgentTrace TraceWithExecutions(params (string name, long durationMs)[] executions)
    {
        var trace = new AgentTrace();
        var i = 0;
        foreach (var (name, durationMs) in executions)
        {
            trace.Entries.Add(TraceEntry.ForToolExecution(
                index: i++, correlationId: null, toolName: name, arguments: null,
                result: "ok", durationMs: durationMs, succeeded: true, error: null));
        }

        return trace;
    }

    [Fact]
    public void EnrichFromTrace_PopulatesTimingAndTotalToolTime()
    {
        var report = new ToolUsageReport();
        report.AddCall(Call("search", 1));

        ToolUsageExtractor.EnrichFromTrace(report, TraceWithExecutions(("search", 250)));

        var call = report.Calls[0];
        Assert.True(call.HasTiming);
        Assert.Equal(TimeSpan.FromMilliseconds(250), call.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(250), report.TotalToolTime);
    }

    [Fact]
    public void EnrichFromTrace_SameToolMultipleCalls_MatchesInFifoOrder()
    {
        var report = new ToolUsageReport();
        report.AddCall(Call("search", 1));
        report.AddCall(Call("search", 2));

        ToolUsageExtractor.EnrichFromTrace(report, TraceWithExecutions(("search", 100), ("search", 400)));

        Assert.Equal(TimeSpan.FromMilliseconds(100), report.Calls[0].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(400), report.Calls[1].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(500), report.TotalToolTime);
    }

    [Fact]
    public void EnrichFromTrace_DoesNotOverwriteExistingTiming()
    {
        var start = DateTimeOffset.UtcNow;
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord
        {
            Name = "search", CallId = "c1", Order = 1,
            StartTime = start, EndTime = start + TimeSpan.FromMilliseconds(999),
        });

        ToolUsageExtractor.EnrichFromTrace(report, TraceWithExecutions(("search", 100)));

        // Streaming-path timing (999ms) is preserved, not replaced by the trace's 100ms.
        Assert.Equal(TimeSpan.FromMilliseconds(999), report.Calls[0].Duration);
    }

    [Fact]
    public void EnrichFromTrace_NoToolExecutionEntries_IsNoOp()
    {
        var report = new ToolUsageReport();
        report.AddCall(Call("search", 1));

        ToolUsageExtractor.EnrichFromTrace(report, new AgentTrace());

        Assert.False(report.Calls[0].HasTiming);
    }

    [Fact]
    public void EnrichFromTrace_UnmatchedName_LeavesCallUntimed()
    {
        var report = new ToolUsageReport();
        report.AddCall(Call("search", 1));

        ToolUsageExtractor.EnrichFromTrace(report, TraceWithExecutions(("other_tool", 100)));

        Assert.False(report.Calls[0].HasTiming);
    }
}
