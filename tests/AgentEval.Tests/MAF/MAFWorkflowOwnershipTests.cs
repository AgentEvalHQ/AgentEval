// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Assertions;
using AgentEval.MAF;
using AgentEval.Tracing;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using static Microsoft.Agents.AI.Workflows.OpenTelemetryWorkflowBuilderExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public partial class MAFWorkflowEventBridgeTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ExecuteWorkflowAsync_UnmeasuredToolBudget_CannotCertifyLiveOrReplay(
        bool scoped, bool replayed, bool completed)
    {
        var agent = new RecordingToolAgent(completed).BindAsExecutor(emitEvents: true);
        var workflow = new MAFWorkflows.WorkflowBuilder(agent).WithOutputFrom(agent).Build();
        await using var recorder = new WorkflowTraceRecorder(
            MAFWorkflowAdapter.FromMAFWorkflow(workflow, "tool budget", ["Caller"]), "tools");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await recorder.ExecuteWorkflowAsync("input", cancellation.Token);
        if (replayed)
        {
            var trace = JsonSerializer.Deserialize<WorkflowTrace>(JsonSerializer.Serialize(recorder.Trace))!;
            result = await new WorkflowTraceReplayingAgent(trace).ExecuteWorkflowAsync("input");
        }
        var call = Assert.Single(Assert.Single(result.Steps).ToolCalls!);
        Assert.Equal(completed, call.WasExecuted);
        Assert.False(call.HasTiming);
        var check = scoped
            ? result.Should().ForExecutor("Caller").HaveCalledTool(call.Name)
                .WithDurationUnder(TimeSpan.FromMinutes(1), "a budget needs evidence").Done()
            : result.Should().HaveCalledTool(call.Name)
                .WithDurationUnder(TimeSpan.FromMinutes(1), "a budget needs evidence").And();
        Assert.False(check.IsValid);
        var message = Assert.Single(check.Failures);
        Assert.Contains("Expected", message);
        Assert.Contains("Actual", message);
        Assert.Contains("Suggestions", message);
        Assert.Contains("not measured", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a budget needs evidence", message);
        Assert.Throws<WorkflowAssertionException>(() => check.Validate());
    }

    [Theory]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, true)]
    [InlineData(false, 5, true)]
    [InlineData(true, 5, true)]
    [InlineData(false, 20, false)]
    [InlineData(true, 20, false)]
    public async Task ExecuteWorkflowAsync_MeasuredToolBudget_PreservesZeroAndThresholds(bool scoped, int milliseconds, bool valid)
    {
        var result = await new MAFWorkflowAdapter("known tool", (_, _) => Events()).ExecuteWorkflowAsync("input");
        var call = Assert.Single(Assert.Single(result.Steps).ToolCalls!);
        Assert.True(call.HasTiming);
        var check = scoped
            ? result.Should().ForExecutor("Caller").HaveCalledTool("lookup").WithDurationUnder(TimeSpan.FromMilliseconds(10)).Done()
            : result.Should().HaveCalledTool("lookup").WithDurationUnder(TimeSpan.FromMilliseconds(10)).And();
        Assert.Equal(valid, check.IsValid);

        async IAsyncEnumerable<WorkflowEvent> Events()
        {
            await Task.Yield();
            yield return new ExecutorOutputEvent("Caller", "reply");
            yield return new ExecutorToolCallEvent("Caller", "lookup", "call", Duration: TimeSpan.FromMilliseconds(milliseconds));
            yield return new WorkflowCompleteEvent();
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, true, true)]
    public async Task ExecuteWorkflowAsync_RawNestedRun_CannotReplaceOuterDuration(
        bool outerProcess, bool innerSession, bool sameId, bool instrumented)
    {
        using var timing = new MAFWorkflowInstrumentation();
        var outerCalls = 0;
        var innerCalls = 0;
        var innerWorker = ((Func<string, string>)(_ => { innerCalls++; return "inner"; }))
            .BindAsExecutor<string, string>(sameId ? "Worker" : "Inner");
        var inner = new MAFWorkflows.WorkflowBuilder(innerWorker).WithOutputFrom(innerWorker)
            .WithOpenTelemetry(options => options.DisableWorkflowRun = !innerSession).Build();
        var worker = ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
        {
            var turn = ++outerCalls;
            await using var run = await MAFWorkflows.InProcessExecution.RunAsync(inner, "input", cancellationToken: ct);
            await Task.Delay(80, ct);
            return "outer-" + turn;
        })).BindAsExecutor<string, string>("Worker");
        var builder = new MAFWorkflows.WorkflowBuilder(worker).AddEdge<string>(worker, worker, _ => outerCalls < 2).WithOutputFrom(worker);
        var outer = instrumented && outerProcess ? timing.Build(builder)
            : builder.WithOpenTelemetry(options => options.DisableExecutorProcess = !outerProcess).Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(outer, "nested ownership", ["Worker"])
            .ExecuteWorkflowAsync("input", cancellation.Token);
        Assert.True(result.IsSuccess);
        Assert.Equal(2, outerCalls);
        Assert.Equal(2, innerCalls);
        Assert.Equal(["outer-1", "outer-2"], result.Steps.Select(step => step.Output));
        foreach (var step in result.Steps)
        {
            if (instrumented && outerProcess) Assert.Null(step.TimingNotMeasuredReason);
            if (step.TimingNotMeasuredReason is null)
                Assert.True(step.Duration >= TimeSpan.FromMilliseconds(60), $"80 ms outer operation reported as {step.Duration}.");
            Assert.False(result.Should().ForStep(step.StepIndex).HaveCompletedWithin(TimeSpan.FromMilliseconds(10)).And().IsValid);
        }
    }
}
