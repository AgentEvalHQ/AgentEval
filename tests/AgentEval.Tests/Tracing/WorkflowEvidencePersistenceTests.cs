// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Tracing;

namespace AgentEval.Tests.Tracing;

public sealed class WorkflowEvidencePersistenceTests
{
    [Fact]
    public async Task Replay_LegacyTrace_DoesNotInventExecutionOrTraversalEvidence()
    {
        const string json = """
            {"steps":[{"executorId":"Worker","stepIndex":1,"toolCalls":[
              {"name":"lookup","startedAt":"2026-01-01T00:00:00Z"},
              {"name":"lookup","startedAt":"2026-01-01T00:00:00Z","durationMs":0}],
              "incomingEdge":{"from":"Start","to":"Worker","edgeType":"Sequential"}}],
             "graph":{"nodes":["Start","Worker"],"edges":[{"from":"Start","to":"Worker"}],"entryPoint":"Start"}}
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var trace = await WorkflowTraceSerializer.DeserializeAsync(stream);
        var replay = await new WorkflowTraceReplayingAgent(trace).ExecuteWorkflowAsync("input");
        var step = Assert.Single(replay.Steps);
        Assert.Null(replay.Graph!.TraversedEdges);
        Assert.Null(replay.Graph.ParallelBranches);
        Assert.Equal(-1, step.IncomingEdge!.SourceStepIndex);
        Assert.Equal(-1, step.IncomingEdge.TargetStepIndex);
        Assert.All(step.ToolCalls!, call => Assert.False(call.WasExecuted));
        Assert.False(step.ToolCalls![0].HasTiming);
        Assert.True(step.ToolCalls[1].HasTiming);
        Assert.Equal(TimeSpan.Zero, step.ToolCalls[1].Duration);
        Assert.Equal(2, step.ToolCalls.Select(call => call.CallId).Distinct().Count());
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_LateToolCompletions_UpdateTheirOriginalExecutorsOnce()
    {
        var adapter = new MAFWorkflowAdapter("late results", (_, _) => Events(), ["Left", "Right"]);
        await using var recorder = new WorkflowTraceRecorder(adapter, "late tools");
        var live = await recorder.ExecuteWorkflowAsync("input");
        using var stream = new MemoryStream();
        await WorkflowTraceSerializer.SerializeAsync(recorder.Trace, stream);
        stream.Position = 0;
        var trace = await WorkflowTraceSerializer.DeserializeAsync(stream);
        var replay = await new WorkflowTraceReplayingAgent(trace).ExecuteWorkflowAsync("input");
        foreach (var result in new[] { live, replay })
        {
            Assert.Equal(["Left", "Right"], result.Steps.Select(step => step.ExecutorId));
            foreach (var step in result.Steps)
            {
                var call = Assert.Single(step.ToolCalls!);
                Assert.Equal(step.ExecutorId, call.ExecutorId);
                Assert.Equal("shared", call.CallId);
                Assert.True(call.WasExecuted);
                Assert.True(call.HasTiming);
                Assert.Equal(TimeSpan.FromMilliseconds(10), call.Duration);
                Assert.Equal(step.ExecutorId, call.Result);
            }
        }

        static async IAsyncEnumerable<WorkflowEvent> Events()
        {
            await Task.Yield();
            yield return new ExecutorOutputEvent("Left", "left") { StartsNewStep = true };
            yield return new ExecutorToolCallEvent("Left", "lookup", "shared") { HasResult = false };
            yield return new ExecutorOutputEvent("Right", "right") { StartsNewStep = true };
            yield return new ExecutorToolCallEvent("Right", "lookup", "shared") { HasResult = false };
            yield return new ExecutorToolCallEvent("Left", "lookup", "shared", Result: "Left", Duration: TimeSpan.FromMilliseconds(10));
            yield return new ExecutorToolCallEvent("Right", "lookup", "shared", Result: "Right", Duration: TimeSpan.FromMilliseconds(10));
            yield return new WorkflowCompleteEvent();
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("failure", true)]
    public void TraceToolCall_UnexecutedIntent_DistinguishesAbsenceFromRecordedError(string? error, bool failed)
    {
        var call = new TraceToolCall { WasExecuted = false, Succeeded = false, Error = error };
        Assert.Equal(failed, call.Failed);
        Assert.True(new TraceToolCall { Succeeded = false }.Failed); // Legacy recorded failure remains a failure.
    }

    [Fact]
    public void Timeline_UnmeasuredCalls_ExcludePlaceholdersFromCountsAndTimingStatistics()
    {
        var timeline = ToolCallTimeline.Create();
        timeline.TotalDuration = TimeSpan.FromSeconds(1);
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "lookup", StartTime = TimeSpan.Zero, Duration = TimeSpan.FromMilliseconds(999), Succeeded = true,
            OutcomeNotMeasuredReason = "No result", TimingNotMeasuredReason = "No completion"
        });
        Assert.Equal(0, timeline.SuccessfulToolCalls);
        Assert.Equal(0, timeline.FailedToolCalls);
        Assert.Null(timeline.GetToolStatistics()["lookup"].SuccessRate);
        Assert.Null(timeline.SlowestTool);
        Assert.Contains("not measured", timeline.ToAsciiDiagram());
        Assert.Contains("not measured", timeline.ToCompactSummary());

        timeline.AddInvocation(new ToolInvocation
        { ToolName = "lookup", StartTime = TimeSpan.Zero, Duration = TimeSpan.FromMilliseconds(20), Succeeded = true });
        var stats = timeline.GetToolStatistics()["lookup"];
        Assert.Equal(2, timeline.TotalToolCalls);
        Assert.Equal(1, timeline.SuccessfulToolCalls);
        Assert.Equal(1, stats.UnmeasuredOutcomeCount);
        Assert.Equal(1, stats.UnmeasuredDurationCount);
        Assert.Equal(100, stats.SuccessRate);
        Assert.Equal(TimeSpan.FromMilliseconds(20), timeline.TotalToolTime);
        Assert.Equal(TimeSpan.FromMilliseconds(20), timeline.AverageToolDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(20), stats.MinDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(20), stats.MaxDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(20), stats.AverageDuration);
    }
}
