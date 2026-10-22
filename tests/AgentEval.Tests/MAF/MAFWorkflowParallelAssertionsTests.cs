// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Assertions;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Tracing;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public sealed class MAFWorkflowParallelAssertionsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteWorkflowAsync_SelectedFanOut_AssertionsAndReplayUseObservedBranches(int selected)
    {
        var calls = new int[2];
        var start = ((Func<string, string>)(_ => "start")).BindAsExecutor<string, string>("Start");
        MAFWorkflows.ExecutorBinding Branch(int index, string id) =>
            ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
            {
                Interlocked.Increment(ref calls[index]);
                await Task.Delay(20, ct);
                return id + " reply";
            })).BindAsExecutor<string, string>(id);
        var left = Branch(0, "Left");
        var right = Branch(1, "Right");
        var workflow = new MAFWorkflows.WorkflowBuilder(start)
            .AddFanOutEdge<string>(start, [left, right], (_, _) => Enumerable.Range(0, selected))
            .WithOutputFrom(left, right).Build();
        var adapter = MAFWorkflowAdapter.FromMAFWorkflow(workflow, "selected fan-out", ["Start", "Left", "Right"]);
        await using var recorder = new WorkflowTraceRecorder(adapter, "selected-fanout");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await recorder.ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { selected >= 1 ? 1 : 0, selected == 2 ? 1 : 0 }, calls);
        Assert.Equal(selected + 1, result.Steps.Count);
        Assert.True(result.Graph!.HasParallelExecution); // Declared topology remains available to visualizers.
        AssertObservedParallelism(result, selected);
        Assert.Equal(selected == 2, recorder.Trace.Graph!.HasParallelExecution);

        var trace = JsonSerializer.Deserialize<WorkflowTrace>(JsonSerializer.Serialize(recorder.Trace))!;
        var replay = await new WorkflowTraceReplayingAgent(trace).ExecuteWorkflowAsync("input");
        AssertObservedParallelism(replay, selected);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    public void HaveParallelExecution_RecordedActivationsWithoutTopology_CountsDistinctBranchIds(int steps, int branches)
    {
        var result = new WorkflowExecutionResult
        {
            FinalOutput = "done",
            Steps = Enumerable.Range(0, steps).Select(index => new ExecutorStep
            {
                ExecutorId = "Worker" + index, Output = "done", StepIndex = index,
                ParallelBranchId = "branch-" + index % branches
            }).ToArray()
        };

        Assert.Equal(branches > 1, result.Should().HaveParallelExecution().IsValid);
        Assert.True(result.Should().HaveParallelBranchCount(branches).IsValid);
    }

    private static void AssertObservedParallelism(WorkflowExecutionResult result, int selected)
    {
        Assert.Equal(selected == 2, result.HasParallelExecution);
        Assert.Equal(selected == 2, result.Should().HaveParallelExecution().IsValid);
        var count = result.Should().HaveParallelBranchCount(selected == 2 ? 2 : 0);
        Assert.True(count.IsValid, string.Join("; ", count.Failures));
        foreach (var step in result.Steps.Where(step => step.ExecutorId != "Start"))
            Assert.Equal(selected == 2, result.Should().ForExecutor(step.ExecutorId).BeInParallelBranch().And().IsValid);
    }
}
