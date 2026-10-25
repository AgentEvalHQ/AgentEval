// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public sealed class MAFWorkflowCancellationTests
{
    [Fact]
    public async Task RunWorkflowTestAsync_RealWorkflowTimesOut_DoesNotPassPartialExecution()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = CreateWorkflow(started, block: true);
        var harness = new WorkflowEvaluationHarness(verbose: false);

        var result = await harness.RunWorkflowTestAsync(workflow,
            new WorkflowTestCase { Name = "real timeout", Input = "input" },
            new WorkflowTestOptions { Timeout = TimeSpan.FromSeconds(5) })
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(started.Task.IsCompletedSuccessfully, "The real executor must start before its timeout.");
        Assert.False(result.Passed);
        Assert.IsType<TimeoutException>(result.Error);
        Assert.Null(result.ExecutionResult);
        Assert.Empty(result.AssertionResults!);
        Assert.Contains(result.FailureMessages!, message => message.Contains("Timeout", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_RealWorkflowCancelledAfterStarting_PropagatesCancellation(bool throughHarness)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = CreateWorkflow(started, block: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task execution = throughHarness
            ? new WorkflowEvaluationHarness(verbose: false).RunWorkflowTestAsync(workflow,
                new WorkflowTestCase { Name = "caller cancellation", Input = "input" },
                new WorkflowTestOptions { Timeout = TimeSpan.FromSeconds(30) }, cancellation.Token)
            : workflow.ExecuteWorkflowAsync("input", cancellation.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task RunWorkflowTestAsync_RealWorkflowCompletes_Passes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await new WorkflowEvaluationHarness(verbose: false).RunWorkflowTestAsync(
            CreateWorkflow(started, block: false),
            new WorkflowTestCase { Name = "completion control", Input = "input", ExpectedOutputContains = "done:" });

        Assert.True(started.Task.IsCompletedSuccessfully);
        Assert.True(result.Passed);
        Assert.Null(result.Error);
        Assert.Equal("done:first:input", result.ExecutionResult!.FinalOutput);
    }

    private static MAFWorkflowAdapter CreateWorkflow(TaskCompletionSource started, bool block)
    {
        var first = ((Func<string, ValueTask<string>>)(input => ValueTask.FromResult("first:" + input)))
            .BindAsExecutor<string, string>("First");
        var second = ((Func<string, CancellationToken, ValueTask<string>>)(async (input, ct) =>
        {
            started.TrySetResult();
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return "done:" + input;
        })).BindAsExecutor<string, string>("Second");
        var workflow = new MAFWorkflows.WorkflowBuilder(first).AddEdge(first, second).WithOutputFrom(second).Build();
        return MAFWorkflowAdapter.FromMAFWorkflow(workflow, "real cancellation", ["First", "Second"]);
    }
}
