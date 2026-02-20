// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF;
using AgentEval.Models;
using static Microsoft.Agents.AI.Workflows.ExecutorBindingExtensions;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Tests for <see cref="MAFGraphExtractor"/>.
/// Verifies correct translation of MAF Workflow graph structure to AgentEval's WorkflowGraphSnapshot.
/// </summary>
public class MAFGraphExtractorTests
{
    [Fact]
    public void ExtractGraph_ThrowsOnNullWorkflow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            MAFGraphExtractor.ExtractGraph(null!, ["a", "b"]));
    }

    [Fact]
    public void ExtractGraph_ThrowsOnNullExecutorIds()
    {
        // We need a real workflow to test, but we can't easily create one without
        // executor bindings. Use a minimal workflow for null check.
        // The null check on executorIds fires before workflow is used.
        var binding = CreateFuncBinding("start");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding).Build(validateOrphans: false);

        Assert.Throws<ArgumentNullException>(() =>
            MAFGraphExtractor.ExtractGraph(workflow, null!));
    }

    [Fact]
    public void ExtractGraph_SingleNode_ReturnsCorrectStructure()
    {
        // Arrange: single executor workflow
        var binding = CreateFuncBinding("solo");
        var workflow = new MAFWorkflows.WorkflowBuilder(binding)
            .WithName("SingleNode")
            .Build(validateOrphans: false);

        // Act
        var graph = MAFGraphExtractor.ExtractGraph(workflow, ["solo"]);

        // Assert
        Assert.NotNull(graph);
        Assert.Single(graph.Nodes);
        Assert.Equal("solo", graph.EntryNodeId);
        Assert.Contains("solo", graph.ExitNodeIds);

        var node = graph.Nodes[0];
        Assert.Equal("solo", node.NodeId);
        Assert.True(node.IsEntryPoint);
        Assert.True(node.IsExitNode);
    }

    [Fact]
    public void ExtractGraph_SequentialChain_BuildsCorrectEdges()
    {
        // Arrange: A → B → C sequential chain
        var a = CreateFuncBinding("A");
        var b = CreateFuncBinding("B");
        var c = CreateFuncBinding("C");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge(a, b)
            .AddEdge(b, c)
            .WithName("Sequential")
            .Build();

        // Act
        var graph = MAFGraphExtractor.ExtractGraph(workflow, ["A", "B", "C"]);

        // Assert
        Assert.NotNull(graph);
        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal("A", graph.EntryNodeId);

        // Entry node
        var nodeA = graph.Nodes.First(n => n.NodeId == "A");
        Assert.True(nodeA.IsEntryPoint);
        Assert.False(nodeA.IsExitNode);

        // Exit node (C has no outgoing edges)
        var nodeC = graph.Nodes.First(n => n.NodeId == "C");
        Assert.False(nodeC.IsEntryPoint);
        Assert.True(nodeC.IsExitNode);
        Assert.Contains("C", graph.ExitNodeIds);

        // Edges
        Assert.Equal(2, graph.Edges.Count);

        var edgeAB = graph.Edges.FirstOrDefault(e => e.SourceExecutorId == "A" && e.TargetExecutorId == "B");
        Assert.NotNull(edgeAB);
        Assert.Equal(EdgeType.Sequential, edgeAB.EdgeType);

        var edgeBC = graph.Edges.FirstOrDefault(e => e.SourceExecutorId == "B" && e.TargetExecutorId == "C");
        Assert.NotNull(edgeBC);
        Assert.Equal(EdgeType.Sequential, edgeBC.EdgeType);
    }

    [Fact]
    public void ExtractGraph_ConditionalEdge_DetectedCorrectly()
    {
        // Arrange: A → B (conditional) and A → C (unconditional)
        var a = CreateFuncBinding("A");
        var b = CreateFuncBinding("B");
        var c = CreateFuncBinding("C");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge<string>(a, b, condition: val => val != null && val.Contains("yes"))
            .AddEdge(a, c)
            .Build(validateOrphans: false);

        // Act
        var graph = MAFGraphExtractor.ExtractGraph(workflow, ["A", "B", "C"]);

        // Assert
        Assert.True(graph.Edges.Count >= 2);

        var conditionalEdge = graph.Edges.FirstOrDefault(e =>
            e.SourceExecutorId == "A" && e.TargetExecutorId == "B");
        Assert.NotNull(conditionalEdge);
        Assert.Equal(EdgeType.Conditional, conditionalEdge.EdgeType);

        var sequentialEdge = graph.Edges.FirstOrDefault(e =>
            e.SourceExecutorId == "A" && e.TargetExecutorId == "C");
        Assert.NotNull(sequentialEdge);
        Assert.Equal(EdgeType.Sequential, sequentialEdge.EdgeType);
    }

    [Fact]
    public void ExtractGraph_FanOutEdge_CreatesMultipleEdges()
    {
        // Arrange: A fans out to B and C
        var a = CreateFuncBinding("A");
        var b = CreateFuncBinding("B");
        var c = CreateFuncBinding("C");
        var d = CreateFuncBinding("D");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddFanOutEdge(a, [b, c])
            .AddFanInBarrierEdge([b, c], d)
            .Build();

        // Act
        var graph = MAFGraphExtractor.ExtractGraph(workflow, ["A", "B", "C", "D"]);

        // Assert
        var fanOutEdges = graph.Edges.Where(e =>
            e.SourceExecutorId == "A" && e.EdgeType == EdgeType.ParallelFanOut).ToList();
        Assert.Equal(2, fanOutEdges.Count);
        Assert.Contains(fanOutEdges, e => e.TargetExecutorId == "B");
        Assert.Contains(fanOutEdges, e => e.TargetExecutorId == "C");

        var fanInEdges = graph.Edges.Where(e =>
            e.TargetExecutorId == "D" && e.EdgeType == EdgeType.ParallelFanIn).ToList();
        Assert.Equal(2, fanInEdges.Count);
        Assert.Contains(fanInEdges, e => e.SourceExecutorId == "B");
        Assert.Contains(fanInEdges, e => e.SourceExecutorId == "C");
    }

    [Fact]
    public void ExtractGraph_AllNodesCyclic_FallsBackToLastAsExit()
    {
        // Arrange: A → B → A (cycle, all nodes have outgoing edges)
        var a = CreateFuncBinding("A");
        var b = CreateFuncBinding("B");

        var workflow = new MAFWorkflows.WorkflowBuilder(a)
            .AddEdge(a, b)
            .AddEdge(b, a)
            .Build();

        // Act
        var graph = MAFGraphExtractor.ExtractGraph(workflow, ["A", "B"]);

        // Assert: Should fall back to last executor as exit
        Assert.NotEmpty(graph.ExitNodeIds);
        Assert.Contains("B", graph.ExitNodeIds);
    }

    // ── Helper to create a simple function-based executor binding ──

    private static MAFWorkflows.ExecutorBinding CreateFuncBinding(string id)
    {
        return ((Func<string, ValueTask<string>>)(input => new ValueTask<string>(input)))
            .BindAsExecutor<string, string>(id);
    }
}
