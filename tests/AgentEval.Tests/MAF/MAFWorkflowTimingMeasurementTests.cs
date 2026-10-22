// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Models.Serialization;
using AgentEval.Tracing;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using ChatRole = Microsoft.Extensions.AI.ChatRole;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using static Microsoft.Agents.AI.Workflows.OpenTelemetryWorkflowBuilderExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public sealed class MAFWorkflowTimingMeasurementTests : IDisposable
{
    private readonly MAFWorkflowInstrumentation _timing = new();
    public void Dispose() => _timing.Dispose();

    [Fact]
    public async Task ExecuteWorkflowAsync_TraceRoundTrip_PreservesUnavailableTimingAndOmitsPartialTotals()
    {
        await using var recorder = new WorkflowTraceRecorder(UnavailableSource(), "timing-roundtrip");
        var original = await recorder.ExecuteWorkflowAsync("input");
        var trace = JsonSerializer.Deserialize<WorkflowTrace>(JsonSerializer.Serialize(recorder.Trace))!;
        var replay = await new WorkflowTraceReplayingAgent(trace).ExecuteWorkflowAsync("input");

        Assert.False(replay.Should().ForStep(0).HaveCompletedWithin(TimeSpan.FromSeconds(60)).And().IsValid);
        Assert.True(replay.Should().ForStep(1).HaveCompletedWithin(TimeSpan.FromSeconds(60)).And().IsValid);
        Assert.DoesNotContain("Worker", trace.Performance!.DurationByExecutor!.Keys);
        Assert.Equal(0, trace.Performance.DurationByExecutor["Measured"]);
        Assert.Equal(original.Steps[0].TimingNotMeasuredReason, replay.Steps[0].TimingNotMeasuredReason);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_JsonExport_PreservesUnavailableTimingReason()
    {
        var result = await UnavailableSource().ExecuteWorkflowAsync("input");
        using var json = JsonDocument.Parse(WorkflowSerializer.ToJson(result));
        var step = json.RootElement.GetProperty("executionTrace").GetProperty("steps")[0];
        Assert.True(step.TryGetProperty("timingNotMeasuredReason", out var reason));
        Assert.Equal(result.Steps[0].TimingNotMeasuredReason, reason.GetString());
        using var timeline = JsonDocument.Parse(WorkflowSerializer.ToTimelineJson(result));
        var end = timeline.RootElement.GetProperty("events").EnumerateArray()
            .First(evt => evt.GetProperty("type").GetString() == "step-end" && evt.GetProperty("executorId").GetString() == "Worker");
        Assert.Equal(JsonValueKind.Null, end.GetProperty("details").GetProperty("duration").ValueKind);
        Assert.Equal(reason.GetString(), end.GetProperty("details").GetProperty("timingNotMeasuredReason").GetString());
    }

    private static MAFWorkflowAdapter UnavailableSource() => new("unavailable source", (_, _) => Events());

    private static async IAsyncEnumerable<WorkflowEvent> Events()
    {
        await Task.Yield();
        yield return new ExecutorOutputEvent("Worker", "partial") { TimingNotMeasuredReason = "Completion missing." };
        yield return new ExecutorOutputEvent("Worker", "next turn")
            { StartsNewStep = true, StartOffset = TimeSpan.Zero, Duration = TimeSpan.FromMilliseconds(5) };
        yield return new ExecutorOutputEvent("Measured", "done") { StartOffset = TimeSpan.Zero, Duration = TimeSpan.Zero };
        yield return new WorkflowCompleteEvent();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteWorkflowAsync_ExplicitUnavailableTiming_OverridesNumbersUntilACompleteMeasurement(bool laterMeasurement)
    {
        var adapter = new MAFWorkflowAdapter("timing update", (_, _) => Source());
        var result = await adapter.ExecuteWorkflowAsync("input");
        var step = Assert.Single(result.Steps);
        Assert.Equal(laterMeasurement, result.Should().ForExecutor("Worker").HaveCompletedWithin(TimeSpan.FromSeconds(60)).And().IsValid);
        Assert.Equal(laterMeasurement ? TimeSpan.FromMilliseconds(5) : TimeSpan.Zero, step.Duration);
        Assert.Equal(laterMeasurement ? null : "Still running.", step.TimingNotMeasuredReason);

        async IAsyncEnumerable<WorkflowEvent> Source()
        {
            await Task.Yield();
            yield return new ExecutorOutputEvent("Worker", "part")
                { StartOffset = TimeSpan.Zero, Duration = TimeSpan.FromSeconds(99), TimingNotMeasuredReason = "Still running." };
            if (laterMeasurement)
                yield return new ExecutorOutputEvent("Worker", " done") { StartOffset = TimeSpan.Zero, Duration = TimeSpan.FromMilliseconds(5) };
            yield return new WorkflowCompleteEvent();
        }
    }

    [Theory]
    [InlineData("unfinished")]
    [InlineData("response-before-end")]
    [InlineData("missing-start")]
    [InlineData("accumulation-only")]
    public async Task ExecuteWorkflowAsync_IncompleteTiming_CannotPassAnExecutorTimeLimit(string shape)
    {
        var events = new List<MAFWorkflows.WorkflowEvent>();
        if (shape != "missing-start")
            events.Add(new MAFWorkflows.ExecutorInvokedEvent("Worker", shape == "accumulation-only"
                ? new ChatMessage(ChatRole.User, "input") : new MAFWorkflows.TurnToken(true)));
        if (shape == "response-before-end")
            events.Add(new MAFWorkflows.AgentResponseEvent("Worker",
                new Microsoft.Agents.AI.AgentResponse(new ChatMessage(ChatRole.Assistant, "reply"))));
        if (shape is "missing-start" or "accumulation-only")
            events.Add(new MAFWorkflows.ExecutorCompletedEvent("Worker", null));
        var result = await Translate(events, ["Worker"]);

        AssertTimingUnavailable(result, "Worker");
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_RealSubworkflowHost_MeasuresItsChildLifetime()
    {
        var child = ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
        {
            await Task.Delay(30, ct);
            return "child reply";
        })).BindAsExecutor<string, string>("Child");
        var inner = new MAFWorkflows.WorkflowBuilder(child).WithOutputFrom(child).BuildWithTiming(_timing);
        var host = new MAFWorkflows.SubworkflowBinding(inner, "Sub", null);
        var outer = new MAFWorkflows.WorkflowBuilder(host).WithOutputFrom(host).BuildWithTiming(_timing);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(outer, "nested timing", ["Sub", "Child"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Contains(result.Steps, step => step.Output.Contains("child reply"));
        var hostStep = Assert.Single(result.Steps, step => step.ExecutorId == "Sub");
        var childStep = Assert.Single(result.Steps, step => step.ExecutorId == "Sub/Child");
        Assert.Null(hostStep.TimingNotMeasuredReason);
        Assert.True(hostStep.Duration >= childStep.Duration);
        Assert.True(hostStep.Duration >= TimeSpan.FromMilliseconds(25));
        Assert.False(result.Should().ForExecutor("Sub").HaveCompletedWithin(TimeSpan.FromMilliseconds(1)).And().IsValid);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_MeasuredZeroDuration_RemainsMeasuredAndPassesLimit()
    {
        var result = await Translate([
            new MAFWorkflows.ExecutorInvokedEvent("Worker", "input"),
            new MAFWorkflows.ExecutorCompletedEvent("Worker", "done")], []);
        var step = Assert.Single(result.Steps);
        Assert.Equal(TimeSpan.Zero, step.Duration);
        Assert.True(result.Should().ForExecutor("Worker").HaveCompletedWithin(TimeSpan.Zero).And().IsValid);
    }

    private static void AssertTimingUnavailable(WorkflowExecutionResult result, string executorId)
    {
        var step = Assert.Single(result.Steps, step => step.ExecutorId == executorId);
        var check = result.Should().ForExecutor(executorId)
            .HaveCompletedWithin(TimeSpan.FromSeconds(60), because: "a budget requires timing evidence").And();
        Assert.False(check.IsValid);
        var message = Assert.Single(check.Failures);
        Assert.Contains("time not measured", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a budget requires timing evidence", message);
        // Reflection keeps the fail-first test runnable before the additive model property exists.
        var reason = Assert.IsType<string>(typeof(ExecutorStep).GetProperty("TimingNotMeasuredReason")?.GetValue(step));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains(reason, message);
        var exception = Assert.Throws<WorkflowAssertionException>(() => check.Validate());
        Assert.Contains("Record executor invocation and completion before checking its duration", exception.Suggestions!);
    }

    private static Task<WorkflowExecutionResult> Translate(IEnumerable<MAFWorkflows.WorkflowEvent> events, string[] chatIds)
    {
        var adapter = new MAFWorkflowAdapter("timing evidence", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(Replay(), chatIds.ToHashSet(), cancellationToken: ct,
                timeProvider: new FakeTimeProvider()));
        return adapter.ExecuteWorkflowAsync("input");

        async IAsyncEnumerable<MAFWorkflows.WorkflowEvent> Replay()
        {
            foreach (var evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
