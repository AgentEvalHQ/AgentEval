// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Models;
using AgentEval.Samples;

namespace AgentEval.Tests.Samples;

public sealed class WorkflowWithToolsReportTests
{
    [Theory]
    [InlineData(null, "time not measured")]
    [InlineData(0, "0ms")]
    [InlineData(10, "10ms")]
    public void WriteToolCall_AvailableEvidence_PreservesResultAndTiming(int? duration, string expected)
    {
        using var writer = new StringWriter();
        WorkflowWithToolsReport.WriteToolCall(writer, Call("Lookup", duration), "observed result");
        Assert.Contains($"Lookup ({expected})", writer.ToString());
        Assert.Contains("observed result", writer.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteToolCall_OnlyOneTimingBoundary_DoesNotInventZero(bool hasStart)
    {
        var call = Call("Lookup", null);
        call.StartTime = hasStart ? DateTimeOffset.UnixEpoch : null;
        call.EndTime = hasStart ? null : DateTimeOffset.UnixEpoch;
        using var writer = new StringWriter();
        WorkflowWithToolsReport.WriteToolCall(writer, call, "observed result");
        Assert.Contains("Lookup (time not measured)", writer.ToString());
        Assert.DoesNotContain("(0ms)", writer.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteReport_AllToolTimingMissing_DisclosesMissingEvidence(bool timeline)
    {
        var text = Render(timeline, null, null);
        Assert.Contains("Tool time: not measured (0 measured, 2 unknown)", text);
        Assert.DoesNotContain("Total tool time: 0ms", text);
        Assert.DoesNotContain("%", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteReport_MixedTiming_LabelsKnownSubtotal(bool timeline)
    {
        var text = Render(timeline, 10, null);
        Assert.Contains("Known tool time subtotal: 10ms (1 measured, 1 unknown)", text);
        Assert.DoesNotContain("Total tool time: 10ms", text);
        Assert.DoesNotContain("%", text);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 10)]
    [InlineData(true, 10)]
    public void WriteReport_CompleteTiming_PreservesMeasuredTotal(bool timeline, int duration)
    {
        var text = Render(timeline, duration, duration);
        Assert.Matches($"(?:Total tool time|Tool time): {2 * duration}ms", text);
        Assert.DoesNotContain("unknown", text);
        if (timeline)
            Assert.Contains($"({(2d * duration):F1}%)", text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteReport_NoCalls_DoesNotClaimMeasuredToolTime(bool timeline)
    {
        var text = Render(timeline);
        Assert.Contains("no tool calls", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Total tool time: 0ms", text);
        Assert.DoesNotContain("%", text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void WriteStep_MissingTimingMarker_OverridesNumericPlaceholder(int placeholder)
    {
        var step = Step([]) with
        {
            Duration = TimeSpan.FromMilliseconds(placeholder),
            TimingNotMeasuredReason = "producer timing unavailable"
        };
        using var writer = new StringWriter();
        WorkflowWithToolsReport.WriteStep(writer, step);
        Assert.Contains("Executor (time not measured: producer timing unavailable)", writer.ToString());
        Assert.DoesNotContain($"({placeholder}ms)", writer.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    public void WriteStep_MeasuredTiming_PreservesDuration(int duration)
    {
        using var writer = new StringWriter();
        WorkflowWithToolsReport.WriteStep(writer, Step([]) with { Duration = TimeSpan.FromMilliseconds(duration) });
        Assert.Contains($"Executor ({duration}ms)", writer.ToString());
    }

    [Fact]
    public void WriteTimeline_ZeroRootDuration_DoesNotInventPercentage()
    {
        var timeline = Result(0).Timeline!;
        timeline.TotalDuration = TimeSpan.Zero;
        using var writer = new StringWriter();
        WorkflowWithToolsReport.WriteTimeline(writer, timeline);
        Assert.Matches("(?:Total tool time|Tool time): 0ms", writer.ToString());
        Assert.DoesNotContain("%", writer.ToString());
    }

    [Fact]
    public void WriteTimeline_MissingToolTiming_PreservesWholeWorkflowDuration()
    {
        var text = Render(true, (int?)null);
        Assert.Contains("100ms total", text);
        Assert.Contains("not measured", text);
    }

    private static string Render(bool timeline, params int?[] durations)
    {
        var result = Result(durations);
        using var writer = new StringWriter();
        if (timeline)
            WorkflowWithToolsReport.WriteTimeline(writer, result.Timeline ?? new ToolCallTimeline { TotalDuration = result.TotalDuration });
        else
            WorkflowWithToolsReport.WriteToolSummary(writer, result.ToolUsage ?? new ToolUsageReport());
        return writer.ToString();
    }

    private static WorkflowExecutionResult Result(params int?[] durations) => new()
    {
        FinalOutput = "completed",
        TotalDuration = TimeSpan.FromMilliseconds(100),
        Steps = [Step(durations.Select((duration, index) => Call($"Tool{index}", duration)).ToArray())]
    };

    private static ExecutorStep Step(IReadOnlyList<ToolCallRecord> calls) => new()
    {
        ExecutorId = "Executor", Output = "observed output", ToolCalls = calls
    };

    private static ToolCallRecord Call(string name, int? duration) => new()
    {
        Name = name, CallId = name, WasExecuted = true, Result = "observed result",
        StartTime = DateTimeOffset.UnixEpoch,
        EndTime = duration.HasValue ? DateTimeOffset.UnixEpoch.AddMilliseconds(duration.Value) : null
    };
}
