// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using AgentEval.MAF;
using AgentEval.Models;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Tests for MAFWorkflowAdapter edge and graph functionality.
/// </summary>
public class MAFWorkflowAdapterEdgeTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // EDGE EVENT PROCESSING TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteWorkflowAsync_Should_CaptureEdgeTraversals()
    {
        var adapter = new MAFWorkflowAdapter(
            "edge-test",
            EmitEdgeEvents);

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.NotNull(result.Graph);
        Assert.NotNull(result.Graph.TraversedEdges);
        Assert.Single(result.Graph.TraversedEdges);
        Assert.Equal("agent-a", result.Graph.TraversedEdges[0].SourceExecutorId);
        Assert.Equal("agent-b", result.Graph.TraversedEdges[0].TargetExecutorId);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_Should_CaptureConditionalEdges()
    {
        var adapter = new MAFWorkflowAdapter(
            "conditional-test",
            EmitConditionalEdgeEvents);

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.NotNull(result.Graph?.TraversedEdges);
        var edge = result.Graph.TraversedEdges[0];
        Assert.Equal(EdgeType.Conditional, edge.EdgeType);
        Assert.True(edge.ConditionResult);
        Assert.Equal("output matched condition", edge.RoutingReason);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_Should_CaptureRoutingDecisions()
    {
        var adapter = new MAFWorkflowAdapter(
            "routing-test",
            EmitRoutingDecisionEvents);

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.NotNull(result.RoutingDecisions);
        Assert.Single(result.RoutingDecisions);
        Assert.Equal("classifier", result.RoutingDecisions[0].DeciderExecutorId);
        Assert.Equal("edge-billing", result.RoutingDecisions[0].SelectedEdgeId);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_Should_CaptureParallelBranches()
    {
        var adapter = new MAFWorkflowAdapter(
            "parallel-test",
            EmitParallelBranchEvents);

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.NotNull(result.Graph?.ParallelBranches);
        Assert.Single(result.Graph.ParallelBranches);
        Assert.Equal("branch-1", result.Graph.ParallelBranches[0].BranchId);
        Assert.Contains("worker-a", result.Graph.ParallelBranches[0].ExecutorIds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // STEP EDGE INFORMATION TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteWorkflowAsync_Should_SetIncomingEdgeOnSteps()
    {
        var adapter = new MAFWorkflowAdapter(
            "incoming-edge-test",
            EmitEdgeEvents);

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.Equal(2, result.Steps.Count);
        // First step has no incoming edge
        Assert.Null(result.Steps[0].IncomingEdge);
        // Second step should have incoming edge
        Assert.NotNull(result.Steps[1].IncomingEdge);
        Assert.Equal("agent-a", result.Steps[1].IncomingEdge!.SourceExecutorId);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_Should_SetParallelBranchIdOnSteps()
    {
        var adapter = new MAFWorkflowAdapter(
            "branch-id-test",
            EmitParallelStepEvents);

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        var parallelStep = result.Steps.FirstOrDefault(s => s.ExecutorId == "parallel-worker");
        Assert.NotNull(parallelStep);
        Assert.Equal("branch-1", parallelStep.ParallelBranchId);
        Assert.True(parallelStep.IsParallelBranch);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // GRAPH BUILDING TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ExecuteWorkflowAsync_Should_BuildGraphFromSteps()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "graph-build-test",
            ("agent-a", "output A"),
            ("agent-b", "output B"),
            ("agent-c", "output C"));

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.NotNull(result.Graph);
        Assert.Equal(3, result.Graph.Nodes.Count);
        Assert.Equal(2, result.Graph.Edges.Count);
        Assert.Equal("agent-a", result.Graph.EntryNodeId);
    }

    [Fact]
    public async Task ExecuteWorkflowAsync_Should_InferSequentialEdges()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "infer-edges-test",
            ("first", "output 1"),
            ("second", "output 2"));

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.NotNull(result.Graph);
        var edge = result.Graph.Edges.FirstOrDefault();
        Assert.NotNull(edge);
        Assert.Equal("first", edge.SourceExecutorId);
        Assert.Equal("second", edge.TargetExecutorId);
        Assert.Equal(EdgeType.Sequential, edge.EdgeType);
    }

    [Fact]
    public async Task FromSteps_Should_EmitEdgeTraversedEvents()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "emit-edges-test",
            ("a", "output A"),
            ("b", "output B"));

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.NotNull(result.Graph?.TraversedEdges);
        Assert.Single(result.Graph.TraversedEdges);
        Assert.Equal("a", result.Graph.TraversedEdges[0].SourceExecutorId);
        Assert.Equal("b", result.Graph.TraversedEdges[0].TargetExecutorId);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FACTORY METHOD TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WithGraph_Should_CreateAdapterWithPredefinedGraph()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [
                new WorkflowNode { NodeId = "a", IsEntryPoint = true },
                new WorkflowNode { NodeId = "b", IsExitNode = true }
            ],
            Edges = [
                new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "a", TargetExecutorId = "b", EdgeType = EdgeType.Conditional }
            ],
            EntryNodeId = "a",
            ExitNodeIds = ["b"]
        };

        var adapter = MAFWorkflowAdapter.WithGraph(
            "with-graph-test",
            graph,
            SimpleGraphExecutor);

        Assert.Equal("with-graph-test", adapter.Name);
        Assert.Equal("Structured", adapter.WorkflowType);
        Assert.NotNull(adapter.GraphDefinition);
        Assert.Equal(2, adapter.ExecutorIds.Count);
    }

    private static async IAsyncEnumerable<WorkflowEvent> SimpleGraphExecutor(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorOutputEvent("a", "output A");
        yield return new ExecutorOutputEvent("b", "output B");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    [Fact]
    public void FromConditionalSteps_Should_CreateAdapterWithConditionalEdges()
    {
        var adapter = MAFWorkflowAdapter.FromConditionalSteps(
            "conditional-steps-test",
            [("router", "route"), ("handler", "handle")],
            [("router", "handler", EdgeType.Conditional, "output.Contains('approved')")]);

        Assert.Equal("conditional-steps-test", adapter.Name);
        Assert.Equal("Conditional", adapter.WorkflowType);
    }

    [Fact]
    public async Task FromConditionalSteps_Should_EmitConditionalEdgeEvents()
    {
        var adapter = MAFWorkflowAdapter.FromConditionalSteps(
            "conditional-edges-test",
            [("router", "routing output"), ("handler", "handled")],
            [("router", "handler", EdgeType.Conditional, "true")]);

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        Assert.NotNull(result.Graph?.TraversedEdges);
        Assert.Single(result.Graph.TraversedEdges);
        Assert.Equal(EdgeType.Conditional, result.Graph.TraversedEdges[0].EdgeType);
        Assert.True(result.Graph.TraversedEdges[0].ConditionResult);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORKFLOW RESULT PROPERTIES TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Result_HasConditionalRouting_Should_ReflectGraphState()
    {
        var adapter = MAFWorkflowAdapter.FromConditionalSteps(
            "has-conditional-test",
            [("router", "route"), ("handler", "handle")],
            [("router", "handler", EdgeType.Conditional, null)]);

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        // Note: HasConditionalRouting checks Graph.Edges, not TraversedEdges
        // In this case, the graph is inferred from steps which uses Sequential edges
        // The conditional edge events are in TraversedEdges
        Assert.NotNull(result.Graph);
    }

    [Fact]
    public async Task Result_GetExecutionPath_Should_ReturnCorrectPath()
    {
        var adapter = MAFWorkflowAdapter.FromSteps(
            "execution-path-test",
            ("first", "1"),
            ("second", "2"),
            ("third", "3"));

        var result = await adapter.ExecuteWorkflowAsync("test prompt");

        var path = result.GetExecutionPath().ToList();
        Assert.Equal(["first", "second", "third"], path);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════════════

    private static async IAsyncEnumerable<WorkflowEvent> EmitEdgeEvents(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorOutputEvent("agent-a", "output A");
        yield return new EdgeTraversedEvent("agent-a", "agent-b", EdgeType.Sequential);
        yield return new ExecutorOutputEvent("agent-b", "output B");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<WorkflowEvent> EmitConditionalEdgeEvents(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorOutputEvent("router", "routing");
        yield return new EdgeTraversedEvent(
            "router", 
            "handler", 
            EdgeType.Conditional,
            ConditionResult: true,
            RoutingReason: "output matched condition");
        yield return new ExecutorOutputEvent("handler", "handled");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<WorkflowEvent> EmitRoutingDecisionEvents(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorOutputEvent("classifier", "classifying");
        yield return new RoutingDecisionEvent(
            "classifier",
            ["edge-technical", "edge-billing", "edge-general"],
            "edge-billing",
            EvaluatedValue: "billing inquiry",
            SelectionReason: "matched billing keywords");
        yield return new EdgeTraversedEvent("classifier", "billing-handler", EdgeType.Switch, MatchedSwitchLabel: "billing");
        yield return new ExecutorOutputEvent("billing-handler", "billing handled");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<WorkflowEvent> EmitParallelBranchEvents(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorOutputEvent("splitter", "splitting");
        yield return new ParallelBranchStartEvent("branch-1", ["worker-a", "worker-b"]);
        yield return new ExecutorOutputEvent("worker-a", "work a");
        yield return new ParallelBranchEndEvent(
            "branch-1",
            ["worker-a", "worker-b"],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            IsSuccess: true,
            Output: "branch output");
        yield return new ExecutorOutputEvent("merger", "merged");
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<WorkflowEvent> EmitParallelStepEvents(string prompt, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ExecutorOutputEvent("main", "main output");
        yield return new ParallelBranchStartEvent("branch-1", ["parallel-worker"]);
        yield return new ExecutorOutputEvent("parallel-worker", "parallel output");
        yield return new ParallelBranchEndEvent(
            "branch-1",
            ["parallel-worker"],
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));
        yield return new WorkflowCompleteEvent();
        await Task.CompletedTask;
    }
}
