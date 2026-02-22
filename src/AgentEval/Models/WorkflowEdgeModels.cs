// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// Type of edge connection between workflow nodes (executors).
/// </summary>
public enum EdgeType
{
    /// <summary>
    /// Sequential execution - output flows directly to next executor.
    /// </summary>
    Sequential,

    /// <summary>
    /// Conditional routing - path taken based on condition evaluation.
    /// </summary>
    Conditional,

    /// <summary>
    /// Switch/match routing - one of multiple paths based on value matching.
    /// </summary>
    Switch,

    /// <summary>
    /// Parallel fan-out - execution branches to multiple executors simultaneously.
    /// </summary>
    ParallelFanOut,

    /// <summary>
    /// Parallel fan-in - multiple branches converge back to single executor.
    /// </summary>
    ParallelFanIn,

    /// <summary>
    /// Loop/iteration edge - executor feeds back to earlier point.
    /// </summary>
    Loop,

    /// <summary>
    /// Error/fallback path - edge taken when source executor fails.
    /// </summary>
    Error,

    /// <summary>
    /// Termination edge - leads to workflow completion.
    /// </summary>
    Terminal
}

/// <summary>
/// Represents an edge definition in the workflow graph (static structure).
/// </summary>
public record WorkflowEdge
{
    /// <summary>
    /// Unique identifier for this edge.
    /// </summary>
    public required string EdgeId { get; init; }

    /// <summary>
    /// Source executor (node) ID.
    /// </summary>
    public required string SourceExecutorId { get; init; }

    /// <summary>
    /// Target executor (node) ID.
    /// </summary>
    public required string TargetExecutorId { get; init; }

    /// <summary>
    /// Type of edge connection.
    /// </summary>
    public EdgeType EdgeType { get; init; } = EdgeType.Sequential;

    /// <summary>
    /// Condition expression for conditional edges (e.g., "output.Contains('approved')").
    /// </summary>
    public string? Condition { get; init; }

    /// <summary>
    /// Label for switch edges (the case value this edge matches).
    /// </summary>
    public string? SwitchLabel { get; init; }

    /// <summary>
    /// Priority when multiple edges are possible (lower = higher priority).
    /// </summary>
    public int Priority { get; init; }

    /// <summary>
    /// Human-readable description of this edge.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Represents a traversed edge during workflow execution (runtime data).
/// </summary>
public record EdgeExecution
{
    /// <summary>
    /// Reference to the static edge definition.
    /// </summary>
    public required string EdgeId { get; init; }

    /// <summary>
    /// Source executor that produced output.
    /// </summary>
    public required string SourceExecutorId { get; init; }

    /// <summary>
    /// Target executor that received input.
    /// </summary>
    public required string TargetExecutorId { get; init; }

    /// <summary>
    /// Type of edge that was traversed.
    /// </summary>
    public EdgeType EdgeType { get; init; }

    /// <summary>
    /// When this edge was traversed (relative to workflow start).
    /// </summary>
    public TimeSpan TraversedAt { get; init; }

    /// <summary>
    /// For conditional edges: whether the condition evaluated to true.
    /// </summary>
    public bool? ConditionResult { get; init; }

    /// <summary>
    /// For switch edges: which case/label was matched.
    /// </summary>
    public string? MatchedSwitchLabel { get; init; }

    /// <summary>
    /// The data/output that flowed through this edge.
    /// </summary>
    public string? TransferredData { get; init; }

    /// <summary>
    /// Step index of source executor when edge was traversed.
    /// </summary>
    public int SourceStepIndex { get; init; }

    /// <summary>
    /// Step index of target executor after receiving input.
    /// </summary>
    public int TargetStepIndex { get; init; }

    /// <summary>
    /// Any routing decision context (why this edge was chosen).
    /// </summary>
    public string? RoutingReason { get; init; }
}

/// <summary>
/// Represents a parallel execution branch in the workflow.
/// </summary>
public record ParallelBranch
{
    /// <summary>
    /// Unique identifier for this branch.
    /// </summary>
    public required string BranchId { get; init; }

    /// <summary>
    /// Executor IDs that are part of this parallel branch.
    /// </summary>
    public required IReadOnlyList<string> ExecutorIds { get; init; }

    /// <summary>
    /// When this branch started.
    /// </summary>
    public TimeSpan StartOffset { get; init; }

    /// <summary>
    /// Total duration of this branch.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Whether this branch completed successfully.
    /// </summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>
    /// The output produced by this branch.
    /// </summary>
    public string? Output { get; init; }
}

/// <summary>
/// Snapshot of the workflow graph structure and execution state.
/// </summary>
public record WorkflowGraphSnapshot
{
    /// <summary>
    /// All nodes (executors) in the workflow graph.
    /// </summary>
    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }

    /// <summary>
    /// All edges defined in the workflow graph.
    /// </summary>
    public required IReadOnlyList<WorkflowEdge> Edges { get; init; }

    /// <summary>
    /// Edges that were actually traversed during execution.
    /// </summary>
    public IReadOnlyList<EdgeExecution>? TraversedEdges { get; init; }

    /// <summary>
    /// Parallel branches executed during this workflow run.
    /// </summary>
    public IReadOnlyList<ParallelBranch>? ParallelBranches { get; init; }

    /// <summary>
    /// The entry point node ID.
    /// </summary>
    public required string EntryNodeId { get; init; }

    /// <summary>
    /// The exit/terminal node ID(s).
    /// </summary>
    public required IReadOnlyList<string> ExitNodeIds { get; init; }

    /// <summary>
    /// Whether this workflow has any conditional or switch edges.
    /// </summary>
    public bool HasConditionalRouting => Edges.Any(e => 
        e.EdgeType == EdgeType.Conditional || e.EdgeType == EdgeType.Switch);

    /// <summary>
    /// Whether this workflow has parallel execution paths.
    /// </summary>
    public bool HasParallelExecution => Edges.Any(e => 
        e.EdgeType == EdgeType.ParallelFanOut || e.EdgeType == EdgeType.ParallelFanIn);

    /// <summary>
    /// Whether this workflow has loop/iteration edges.
    /// </summary>
    public bool HasLoops => Edges.Any(e => e.EdgeType == EdgeType.Loop);

    /// <summary>
    /// Gets all edges originating from a specific node.
    /// </summary>
    public IEnumerable<WorkflowEdge> GetOutgoingEdges(string executorId) =>
        Edges.Where(e => e.SourceExecutorId.Equals(executorId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets all edges leading to a specific node.
    /// </summary>
    public IEnumerable<WorkflowEdge> GetIncomingEdges(string executorId) =>
        Edges.Where(e => e.TargetExecutorId.Equals(executorId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets the actual path taken through the graph (from traversed edges).
    /// </summary>
    public IEnumerable<string> GetExecutionPath()
    {
        if (TraversedEdges == null || TraversedEdges.Count == 0)
            yield break;

        var orderedEdges = TraversedEdges.OrderBy(e => e.TraversedAt).ToList();
        
        yield return orderedEdges[0].SourceExecutorId;
        foreach (var edge in orderedEdges)
        {
            yield return edge.TargetExecutorId;
        }
    }
}

/// <summary>
/// Represents a node (executor) in the workflow graph.
/// </summary>
public record WorkflowNode
{
    /// <summary>
    /// Unique identifier for this node (executor ID).
    /// </summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// Human-readable name for this node.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Type of executor (e.g., "ChatCompletionAgent", "SemanticKernelAgent").
    /// </summary>
    public string? ExecutorType { get; init; }

    /// <summary>
    /// Whether this is the entry point of the workflow.
    /// </summary>
    public bool IsEntryPoint { get; init; }

    /// <summary>
    /// Whether this is an exit/terminal node.
    /// </summary>
    public bool IsExitNode { get; init; }

    /// <summary>
    /// Model ID used by this executor (if applicable).
    /// </summary>
    public string? ModelId { get; init; }

    /// <summary>
    /// Description of what this node does.
    /// </summary>
    public string? Description { get; init; }
}

/// <summary>
/// Routing decision information for conditional/switch edges.
/// </summary>
public record RoutingDecision
{
    /// <summary>
    /// The executor that made the routing decision.
    /// </summary>
    public required string DeciderExecutorId { get; init; }

    /// <summary>
    /// All possible edges that could have been taken.
    /// </summary>
    public required IReadOnlyList<string> PossibleEdgeIds { get; init; }

    /// <summary>
    /// The edge that was actually selected.
    /// </summary>
    public required string SelectedEdgeId { get; init; }

    /// <summary>
    /// The value/output that was evaluated for routing.
    /// </summary>
    public string? EvaluatedValue { get; init; }

    /// <summary>
    /// Reason the specific edge was chosen.
    /// </summary>
    public string? SelectionReason { get; init; }

    /// <summary>
    /// When the decision was made.
    /// </summary>
    public TimeSpan DecisionTime { get; init; }
}
