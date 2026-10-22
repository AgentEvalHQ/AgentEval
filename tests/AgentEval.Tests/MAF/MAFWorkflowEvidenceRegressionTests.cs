// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Metrics.Agentic;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Models.Serialization;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.MAF;

public sealed partial class MAFWorkflowShapesTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteWorkflowAsync_MultipleBufferedActivations_PreservesEveryDeliveredSource(int cycles)
    {
        var rounds = 0;
        var leftCalls = 0;
        var rightCalls = 0;
        var received = new List<string>();
        var start = Function("Start", () => "seed-" + ++rounds);
        var left = ((Func<string, string>)(_ => "L" + ++leftCalls)).BindAsExecutor<string, string>("Left");
        var mid = ((Func<string, string>)(input => input)).BindAsExecutor<string, string>("Mid");
        var delay = ((Func<string, string>)(input => input)).BindAsExecutor<string, string>("Delay");
        var right = ((Func<string, string>)(_ => "R" + ++rightCalls)).BindAsExecutor<string, string>("Right");
        var join = ((Func<string, string>)(input => { received.Add(input); return input; }))
            .BindAsExecutor<string, string>("Join");
        var workflow = new MAFWorkflows.WorkflowBuilder(start).AddFanOutEdge(start, [left, mid])
            .AddEdge<string>(left, left, _ => leftCalls % 2 == 1)
            .AddEdge(mid, delay).AddEdge(delay, right).AddFanInBarrierEdge([left, right], join)
            .AddEdge<string>(join, start, input => input?.StartsWith("R", StringComparison.Ordinal) == true && rounds < cycles)
            .WithOutputFrom(join).Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "queued activations", ["Start", "Left", "Mid", "Delay", "Right", "Join"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Equal(cycles, rounds);
        Assert.Equal(cycles * 2, leftCalls);
        Assert.Equal(cycles, rightCalls);
        var joins = result.Steps.Where(step => step.ExecutorId == "Join").ToArray();
        Assert.Equal(cycles, joins.Length);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var expected = new[] { "L" + (cycle * 2 + 1), "L" + (cycle * 2 + 2), "R" + (cycle + 1) };
            Assert.Equal(expected.Order(), received.Skip(cycle * 3).Take(3).Order());
            var edges = result.Graph!.TraversedEdges!.Where(edge => edge.TargetStepIndex == joins[cycle].StepIndex).ToArray();
            Assert.Equal(expected.Order(), edges.Select(edge => result.Steps[edge.SourceStepIndex].Output).Order());
            Assert.All(edges, edge => Assert.Contains(edge, result.Steps[edge.SourceStepIndex].OutgoingEdges!));
        }
    }


    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteWorkflowAsync_UnequalDepthBarrier_PreservesBufferedSourceActivation(int cycles)
    {
        var rounds = 0;
        var received = new List<string>();
        var start = Function("Start", () => "seed-" + ++rounds);
        var left = ((Func<string, string>)(input => "left:" + input)).BindAsExecutor<string, string>("Left");
        var mid = ((Func<string, string>)(input => input)).BindAsExecutor<string, string>("Mid");
        var right = ((Func<string, string>)(input => "right:" + input)).BindAsExecutor<string, string>("Right");
        var join = ((Func<string, string>)(input => { received.Add(input); return input; }))
            .BindAsExecutor<string, string>("Join");
        var workflow = new MAFWorkflows.WorkflowBuilder(start).AddFanOutEdge(start, [left, mid])
            .AddEdge(mid, right).AddFanInBarrierEdge([left, right], join)
            .AddEdge<string>(join, start, input => input?.StartsWith("left:", StringComparison.Ordinal) == true && rounds < cycles)
            .WithOutputFrom(join).Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "buffered barrier", ["Start", "Left", "Mid", "Right", "Join"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Equal(cycles, rounds);
        Assert.Equal(cycles * 2, received.Count);
        var joins = result.Steps.Where(step => step.ExecutorId == "Join").ToArray();
        Assert.Equal(cycles, joins.Length);
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var edges = result.Graph!.TraversedEdges!.Where(edge => edge.TargetStepIndex == joins[cycle].StepIndex).ToArray();
            Assert.Equal(["Left", "Right"], edges.Select(edge => edge.SourceExecutorId).Order(StringComparer.Ordinal));
            Assert.All(edges, edge =>
            {
                Assert.Equal(EdgeType.ParallelFanIn, edge.EdgeType);
                var source = result.Steps[edge.SourceStepIndex];
                Assert.Equal(edge.SourceExecutorId, source.ExecutorId);
                Assert.EndsWith("seed-" + (cycle + 1), source.Output);
            });
        }
    }

    [Theory]
    [InlineData("function-chain", false)]
    [InlineData("function-loop", false)]
    [InlineData("function-chain", true)]
    [InlineData("function-loop", true)]
    public async Task ExecuteWorkflowAsync_RecordedTraversal_PreservesAssertionsAndActivationIndices(string shape, bool stepEdges)
    {
        var (workflow, ids, _) = CreateCoreShape(shape);
        await using var recorder = new WorkflowTraceRecorder(MAFWorkflowAdapter.FromMAFWorkflow(workflow, shape, ids), shape);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var live = await recorder.ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        var trace = JsonSerializer.Deserialize<WorkflowTrace>(JsonSerializer.Serialize(recorder.Trace))!;
        var replay = await new WorkflowTraceReplayingAgent(trace).ExecuteWorkflowAsync("input");
        if (stepEdges)
        {
            Assert.Equal(live.Steps.Select(step => step.IncomingEdge), replay.Steps.Select(step => step.IncomingEdge));
            Assert.Equal(live.Steps.SelectMany(step => step.OutgoingEdges ?? []), replay.Steps.SelectMany(step => step.OutgoingEdges ?? []));
        }
        else
        {
            Assert.Equal(live.Graph!.TraversedEdges, replay.Graph!.TraversedEdges);
            Assert.True(replay.Should().HaveUsedEdgeType(live.Graph.TraversedEdges![0].EdgeType).IsValid);
            foreach (var edge in live.Graph.TraversedEdges!)
                Assert.True(replay.Should().HaveTraversedEdge(edge.SourceExecutorId, edge.TargetExecutorId).IsValid);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExecuteWorkflowAsync_SelectedTargetWithRepeatedTurns_CountsTargetsAsBranches(int selected)
    {
        var source = ((Func<string, MAFWorkflows.IWorkflowContext, CancellationToken, ValueTask<string>>)Emit)
            .BindAsExecutor<string, string>("Source");
        var onlyClient = new BridgeScriptedChatClient("Only", []);
        var otherClient = new BridgeScriptedChatClient("Other", []);
        var only = new ChatClientAgent(onlyClient, name: "Only").BindAsExecutor(emitEvents: true);
        var other = new ChatClientAgent(otherClient, name: "Other").BindAsExecutor(emitEvents: true);
        var workflow = new MAFWorkflows.WorkflowBuilder(source)
            .AddFanOutEdge<object>(source, [only, other], (_, _) => Enumerable.Range(0, selected))
            .WithOutputFrom(only, other).Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await MAFWorkflowAdapter.FromMAFWorkflow(workflow, "repeated selected target", ["Source", "Only", "Other"])
            .ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors?.Select(error => error.Message) ?? []));
        Assert.Equal(2, onlyClient.Turns);
        Assert.Equal(selected == 2 ? 2 : 0, otherClient.Turns);
        Assert.Equal(selected == 2, result.HasParallelExecution);
        Assert.True(result.Should().HaveParallelBranchCount(selected == 2 ? 2 : 0).IsValid,
            string.Join("; ", result.Steps.Select(step => $"{step.StepIndex}:{step.ExecutorId}:{step.ParallelBranchId}")));
        foreach (var group in result.Steps.Where(step => step.ExecutorId != "Source").GroupBy(step => step.ExecutorId))
            Assert.Single(group.Select(step => step.ParallelBranchId).Distinct());

        [MAFWorkflows.SendsMessage(typeof(ChatMessage))]
        [MAFWorkflows.SendsMessage(typeof(MAFWorkflows.TurnToken))]
        static async ValueTask<string> Emit(string input, MAFWorkflows.IWorkflowContext context, CancellationToken ct)
        {
            for (var turn = 0; turn < 2; turn++)
            {
                await context.SendMessageAsync(new ChatMessage(ChatRole.User, input + turn), targetId: null, cancellationToken: ct);
                await context.SendMessageAsync(new MAFWorkflows.TurnToken(true), targetId: null, cancellationToken: ct);
            }
            return "sent";
        }
    }
}

public partial class MAFWorkflowEventBridgeTests
{
    [Fact]
    public async Task TranslateEvents_MultipleBufferedActivations_RetainsAllSourceIndices()
    {
        var ids = new[] { "Left", "Right", "Join" };
        var graph = new WorkflowGraphSnapshot
        {
            EntryNodeId = "Left", ExitNodeIds = ["Join"], Nodes = ids.Select(id => new WorkflowNode { NodeId = id }).ToArray(),
            Edges = ids.Take(2).Select(id => new WorkflowEdge
            { EdgeId = id + ">Join", SourceExecutorId = id, TargetExecutorId = "Join", EdgeType = EdgeType.ParallelFanIn }).ToArray()
        };
        var events = SuperStep(0, FunctionDelivery("Left", "seed", "L1"))
            .Concat(SuperStep(1, FunctionDelivery("Left", "L1", "L2")))
            .Concat(SuperStep(2, FunctionDelivery("Right", "seed", "R")))
            .Concat(SuperStep(3, FunctionDelivery("Join", "L1", "L1"), FunctionDelivery("Join", "L2", "L2"), FunctionDelivery("Join", "R", "R")));
        var adapter = new MAFWorkflowAdapter("queued activations", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(RecordingEvents(events), new HashSet<string>(),
                cancellationToken: ct, executorOrder: ids, graph: graph), ids, graphDefinition: graph);
        var result = await adapter.ExecuteWorkflowAsync("input");
        Assert.Equal(["Left", "Left", "Right", "Join"], result.Steps.Select(step => step.ExecutorId));
        Assert.Equal(new[] { (0, 3), (1, 3), (2, 3) }, result.Graph!.TraversedEdges!
            .Select(edge => (edge.SourceStepIndex, edge.TargetStepIndex)).Order());
    }


    [Fact]
    public void RestoreNestedAgentEvent_DuplicateAgentIdentity_RefusesArbitraryAttribution()
    {
        var left = new SharedIdentityAgent("Left").BindAsExecutor(emitEvents: true);
        var right = new SharedIdentityAgent("Right").BindAsExecutor(emitEvents: true);
        var inner = new MAFWorkflows.WorkflowBuilder(left).AddEdge(left, right).WithOutputFrom(right).Build();
        var host = new MAFWorkflows.SubworkflowBinding(inner, "Sub", null);
        var topology = MAFWorkflowTopology.Create(new MAFWorkflows.WorkflowBuilder(host).WithOutputFrom(host).Build());
        var update = new AgentResponseUpdate(ChatRole.Assistant, "reply") { AgentId = "shared-agent" };
        var error = Assert.Throws<NotSupportedException>(() =>
            topology.RestoreNestedAgentEvent(new MAFWorkflows.WorkflowOutputEvent(update, "Sub")));
        Assert.Contains("2 matches", error.Message);
    }

    private sealed class SharedIdentityAgent(string name) : FakeStreamableAIAgent(name, "reply")
    {
        protected override string? IdCore => "shared-agent";
    }

    [Theory]
    [InlineData("eval-projection", false)]
    [InlineData("eval-projection", true)]
    [InlineData("execution", true)]
    [InlineData("timing", false)]
    [InlineData("identity", true)]
    [InlineData("trace-outcome", false)]
    [InlineData("timeline-outcome", false)]
    [InlineData("timeline-timing", false)]
    public async Task ExecuteWorkflowAsync_ToolEvidenceRoundTrip_PreservesObservedFacts(string check, bool hasResult)
    {
        var agent = new RecordingToolAgent(hasResult).BindAsExecutor(emitEvents: true);
        var workflow = new MAFWorkflows.WorkflowBuilder(agent).WithOutputFrom(agent).Build();
        await using var recorder = new WorkflowTraceRecorder(MAFWorkflowAdapter.FromMAFWorkflow(workflow, "tool evidence", ["Caller"]), "tools");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var live = await recorder.ExecuteWorkflowAsync("input", cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        var liveCall = Assert.Single(Assert.Single(live.Steps).ToolCalls!);
        Assert.Equal(hasResult, liveCall.WasExecuted);
        var trace = JsonSerializer.Deserialize<WorkflowTrace>(JsonSerializer.Serialize(recorder.Trace))!;
        var replay = await new WorkflowTraceReplayingAgent(trace).ExecuteWorkflowAsync("input");
        if (check == "eval-projection")
        {
            var projected = new TestCase { Name = "tool", Input = "input" }
                .ToEvalInput(new TestResult { TestName = "tool", Timeline = live.Timeline });
            var call = Assert.Single(projected.ToolCalls!);
            if (hasResult) Assert.Null(call.Result);
            else Assert.StartsWith(TestRunEvalProjection.ToolNotExecutedResultPrefix, call.Result);
            return;
        }

        var replayCall = Assert.Single(Assert.Single(replay.Steps).ToolCalls!);
        switch (check)
        {
            case "execution": Assert.True(replayCall.WasExecuted); break;
            case "timing": Assert.False(replayCall.HasTiming); Assert.Null(replayCall.EndTime); break;
            case "identity": Assert.Equal(liveCall.CallId, replayCall.CallId); Assert.Equal(liveCall.ExecutorId, replayCall.ExecutorId); break;
            case "trace-outcome": Assert.False(Assert.Single(Assert.Single(trace.Steps).ToolCalls!).Succeeded); break;
            case "timeline-outcome": Assert.Equal(0, live.Timeline!.SuccessfulToolCalls); break;
            case "timeline-timing": Assert.Contains("not measured", live.Timeline!.ToAsciiDiagram(), StringComparison.OrdinalIgnoreCase); break;
        }
        Assert.Equal(liveCall.WasExecuted, Assert.Single(replay.ToolUsage!.Calls).WasExecuted);
        var metric = new ToolSuccessMetric();
        var liveScore = await metric.EvaluateAsync(new EvaluationContext { Input = "input", Output = live.FinalOutput, ToolUsage = live.ToolUsage });
        var replayScore = await metric.EvaluateAsync(new EvaluationContext { Input = "input", Output = replay.FinalOutput, ToolUsage = replay.ToolUsage });
        Assert.Equal(liveScore.Score, replayScore.Score);
        Assert.Equal(liveCall.HasTiming, replayCall.HasTiming);
        Assert.Equal(liveCall.HasError, replayCall.HasError);
        foreach (var result in new[] { live, replay })
        {
            using var scope = AgentEvalScope.Collecting();
            result.ToolUsage!.Should().HaveCalledTool("lookup").WithDurationUnder(TimeSpan.FromMinutes(1));
            scope.Dispose();
            Assert.Equal(hasResult ? AssertionOutcome.Passed : AssertionOutcome.Inconclusive, scope.Results[^1].Outcome);
            using var export = JsonDocument.Parse(WorkflowSerializer.ToJson(result));
            var tool = export.RootElement.GetProperty("executionTrace").GetProperty("steps")[0].GetProperty("toolCalls")[0];
            Assert.Equal(hasResult, tool.GetProperty("wasExecuted").GetBoolean());
            Assert.Equal(hasResult, tool.TryGetProperty("duration", out _));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TranslateEvents_SelectedTargetRepeatedTurns_DoesNotCountActivationsAsBranches(int selected)
    {
        var ids = new[] { "Source", "Only", "Other" };
        var graph = new WorkflowGraphSnapshot
        {
            EntryNodeId = "Source", ExitNodeIds = ["Only", "Other"], Nodes = ids.Select(id => new WorkflowNode { NodeId = id }).ToArray(),
            Edges = ids.Skip(1).Select(id => new WorkflowEdge
            { EdgeId = "Source->>" + id, SourceExecutorId = "Source", TargetExecutorId = id, EdgeType = EdgeType.ParallelFanOut }).ToArray()
        };
        var deliveries = Enumerable.Range(0, selected).SelectMany(index =>
            AgentDelivery(ids[index + 1], new MAFWorkflows.TurnToken(true), "first")
                .Concat(AgentDelivery(ids[index + 1], new MAFWorkflows.TurnToken(true), "second"))).ToArray();
        var events = SuperStep(0, AgentDelivery("Source", "input", "source"))
            .Concat(SuperStep(1, deliveries));
        var adapter = new MAFWorkflowAdapter("repeated targets", (_, ct) =>
            MAFWorkflowEventBridge.TranslateEventsAsync(RecordingEvents(events), new HashSet<string> { "Only", "Other" },
                cancellationToken: ct, executorOrder: ids, graph: graph), ids, graphDefinition: graph);
        var result = await adapter.ExecuteWorkflowAsync("input");
        Assert.Equal(1 + selected * 2, result.Steps.Count);
        Assert.Equal(selected == 2, result.HasParallelExecution);
        Assert.True(result.Should().HaveParallelBranchCount(selected == 2 ? 2 : 0).IsValid);
    }
}
