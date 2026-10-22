// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.MAF;
using AgentEval.Tests.Snapshots;
using Microsoft.Agents.AI.Workflows;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;

namespace AgentEval.Tests.MAF;

public sealed class MAFWorkflowInstrumentationTests
{
    [Fact]
    public Task PublicSurface_MatchesApprovedContract() =>
        Verify(PublicApiSurface.Render([typeof(MAFWorkflowInstrumentation)], type => type == typeof(MAFWorkflowInstrumentation)));

    [Fact]
    public async Task ExecuteWorkflowAsync_ReusedWorkflow_PreservesStateAndDeclinesTimingAfterDisposal()
    {
        using var timing = new MAFWorkflowInstrumentation();
        var calls = 0;
        var worker = ((Func<string, string>)(_ => (++calls).ToString())).BindAsExecutor<string, string>("Worker");
        var workflow = timing.Build(new WorkflowBuilder(worker).WithOutputFrom(worker));
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "state", ["Worker"]);
        var first = await adapter.ExecuteWorkflowAsync("input");
        var second = await adapter.ExecuteWorkflowAsync("input");
        Assert.Equal("1", first.FinalOutput);
        Assert.Equal("2", second.FinalOutput);
        Assert.Null(Assert.Single(first.Steps).TimingNotMeasuredReason);
        Assert.Null(Assert.Single(second.Steps).TimingNotMeasuredReason);
        timing.Dispose();
        var third = await adapter.ExecuteWorkflowAsync("input");
        Assert.True(third.IsSuccess);
        Assert.Equal("3", third.FinalOutput);
        Assert.NotNull(Assert.Single(third.Steps).TimingNotMeasuredReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteWorkflowAsync_InterruptedRun_DoesNotLeakCaptureIntoNextRun(bool cancellation)
    {
        using var timing = new MAFWorkflowInstrumentation();
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var worker = ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
        {
            if (++calls == 1)
            {
                if (cancellation) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
                throw new InvalidOperationException("observed executor failure");
            }
            await Task.Delay(40, ct);
            return "recovered";
        })).BindAsExecutor<string, string>("Worker");
        var workflow = timing.Build(new WorkflowBuilder(worker).WithOutputFrom(worker));
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "recovery", ["Worker"]);
        if (cancellation)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExecuteWorkflowAsync("input", stop.Token));
        else
            Assert.False((await adapter.ExecuteWorkflowAsync("input")).IsSuccess);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await adapter.ExecuteWorkflowAsync("input", timeout.Token);
        Assert.True(result.IsSuccess);
        Assert.Equal("recovered", result.FinalOutput);
        var step = Assert.Single(result.Steps);
        Assert.Null(step.TimingNotMeasuredReason);
        Assert.True(step.Duration >= TimeSpan.FromMilliseconds(30));
    }

    [Fact]
    public void Build_InvalidOrDisposedOwner_RefusesUse()
    {
        using var owner = new MAFWorkflowInstrumentation();
        Assert.Throws<ArgumentNullException>(() => owner.Build(null!));
        owner.Dispose();
        var worker = ((Func<string, string>)(s => s)).BindAsExecutor<string, string>("Worker");
        Assert.Throws<ObjectDisposedException>(() => owner.Build(new WorkflowBuilder(worker)));
    }

    [Fact]
    public void Build_RejectedTopology_DisposesItsSourceAndAllowsLaterBuild()
    {
        using var owner = new MAFWorkflowInstrumentation();
        ActivitySource? rejected = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AgentEval.MAF.WorkflowTiming",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => { if (activity.OperationName == "workflow.build") rejected = activity.Source; }
        };
        ActivitySource.AddActivityListener(listener);
        var start = ((Func<string, string>)(s => s)).BindAsExecutor<string, string>("Start");
        var orphan = ((Func<string, string>)(s => s)).BindAsExecutor<string, string>("Orphan");
        Assert.ThrowsAny<Exception>(() => owner.Build(new WorkflowBuilder(start).BindExecutor(orphan)));
        Assert.NotNull(rejected);
        Assert.False(rejected.HasListeners());
        Assert.NotNull(owner.Build(new WorkflowBuilder(start).WithOutputFrom(start)));
    }

    [Fact]
    public void Capture_WrongSessionAndSource_DoNotBecomeDeclaredOperationTiming()
    {
        using var source = new ActivitySource("same.name");
        using var unrelated = new ActivitySource("same.name");
        using var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, unrelated),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using var capture = new MAFWorkflowTimingCapture(TimeProvider.System.GetTimestamp(),
            new Dictionary<string, ActivitySource> { ["Worker"] = source, ["Child"] = unrelated });
        using (capture.Enter())
        {
            using (var otherSession = source.StartActivity("workflow.session"))
            {
                otherSession!.SetTag("session.id", "another-run");
                using (source.StartActivity("executor.process Worker")) { }
            }
            using var session = source.StartActivity("workflow.session");
            session!.SetTag("session.id", capture.SessionId);
            using (unrelated.StartActivity("executor.process Worker")) { }
            using (source.StartActivity("executor.process Worker")) { }
        }
        var delivery = Assert.IsType<MAFWorkflowTimingCapture.Delivery>(capture.Take("Worker"));
        Assert.NotNull(delivery.End);
        Assert.Null(capture.Take("Worker"));
    }

    [Fact]
    public void Capture_MissingDelivery_CannotBorrowNextDelivery()
    {
        using var source = new ActivitySource("missing.delivery");
        using var capture = new MAFWorkflowTimingCapture(TimeProvider.System.GetTimestamp(),
            new Dictionary<string, ActivitySource> { ["Worker"] = source });
        Assert.Null(capture.Take("Worker"));
        using (capture.Enter())
        {
            using var session = source.StartActivity("workflow.session");
            session!.SetTag("session.id", capture.SessionId);
            using (source.StartActivity("executor.process Worker")) { }
        }
        Assert.Null(capture.Take("Worker"));
    }
}
