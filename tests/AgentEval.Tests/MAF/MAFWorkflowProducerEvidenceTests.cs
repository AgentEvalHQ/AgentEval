// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Diagnostics;
using AgentEval.Assertions;
using AgentEval.MAF;
using AgentEval.Models;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using static Microsoft.Agents.AI.Workflows.OpenTelemetryWorkflowBuilderExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public sealed partial class MAFWorkflowShapesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteWorkflowAsync_QueuedDelivery_UsesProducerTimingOrExplicitlyDeclinesMeasurement(bool telemetry)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = Function("Start", () => "seed");
        var slow = ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
        {
            await Task.Delay(80, ct);
            finished.TrySetResult();
            return "slow reply";
        })).BindAsExecutor<string, string>("Slow");
        var builder = new MAFWorkflows.WorkflowBuilder(start).AddEdge(start, slow).WithOutputFrom(slow);
        if (telemetry) builder.WithOpenTelemetry();
        var workflow = builder.Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await new MAFWorkflowAdapter("queued timing", DelayedEvents, ["Start", "Slow"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess);
        var step = Assert.Single(result.Steps, item => item.ExecutorId == "Slow");
        Assert.Equal("slow reply", step.Output);
        if (telemetry)
        {
            Assert.Null(step.TimingNotMeasuredReason);
            Assert.True(step.Duration >= TimeSpan.FromMilliseconds(40), $"Queued 80 ms operation recorded as {step.Duration}.");
        }
        else Assert.False(string.IsNullOrWhiteSpace(step.TimingNotMeasuredReason));
        Assert.False(result.Should().ForExecutor("Slow").HaveCompletedWithin(TimeSpan.FromMilliseconds(10)).And().IsValid);

        async IAsyncEnumerable<WorkflowEvent> DelayedEvents(string input, [EnumeratorCancellation] CancellationToken ct)
        {
            var first = true;
            await foreach (var item in MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, input, ct))
            {
                // The producer continues while its consumer is paused: both Slow boundary events queue.
                if (first) { first = false; await finished.Task.WaitAsync(ct); }
                yield return item;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteWorkflowAsync_UnavailableProducerSpans_KeepCallerTelemetryConfiguration(bool customSource)
    {
        using var source = new ActivitySource("caller.workflow.telemetry");
        var observed = 0;
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { if (activity.OperationName.StartsWith("executor.process ", StringComparison.Ordinal)) Interlocked.Increment(ref observed); }
        };
        ActivitySource.AddActivityListener(listener);
        var start = Function("Start", () => "reply");
        var workflow = new MAFWorkflows.WorkflowBuilder(start).WithOutputFrom(start)
            .WithOpenTelemetry(options => options.DisableExecutorProcess = !customSource, customSource ? source : null).Build();
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "configured telemetry", ["Start"])
            .ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsSuccess);
        Assert.NotNull(Assert.Single(result.Steps).TimingNotMeasuredReason);
        Assert.Equal(customSource ? 1 : 0, observed);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_ConcurrentRunsWithSameExecutorId_DoNotShareProducerSpans()
    {
        var starts = 0;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WorkflowExecutionResult> Run(int delay)
        {
            var worker = ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
            {
                if (Interlocked.Increment(ref starts) == 2) bothStarted.TrySetResult();
                await bothStarted.Task.WaitAsync(ct);
                await Task.Delay(delay, ct);
                return delay.ToString();
            })).BindAsExecutor<string, string>("Worker");
            var workflow = new MAFWorkflows.WorkflowBuilder(worker).WithOutputFrom(worker).WithOpenTelemetry().Build();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "isolated", ["Worker"]).ExecuteWorkflowAsync("input", cancellation.Token);
        }
        var results = await Task.WhenAll(Run(40), Run(180)).WaitAsync(TimeSpan.FromSeconds(15));
        var fast = Assert.Single(results[0].Steps);
        var slow = Assert.Single(results[1].Steps);
        Assert.Null(fast.TimingNotMeasuredReason);
        Assert.Null(slow.TimingNotMeasuredReason);
        Assert.Equal("40", fast.Output);
        Assert.Equal("180", slow.Output);
        Assert.True(slow.Duration >= TimeSpan.FromMilliseconds(150));
        Assert.True(slow.Duration > fast.Duration);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteWorkflowAsync_ArrayBarrier_PreservesWholePayloadSourceIdentity(int cycles)
    {
        var rounds = 0;
        var produced = new List<string[]>();
        var received = new List<string[]>();
        var start = Function("Start", () => "seed-" + ++rounds);
        var left = ((Func<string, string[]>)(input => Make("left:" + input))).BindAsExecutor<string, string[]>("Left");
        var mid = ((Func<string, string>)(input => input)).BindAsExecutor<string, string>("Mid");
        var right = ((Func<string, string[]>)(input => Make("right:" + input))).BindAsExecutor<string, string[]>("Right");
        var join = ((Func<string[], string>)(input => { received.Add(input); return input[0]; }))
            .BindAsExecutor<string[], string>("Join");
        var workflow = new MAFWorkflows.WorkflowBuilder(start).AddFanOutEdge(start, [left, mid])
            .AddEdge(mid, right).AddFanInBarrierEdge([left, right], join)
            .AddEdge<string>(join, start, input => input?.StartsWith("right:", StringComparison.Ordinal) == true && rounds < cycles)
            .WithOutputFrom(join).Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "array barrier", ["Start", "Left", "Mid", "Right", "Join"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Equal(cycles * 2, received.Count);
        Assert.Equal(cycles * 2, produced.Count);
        Assert.All(produced, payload => Assert.Single(received, actual => ReferenceEquals(actual, payload)));
        var joins = result.Steps.Where(step => step.ExecutorId == "Join").ToArray();
        Assert.Equal(cycles, joins.Length);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var edges = result.Graph!.TraversedEdges!.Where(edge => edge.TargetStepIndex == joins[cycle].StepIndex).ToArray();
            Assert.Equal(["Left", "Right"], edges.Select(edge => edge.SourceExecutorId).Order());
            Assert.All(edges, edge =>
            {
                var sources = result.Steps.Where(step => step.ExecutorId == edge.SourceExecutorId).ToArray();
                Assert.Equal(sources[cycle].StepIndex, edge.SourceStepIndex);
                Assert.Contains(edge, sources[cycle].OutgoingEdges!);
            });
        }

        string[] Make(string value) { var array = new[] { value }; produced.Add(array); return array; }
    }
}

public partial class MAFWorkflowEventBridgeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExecuteWorkflowAsync_CustomToolSource_SeparatesCompletionFromTiming(bool measured, bool late)
    {
        var result = await new MAFWorkflowAdapter("custom tools", (_, _) => Events()).ExecuteWorkflowAsync("input");
        var call = Assert.Single(Assert.Single(result.Steps).ToolCalls!);
        Assert.True(call.WasExecuted);
        Assert.Null(call.Result);
        Assert.Equal(measured, call.HasTiming);
        if (measured) Assert.Equal(TimeSpan.Zero, call.Duration);
        else Assert.Null(call.EndTime);

        async IAsyncEnumerable<WorkflowEvent> Events()
        {
            await Task.Yield();
            yield return new ExecutorOutputEvent("Worker", "reply");
            var completed = new ExecutorToolCallEvent("Worker", "lookup", "call");
            if (!measured) completed = completed with { HasTiming = false };
            if (late) yield return completed with { HasResult = false };
            yield return completed;
            yield return new WorkflowCompleteEvent();
        }
    }

    [Fact]
    public void TimingCapture_ScopedActivities_PreserveDeliveryOrderAndIgnoreOtherScopes()
    {
        using var source = new ActivitySource("Microsoft.Agents.AI.Workflows");
        using var capture = new MAFWorkflowTimingCapture(TimeProvider.System.GetTimestamp());
        using (source.StartActivity("executor.process Outside")) { }
        Assert.Null(capture.Take("Outside"));
        using (capture.Enter())
        {
            using (source.StartActivity("unrelated")) { }
            Assert.Null(capture.Take("unrelated"));
            using var first = source.StartActivity("executor.process Worker");
            var delivery = Assert.IsType<MAFWorkflowTimingCapture.Delivery>(capture.Take("Worker"));
            Assert.Null(delivery.End);
            first!.Stop();
            Assert.True(delivery.End >= delivery.Start);
            Assert.Null(capture.Take("Worker"));
            using var nested = new MAFWorkflowTimingCapture(TimeProvider.System.GetTimestamp());
            using (nested.Enter())
            {
                using (source.StartActivity("executor.process Worker")) { }
                Assert.NotNull(nested.Take("Worker"));
                Assert.Null(capture.Take("Worker"));
            }
            using (source.StartActivity("executor.process Worker")) { }
            Assert.NotNull(capture.Take("Worker"));
            Assert.Null(nested.Take("Worker"));
        }
        using (source.StartActivity("executor.process Outside")) { }
        Assert.Null(capture.Take("Outside"));
    }

    [Fact]
    public async Task TranslateEvents_UnfinishedProducerSpan_CannotCertifyCompletedQueuedEvents()
    {
        using var source = new ActivitySource("Microsoft.Agents.AI.Workflows");
        using var capture = new MAFWorkflowTimingCapture(TimeProvider.System.GetTimestamp());
        using var scope = capture.Enter();
        using var activity = source.StartActivity("executor.process Worker");
        var adapter = new MAFWorkflowAdapter("unfinished span", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(RecordingEvents(SuperStep(0, FunctionDelivery("Worker", "input", "reply"))),
                new HashSet<string>(), cancellationToken: ct, timingCapture: capture));
        var result = await adapter.ExecuteWorkflowAsync("input");
        Assert.Equal("reply", Assert.Single(result.Steps).Output);
        Assert.NotNull(result.Steps[0].TimingNotMeasuredReason);
        Assert.False(result.Should().ForExecutor("Worker").HaveCompletedWithin(TimeSpan.FromMinutes(1)).And().IsValid);
    }

    [Fact]
    public async Task TranslateEvents_ArrayBarrier_PreservesWholePayloadSourceIdentity()
    {
        string[] left = ["left"], right = ["right"];
        var ids = new[] { "Left", "Right", "Join" };
        var graph = new WorkflowGraphSnapshot
        {
            EntryNodeId = "Left", ExitNodeIds = ["Join"], Nodes = ids.Select(id => new WorkflowNode { NodeId = id }).ToArray(),
            Edges = ids.Take(2).Select(id => new WorkflowEdge
            { EdgeId = id + ">Join", SourceExecutorId = id, TargetExecutorId = "Join", EdgeType = EdgeType.ParallelFanIn }).ToArray()
        };
        var events = SuperStep(0, Delivery("Left", "seed", left))
            .Concat(SuperStep(1, Delivery("Right", "seed", right)))
            .Concat(SuperStep(2, Delivery("Join", left, "L"), Delivery("Join", right, "R")));
        var adapter = new MAFWorkflowAdapter("array evidence", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(RecordingEvents(events), new HashSet<string>(),
                cancellationToken: ct, executorOrder: ids, graph: graph), ids, graphDefinition: graph);
        var result = await adapter.ExecuteWorkflowAsync("input");
        Assert.Equal(new[] { (0, 2), (1, 2) }, result.Graph!.TraversedEdges!
            .Select(edge => (edge.SourceStepIndex, edge.TargetStepIndex)).Order());

        static MAFWorkflows.WorkflowEvent[] Delivery(string id, object input, object output) =>
            [new MAFWorkflows.ExecutorInvokedEvent(id, input), new MAFWorkflows.ExecutorCompletedEvent(id, output)];
    }
}
