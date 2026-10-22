// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using static Microsoft.Agents.AI.Workflows.OpenTelemetryWorkflowBuilderExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

public sealed partial class MAFWorkflowShapesTests
{
    [Fact]
    public async Task ExecuteWorkflowAsync_ConditionalJoinWithEqualPayloads_DoesNotInventSenderEvidence()
    {
        var start = Function("Start", () => "start");
        var left = Function("Left", () => "same");
        var right = Function("Right", () => "same");
        var joinCalls = 0;
        var join = Function("Join", () => { joinCalls++; return "joined"; });
        var workflow = new MAFWorkflows.WorkflowBuilder(start).AddFanOutEdge(start, [left, right])
            .AddEdge<string>(left, join, _ => true).AddEdge<string>(right, join, _ => false).WithOutputFrom(join).WithOpenTelemetry().Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "ambiguous sender", ["Start", "Left", "Right", "Join"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, joinCalls);
        Assert.Equal("joined", result.FinalOutput);
        Assert.Equal(["Start", "Left", "Right", "Join"], result.Steps.Select(step => step.ExecutorId));
        // MAF's public delivery event carries the equal payload, but neither sender nor evaluated condition.
        Assert.DoesNotContain(result.Graph!.TraversedEdges!, edge => edge.TargetExecutorId == "Join");
        Assert.Equal(2, result.Graph.TraversedEdges!.Count);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_FunctionFanOutAndFanIn_PreservesEachActivationAndActualEdges()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        MAFWorkflows.ExecutorBinding Branch(string id, int delay) =>
            ((Func<string, CancellationToken, ValueTask<string>>)(async (_, ct) =>
            {
                if (Interlocked.Increment(ref starts) == 2) bothStarted.TrySetResult();
                await bothStarted.Task.WaitAsync(ct);
                await Task.Delay(delay, ct);
                return id + " reply";
            })).BindAsExecutor<string, string>(id);
        var start = Function("Start", () => "start");
        var left = Branch("Left", 60);
        var right = Branch("Right", 30);
        var joinCalls = 0;
        // The barrier releases each source message; it does not aggregate them into an array.
        var join = ((Func<string, ValueTask<string>>)(input =>
        {
            Interlocked.Increment(ref joinCalls);
            return ValueTask.FromResult(input + "|");
        })).BindAsExecutor<string, string>("Join");
        var workflow = new MAFWorkflows.WorkflowBuilder(start).AddFanOutEdge(start, [left, right])
            .AddFanInBarrierEdge([left, right], join).WithOutputFrom(join).WithOpenTelemetry().Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "barrier", ["Start", "Right", "Left", "Join"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(bothStarted.Task.IsCompletedSuccessfully);
        Assert.Equal(2, joinCalls);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(e => e.Message) ?? []));
        Assert.Equal(["Start", "Right", "Left", "Join"], result.Steps.Select(step => step.ExecutorId));
        Assert.Equal(["start", "Right reply", "Left reply"], result.Steps.Take(3).Select(step => step.Output));
        var joinStep = Assert.Single(result.Steps, step => step.ExecutorId == "Join");
        Assert.Equal(["Left reply", "Right reply"], joinStep.Output.Split('|', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        Assert.Equal(joinStep.Output, result.FinalOutput);
        var leftStep = Assert.Single(result.Steps, step => step.ExecutorId == "Left");
        var rightStep = Assert.Single(result.Steps, step => step.ExecutorId == "Right");
        Assert.True(leftStep.Duration >= TimeSpan.FromMilliseconds(30));
        Assert.True(rightStep.Duration >= TimeSpan.FromMilliseconds(15));
        Assert.True(leftStep.StartOffset < rightStep.StartOffset + rightStep.Duration);
        Assert.True(rightStep.StartOffset < leftStep.StartOffset + leftStep.Duration);
        Assert.Equal(2, result.Graph!.ParallelBranches!.Count);
        foreach (var step in new[] { leftStep, rightStep })
        {
            var branch = Assert.Single(result.Graph.ParallelBranches, branch => branch.BranchId == step.ParallelBranchId);
            Assert.Equal([step.ExecutorId], branch.ExecutorIds);
            Assert.Equal(step.StartOffset, branch.StartOffset);
            Assert.Equal(step.Duration, branch.Duration);
        }
        Assert.Equal(["Left->Join:ParallelFanIn", "Right->Join:ParallelFanIn", "Start->Left:ParallelFanOut", "Start->Right:ParallelFanOut"],
            result.Graph!.TraversedEdges!.Select(edge => $"{edge.SourceExecutorId}->{edge.TargetExecutorId}:{edge.EdgeType}").Order(StringComparer.Ordinal));
        Assert.All(result.Graph.TraversedEdges, edge =>
        {
            Assert.Equal(edge.SourceExecutorId, result.Steps[edge.SourceStepIndex].ExecutorId);
            Assert.Equal(edge.TargetExecutorId, result.Steps[edge.TargetStepIndex].ExecutorId);
        });
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_AgentFanOut_KeepsToolsAndTextOnTheirOwner()
    {
        static MAFWorkflows.ExecutorBinding ToolAgent(string name, int delay)
        {
            var tool = AIFunctionFactory.Create((int step) => name + " result " + step, "Lookup" + name);
            return new ChatClientAgent(new BridgeScriptedChatClient(name, ["Lookup" + name], TimeSpan.FromMilliseconds(delay)),
                name: name, tools: [tool]).BindAsExecutor(emitEvents: true);
        }
        var start = new MAFWorkflows.ChatForwardingExecutor("Start");
        var left = ToolAgent("Left", 40);
        var right = ToolAgent("Right", 20);
        var workflow = new MAFWorkflows.WorkflowBuilder(start).AddFanOutEdge(start, [left, right])
            .WithOutputFrom(left, right).WithOpenTelemetry().Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "agent fan-out", ["Start", "Right", "Left"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.IsSuccess);
        Assert.Equal(["Start", "Right", "Left"], result.Steps.Select(step => step.ExecutorId));
        foreach (var name in new[] { "Left", "Right" })
        {
            var step = Assert.Single(result.Steps, candidate => candidate.ExecutorId == name);
            Assert.Equal(BridgeScriptedChatClient.FinalReply(name, 1), step.Output);
            var call = Assert.Single(step.ToolCalls!);
            Assert.Equal(name, call.ExecutorId);
            Assert.Equal("Lookup" + name, call.Name);
            Assert.True(call.WasExecuted);
            Assert.Contains(name + " result", call.Result?.ToString());
        }
        Assert.Equal(["Start->Left", "Start->Right"], result.Graph!.TraversedEdges!
            .Select(edge => $"{edge.SourceExecutorId}->{edge.TargetExecutorId}").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteWorkflowAsync_AgentFanIn_RecordsEveryObservedTurnWithoutAnInventedSelfLoop(bool barrier)
    {
        var start = new MAFWorkflows.ChatForwardingExecutor("Start");
        var left = new ChatClientAgent(new BridgeScriptedChatClient("Left", []), name: "Left").BindAsExecutor(emitEvents: true);
        var right = new ChatClientAgent(new BridgeScriptedChatClient("Right", []), name: "Right").BindAsExecutor(emitEvents: true);
        var joinClient = new BridgeScriptedChatClient("Join", [], TimeSpan.FromMilliseconds(20));
        var join = new ChatClientAgent(joinClient, name: "Join").BindAsExecutor(emitEvents: true);
        var builder = new MAFWorkflows.WorkflowBuilder(start).AddFanOutEdge(start, [left, right]);
        if (barrier) builder.AddFanInBarrierEdge([left, right], join);
        else builder.AddEdge(left, join).AddEdge(right, join);
        var workflow = builder.WithOutputFrom(join).WithOpenTelemetry().Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "agent fan-in", ["Start", "Right", "Left", "Join"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.IsSuccess);
        if (barrier) Assert.InRange(joinClient.Turns, 1, 2);
        else Assert.Equal(2, joinClient.Turns);
        Assert.Equal(new[] { "Start", "Right", "Left" }, result.Steps.Take(3).Select(step => step.ExecutorId));
        Assert.All(result.Steps.Skip(3), step => Assert.Equal("Join", step.ExecutorId));
        var joins = result.Steps.Where(step => step.ExecutorId == "Join").ToArray();
        // The barrier routes individual protocol messages; it can leave accumulation waiting for a token.
        // Compare recorded replies with independently observed model turns, including every real reply exactly once.
        var turns = joins.Where(step => step.Output.Length > 0).ToArray();
        Assert.Equal(Enumerable.Range(1, joinClient.Turns).Select(turn => BridgeScriptedChatClient.FinalReply("Join", turn)),
            turns.Select(step => step.Output));
        Assert.All(turns, step => Assert.True(step.Duration >= TimeSpan.FromMilliseconds(10)));
        if (joins.Length > 1) Assert.True(joins[0].StartOffset + joins[0].Duration <= joins[1].StartOffset);
        Assert.Equal(turns[^1].Output, result.FinalOutput);
        Assert.DoesNotContain(result.Graph!.TraversedEdges!, edge => edge.SourceExecutorId == edge.TargetExecutorId);
        if (barrier)
            Assert.All(joins, step => Assert.Equal(["Left", "Right"], result.Graph.TraversedEdges!
                .Where(edge => edge.TargetStepIndex == step.StepIndex).Select(edge => edge.SourceExecutorId).Order(StringComparer.Ordinal)));
        else
        {
            // Both lists can arrive before the first token, or one before each token. Attribute by input evidence.
            Assert.Equal(["Left", "Right"], result.Graph.TraversedEdges!
                .Where(edge => edge.TargetExecutorId == "Join").Select(edge => edge.SourceExecutorId).Order(StringComparer.Ordinal));
        }
    }
}
