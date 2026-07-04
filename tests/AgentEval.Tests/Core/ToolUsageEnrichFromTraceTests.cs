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
    // An EXECUTED call (has a paired result), which is what carries a matching ToolExecution trace entry.
    private static ToolCallRecord Call(string name, int order) =>
        new() { Name = name, CallId = $"c{order}", Order = order, WasExecuted = true };

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
            Name = "search", CallId = "c1", Order = 1, WasExecuted = true,
            StartTime = start, EndTime = start + TimeSpan.FromMilliseconds(999),
        });

        ToolUsageExtractor.EnrichFromTrace(report, TraceWithExecutions(("search", 100)));

        // Streaming-path timing (999ms) is preserved, not replaced by the trace's 100ms.
        Assert.Equal(TimeSpan.FromMilliseconds(999), report.Calls[0].Duration);
    }

    [Fact]
    public void EnrichFromTrace_EmittedNotExecutedCall_DoesNotConsumeSlot()
    {
        // 'foo' emitted-but-not-executed at order 1, then executed at order 2 (both untimed). The single
        // recorded execution (30ms) must attach to the EXECUTED call, not the emit.
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord { Name = "foo", CallId = "a", Order = 1, WasExecuted = false });
        report.AddCall(new ToolCallRecord { Name = "foo", CallId = "b", Order = 2, WasExecuted = true });

        ToolUsageExtractor.EnrichFromTrace(report, TraceWithExecutions(("foo", 30)));

        Assert.False(report.Calls[0].HasTiming);                              // emit: no timing
        Assert.Equal(TimeSpan.FromMilliseconds(30), report.Calls[1].Duration); // executed call gets it
    }

    [Fact]
    public void EnrichFromTrace_StreamingTimedThenUntimed_SameTool_StaysAligned()
    {
        // Executed 'search' #1 already streaming-timed (50ms real), executed 'search' #2 untimed. Trace queue
        // is [50, 300] in execution order. #1 consumes slot 50 (kept at 50), #2 must get 300 — not 50.
        var start = DateTimeOffset.UtcNow;
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord
        {
            Name = "search", CallId = "c1", Order = 1, WasExecuted = true,
            StartTime = start, EndTime = start + TimeSpan.FromMilliseconds(50),
        });
        report.AddCall(new ToolCallRecord { Name = "search", CallId = "c2", Order = 2, WasExecuted = true });

        ToolUsageExtractor.EnrichFromTrace(report, TraceWithExecutions(("search", 50), ("search", 300)));

        Assert.Equal(TimeSpan.FromMilliseconds(50), report.Calls[0].Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(300), report.Calls[1].Duration);
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
