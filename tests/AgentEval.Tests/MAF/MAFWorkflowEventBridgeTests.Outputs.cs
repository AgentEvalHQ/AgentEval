// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public partial class MAFWorkflowEventBridgeTests
{
    [Fact]
    public async Task FromMAFWorkflow_EarlyOutput_RecordsEveryLaterStep()
    {
        var first = CreateFuncBinding("First", "first");
        var middle = CreateFuncBinding("Middle", "middle");
        var last = CreateFuncBinding("Last", "last");
        var workflow = new MAFWorkflows.WorkflowBuilder(first).AddEdge(first, middle).AddEdge(middle, last)
            .WithOutputFrom(first, last).Build();
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "early output", ["First", "Middle", "Last"])
            .ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess);
        Assert.Equal(["First:first", "Middle:middle", "Last:last"], result.Steps.Select(s => $"{s.ExecutorId}:{s.Output}"));
        Assert.Equal("last", result.FinalOutput);
    }

    [Fact]
    public async Task FromMAFWorkflow_OutputOnEachLoopIteration_RecordsAllIterations()
    {
        var count = 0;
        var loop = ((Func<string, ValueTask<string>>)(_ => ValueTask.FromResult($"pass-{++count}")))
            .BindAsExecutor<string, string>("Loop");
        var last = CreateFuncBinding("Last", "done");
        var workflow = new MAFWorkflows.WorkflowBuilder(loop)
            .AddEdge<string>(loop, loop, _ => count < 2).AddEdge<string>(loop, last, _ => count >= 2)
            .WithOutputFrom(loop, last).Build();
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "loop outputs", ["Loop", "Last"])
            .ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["Loop:pass-1", "Loop:pass-2", "Last:done"], result.Steps.Select(s => $"{s.ExecutorId}:{s.Output}"));
        Assert.Equal("done", result.FinalOutput);
    }

    [Fact]
    public async Task FromMAFWorkflow_MultipleIdenticalYields_PreservesEachYieldWithoutDuplicatingReturn()
    {
        var binding = ((Func<string, MAFWorkflows.IWorkflowContext, CancellationToken, ValueTask<string>>)Emit)
            .BindAsExecutor<string, string>("Emitter");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding).WithOutputFrom(binding).Build();
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "multiple outputs", ["Emitter"])
            .ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(e => e.Message) ?? []));
        Assert.Equal("oneonetwo", Assert.Single(result.Steps).Output);
        Assert.Equal("oneonetwo", result.FinalOutput);

        [MAFWorkflows.YieldsOutput(typeof(string))]
        static async ValueTask<string> Emit(string input, MAFWorkflows.IWorkflowContext context, CancellationToken ct)
        {
            await context.YieldOutputAsync("one", ct);
            await context.YieldOutputAsync("one", ct);
            return "two";
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FromMAFWorkflow_AgentBuilderChatList_RendersAllOutputMessagesWithoutInput(bool concurrent)
    {
        AIAgent[] agents = [new FakeStreamableAIAgent("Left", "left reply"), new FakeStreamableAIAgent("Right", "right reply")];
        var workflow = concurrent ? MAFWorkflows.AgentWorkflowBuilder.BuildConcurrent(agents)
            : MAFWorkflows.AgentWorkflowBuilder.BuildSequential(agents);
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "chat-list output", ["Left", "Right"])
            .ExecuteWorkflowAsync("private input marker").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.IsSuccess);
        Assert.Contains("left reply", result.FinalOutput);
        Assert.Contains("right reply", result.FinalOutput);
        Assert.DoesNotContain("private input marker", result.FinalOutput);
        Assert.DoesNotContain("System.Collections", result.FinalOutput);
    }

    [Fact]
    public async Task FromMAFWorkflow_ErrorAfterEarlyOutput_RecordsTheError()
    {
        var first = CreateFuncBinding("First", "partial");
        var failing = ((Func<string, ValueTask<string>>)(_ => throw new InvalidOperationException("late failure")))
            .BindAsExecutor<string, string>("Failing");
        var workflow = new MAFWorkflows.WorkflowBuilder(first).AddEdge(first, failing).WithOutputFrom(first).Build();
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "late error", ["First", "Failing"])
            .ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, error => error.Message.Contains("late failure"));
    }

    [Fact]
    public async Task FromMAFWorkflow_RequestAfterEarlyOutput_RecordsPendingRequest()
    {
        var first = CreateFuncBinding("First", "partial");
        var port = MAFWorkflows.RequestPort.Create<string, string>("Approval");
        var workflow = new MAFWorkflows.WorkflowBuilder(first).AddEdge(first, port).WithOutputFrom(first).Build();
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "late request", ["First", "Approval"])
            .ExecuteWorkflowAsync("input").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Errors!, error => error.ExecutorId == "Approval");
    }

    [Fact]
    public async Task TranslateEvents_CancelledAfterEarlyOutput_DoesNotCompleteNormally()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new MAFWorkflowAdapter("late cancellation", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(Events(), new HashSet<string>(), cancellationToken: ct), ["First"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ExecuteWorkflowAsync("input", cancellation.Token));

        async IAsyncEnumerable<MAFWorkflows.WorkflowEvent> Events()
        {
            await Task.Yield();
            yield return new MAFWorkflows.ExecutorInvokedEvent("First", "input");
            yield return new MAFWorkflows.ExecutorCompletedEvent("First", "partial");
            yield return new MAFWorkflows.WorkflowOutputEvent("partial", "First");
            cancellation.Cancel();
        }
    }
}
