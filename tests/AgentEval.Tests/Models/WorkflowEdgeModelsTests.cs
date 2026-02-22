// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Tests.Models;

/// <summary>
/// Tests for the edge and graph workflow models.
/// </summary>
public class WorkflowEdgeModelsTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // EDGE TYPE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EdgeType_Should_HaveAllExpectedValues()
    {
        // Verify all edge types exist
        Assert.True(Enum.IsDefined(typeof(EdgeType), EdgeType.Sequential));
        Assert.True(Enum.IsDefined(typeof(EdgeType), EdgeType.Conditional));
        Assert.True(Enum.IsDefined(typeof(EdgeType), EdgeType.Switch));
        Assert.True(Enum.IsDefined(typeof(EdgeType), EdgeType.ParallelFanOut));
        Assert.True(Enum.IsDefined(typeof(EdgeType), EdgeType.ParallelFanIn));
        Assert.True(Enum.IsDefined(typeof(EdgeType), EdgeType.Loop));
        Assert.True(Enum.IsDefined(typeof(EdgeType), EdgeType.Error));
        Assert.True(Enum.IsDefined(typeof(EdgeType), EdgeType.Terminal));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORKFLOW EDGE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WorkflowEdge_Should_InitializeWithRequiredProperties()
    {
        var edge = new WorkflowEdge
        {
            EdgeId = "edge-1",
            SourceExecutorId = "agent-a",
            TargetExecutorId = "agent-b"
        };

        Assert.Equal("edge-1", edge.EdgeId);
        Assert.Equal("agent-a", edge.SourceExecutorId);
        Assert.Equal("agent-b", edge.TargetExecutorId);
        Assert.Equal(EdgeType.Sequential, edge.EdgeType); // Default
    }

    [Fact]
    public void WorkflowEdge_Should_SupportConditionalEdge()
    {
        var edge = new WorkflowEdge
        {
            EdgeId = "conditional-edge",
            SourceExecutorId = "router",
            TargetExecutorId = "handler-approved",
            EdgeType = EdgeType.Conditional,
            Condition = "output.Contains('approved')",
            Description = "Route to approved handler"
        };

        Assert.Equal(EdgeType.Conditional, edge.EdgeType);
        Assert.Equal("output.Contains('approved')", edge.Condition);
        Assert.Equal("Route to approved handler", edge.Description);
    }

    [Fact]
    public void WorkflowEdge_Should_SupportSwitchEdge()
    {
        var edge = new WorkflowEdge
        {
            EdgeId = "switch-case-1",
            SourceExecutorId = "classifier",
            TargetExecutorId = "technical-handler",
            EdgeType = EdgeType.Switch,
            SwitchLabel = "technical",
            Priority = 1
        };

        Assert.Equal(EdgeType.Switch, edge.EdgeType);
        Assert.Equal("technical", edge.SwitchLabel);
        Assert.Equal(1, edge.Priority);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EDGE EXECUTION TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EdgeExecution_Should_CaptureTraversalDetails()
    {
        var execution = new EdgeExecution
        {
            EdgeId = "edge-1",
            SourceExecutorId = "agent-a",
            TargetExecutorId = "agent-b",
            EdgeType = EdgeType.Sequential,
            TraversedAt = TimeSpan.FromMilliseconds(500),
            SourceStepIndex = 0,
            TargetStepIndex = 1,
            TransferredData = "processed output"
        };

        Assert.Equal("edge-1", execution.EdgeId);
        Assert.Equal(TimeSpan.FromMilliseconds(500), execution.TraversedAt);
        Assert.Equal(0, execution.SourceStepIndex);
        Assert.Equal(1, execution.TargetStepIndex);
        Assert.Equal("processed output", execution.TransferredData);
    }

    [Fact]
    public void EdgeExecution_Should_CaptureConditionalResult()
    {
        var execution = new EdgeExecution
        {
            EdgeId = "conditional-edge",
            SourceExecutorId = "router",
            TargetExecutorId = "approved-handler",
            EdgeType = EdgeType.Conditional,
            TraversedAt = TimeSpan.FromSeconds(1),
            ConditionResult = true,
            RoutingReason = "Output contained 'approved'"
        };

        Assert.Equal(EdgeType.Conditional, execution.EdgeType);
        Assert.True(execution.ConditionResult);
        Assert.Equal("Output contained 'approved'", execution.RoutingReason);
    }

    [Fact]
    public void EdgeExecution_Should_CaptureSwitchMatch()
    {
        var execution = new EdgeExecution
        {
            EdgeId = "switch-edge",
            SourceExecutorId = "classifier",
            TargetExecutorId = "billing-handler",
            EdgeType = EdgeType.Switch,
            TraversedAt = TimeSpan.FromSeconds(2),
            MatchedSwitchLabel = "billing"
        };

        Assert.Equal(EdgeType.Switch, execution.EdgeType);
        Assert.Equal("billing", execution.MatchedSwitchLabel);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PARALLEL BRANCH TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ParallelBranch_Should_InitializeWithRequiredProperties()
    {
        var branch = new ParallelBranch
        {
            BranchId = "branch-1",
            ExecutorIds = ["agent-a", "agent-b"],
            StartOffset = TimeSpan.FromSeconds(1),
            Duration = TimeSpan.FromSeconds(5),
            IsSuccess = true,
            Output = "Branch output"
        };

        Assert.Equal("branch-1", branch.BranchId);
        Assert.Equal(2, branch.ExecutorIds.Count);
        Assert.Contains("agent-a", branch.ExecutorIds);
        Assert.Contains("agent-b", branch.ExecutorIds);
        Assert.True(branch.IsSuccess);
        Assert.Equal("Branch output", branch.Output);
    }

    [Fact]
    public void ParallelBranch_Should_DefaultToSuccess()
    {
        var branch = new ParallelBranch
        {
            BranchId = "branch-1",
            ExecutorIds = ["agent-a"]
        };

        Assert.True(branch.IsSuccess);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORKFLOW NODE TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WorkflowNode_Should_InitializeWithRequiredProperties()
    {
        var node = new WorkflowNode
        {
            NodeId = "researcher",
            DisplayName = "Research Agent",
            ExecutorType = "ChatCompletionAgent",
            IsEntryPoint = true,
            IsExitNode = false,
            ModelId = "gpt-4o",
            Description = "Researches topics"
        };

        Assert.Equal("researcher", node.NodeId);
        Assert.Equal("Research Agent", node.DisplayName);
        Assert.Equal("ChatCompletionAgent", node.ExecutorType);
        Assert.True(node.IsEntryPoint);
        Assert.False(node.IsExitNode);
        Assert.Equal("gpt-4o", node.ModelId);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WORKFLOW GRAPH SNAPSHOT TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void WorkflowGraphSnapshot_Should_InitializeWithRequiredProperties()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [
                new WorkflowNode { NodeId = "a", IsEntryPoint = true },
                new WorkflowNode { NodeId = "b" },
                new WorkflowNode { NodeId = "c", IsExitNode = true }
            ],
            Edges = [
                new WorkflowEdge { EdgeId = "a->b", SourceExecutorId = "a", TargetExecutorId = "b" },
                new WorkflowEdge { EdgeId = "b->c", SourceExecutorId = "b", TargetExecutorId = "c" }
            ],
            EntryNodeId = "a",
            ExitNodeIds = ["c"]
        };

        Assert.Equal(3, graph.Nodes.Count);
        Assert.Equal(2, graph.Edges.Count);
        Assert.Equal("a", graph.EntryNodeId);
        Assert.Single(graph.ExitNodeIds);
    }

    [Fact]
    public void WorkflowGraphSnapshot_Should_DetectConditionalRouting()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [new WorkflowNode { NodeId = "a" }],
            Edges = [
                new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "a", TargetExecutorId = "b", EdgeType = EdgeType.Conditional }
            ],
            EntryNodeId = "a",
            ExitNodeIds = ["b"]
        };

        Assert.True(graph.HasConditionalRouting);
        Assert.False(graph.HasParallelExecution);
        Assert.False(graph.HasLoops);
    }

    [Fact]
    public void WorkflowGraphSnapshot_Should_DetectParallelExecution()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [new WorkflowNode { NodeId = "a" }],
            Edges = [
                new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "a", TargetExecutorId = "b", EdgeType = EdgeType.ParallelFanOut }
            ],
            EntryNodeId = "a",
            ExitNodeIds = ["b"]
        };

        Assert.True(graph.HasParallelExecution);
        Assert.False(graph.HasConditionalRouting);
    }

    [Fact]
    public void WorkflowGraphSnapshot_Should_DetectLoops()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [new WorkflowNode { NodeId = "a" }, new WorkflowNode { NodeId = "b" }],
            Edges = [
                new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "a", TargetExecutorId = "b", EdgeType = EdgeType.Sequential },
                new WorkflowEdge { EdgeId = "e2", SourceExecutorId = "b", TargetExecutorId = "a", EdgeType = EdgeType.Loop }
            ],
            EntryNodeId = "a",
            ExitNodeIds = ["b"]
        };

        Assert.True(graph.HasLoops);
    }

    [Fact]
    public void WorkflowGraphSnapshot_GetOutgoingEdges_Should_ReturnCorrectEdges()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [
                new WorkflowNode { NodeId = "router" },
                new WorkflowNode { NodeId = "handler-a" },
                new WorkflowNode { NodeId = "handler-b" }
            ],
            Edges = [
                new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "router", TargetExecutorId = "handler-a" },
                new WorkflowEdge { EdgeId = "e2", SourceExecutorId = "router", TargetExecutorId = "handler-b" },
                new WorkflowEdge { EdgeId = "e3", SourceExecutorId = "handler-a", TargetExecutorId = "end" }
            ],
            EntryNodeId = "router",
            ExitNodeIds = ["end"]
        };

        var outgoingFromRouter = graph.GetOutgoingEdges("router").ToList();
        Assert.Equal(2, outgoingFromRouter.Count);
    }

    [Fact]
    public void WorkflowGraphSnapshot_GetIncomingEdges_Should_ReturnCorrectEdges()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [
                new WorkflowNode { NodeId = "a" },
                new WorkflowNode { NodeId = "b" },
                new WorkflowNode { NodeId = "merger" }
            ],
            Edges = [
                new WorkflowEdge { EdgeId = "e1", SourceExecutorId = "a", TargetExecutorId = "merger" },
                new WorkflowEdge { EdgeId = "e2", SourceExecutorId = "b", TargetExecutorId = "merger" }
            ],
            EntryNodeId = "a",
            ExitNodeIds = ["merger"]
        };

        var incomingToMerger = graph.GetIncomingEdges("merger").ToList();
        Assert.Equal(2, incomingToMerger.Count);
    }

    [Fact]
    public void WorkflowGraphSnapshot_GetExecutionPath_Should_ReturnOrderedPath()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [
                new WorkflowNode { NodeId = "a" },
                new WorkflowNode { NodeId = "b" },
                new WorkflowNode { NodeId = "c" }
            ],
            Edges = [],
            TraversedEdges = [
                new EdgeExecution { EdgeId = "e1", SourceExecutorId = "a", TargetExecutorId = "b", TraversedAt = TimeSpan.FromSeconds(1) },
                new EdgeExecution { EdgeId = "e2", SourceExecutorId = "b", TargetExecutorId = "c", TraversedAt = TimeSpan.FromSeconds(2) }
            ],
            EntryNodeId = "a",
            ExitNodeIds = ["c"]
        };

        var path = graph.GetExecutionPath().ToList();
        Assert.Equal(["a", "b", "c"], path);
    }

    [Fact]
    public void WorkflowGraphSnapshot_GetExecutionPath_Should_ReturnEmptyWhenNoTraversedEdges()
    {
        var graph = new WorkflowGraphSnapshot
        {
            Nodes = [new WorkflowNode { NodeId = "a" }],
            Edges = [],
            TraversedEdges = null,
            EntryNodeId = "a",
            ExitNodeIds = ["a"]
        };

        var path = graph.GetExecutionPath().ToList();
        Assert.Empty(path);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ROUTING DECISION TESTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void RoutingDecision_Should_CaptureAllDecisionDetails()
    {
        var decision = new RoutingDecision
        {
            DeciderExecutorId = "classifier",
            PossibleEdgeIds = ["edge-technical", "edge-billing", "edge-general"],
            SelectedEdgeId = "edge-billing",
            EvaluatedValue = "billing inquiry",
            SelectionReason = "Input contained billing keywords",
            DecisionTime = TimeSpan.FromMilliseconds(150)
        };

        Assert.Equal("classifier", decision.DeciderExecutorId);
        Assert.Equal(3, decision.PossibleEdgeIds.Count);
        Assert.Equal("edge-billing", decision.SelectedEdgeId);
        Assert.Equal("billing inquiry", decision.EvaluatedValue);
        Assert.Equal("Input contained billing keywords", decision.SelectionReason);
    }
}
