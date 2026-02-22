// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Models.Serialization;

/// <summary>
/// Serializes workflow execution results to various formats for visualization and analysis.
/// Supports JSON export for integration with visualization tools, Mermaid diagrams, and more.
/// </summary>
public static class WorkflowSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Serializes the complete workflow execution result to JSON, including
    /// both the static graph structure and the dynamic execution trace.
    /// </summary>
    /// <param name="result">The workflow execution result to serialize.</param>
    /// <returns>JSON string representation suitable for visualization tools.</returns>
    public static string ToJson(WorkflowExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var export = new WorkflowExecutionExport
        {
            WorkflowId = result.OriginalPrompt?.GetHashCode().ToString("X8") ?? "unknown",
            ExecutedAt = DateTimeOffset.UtcNow,
            TotalDuration = result.TotalDuration,
            FinalOutput = result.FinalOutput,
            IsSuccess = result.IsSuccess,
            Graph = result.Graph != null ? ExportGraph(result.Graph) : null,
            ExecutionTrace = ExportExecutionTrace(result)
        };

        return JsonSerializer.Serialize(export, JsonOptions);
    }

    /// <summary>
    /// Serializes only the graph structure (nodes and edges) without execution trace.
    /// Useful for visualizing the workflow topology.
    /// </summary>
    /// <param name="result">The workflow execution result.</param>
    /// <returns>JSON string of the graph structure only.</returns>
    public static string ToGraphJson(WorkflowExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Graph == null)
        {
            // Infer graph from steps
            var inferredGraph = InferGraphFromSteps(result.Steps);
            return JsonSerializer.Serialize(ExportGraph(inferredGraph), JsonOptions);
        }

        return JsonSerializer.Serialize(ExportGraph(result.Graph), JsonOptions);
    }

    /// <summary>
    /// Generates a Mermaid flowchart diagram from the workflow execution.
    /// Highlights executed paths and shows edge types.
    /// </summary>
    /// <param name="result">The workflow execution result.</param>
    /// <returns>Mermaid diagram syntax string.</returns>
    public static string ToMermaid(WorkflowExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine("```mermaid");
        sb.AppendLine("graph TD");

        var graph = result.Graph ?? InferGraphFromSteps(result.Steps);
        var executedNodes = result.Steps.Select(s => s.ExecutorId).ToHashSet();
        var traversedEdges = result.Graph?.TraversedEdges?
            .Select(e => (e.SourceExecutorId, e.TargetExecutorId))
            .ToHashSet() ?? [];

        // Define nodes
        foreach (var node in graph.Nodes)
        {
            var shape = node.IsEntryPoint ? "([" : node.IsExitNode ? "[[" : "[";
            var shapeEnd = node.IsEntryPoint ? "])" : node.IsExitNode ? "]]" : "]";
            sb.AppendLine($"    {SanitizeId(node.NodeId)}{shape}{node.DisplayName ?? node.NodeId}{shapeEnd}");
        }

        sb.AppendLine();

        // Define edges
        foreach (var edge in graph.Edges)
        {
            var arrow = edge.EdgeType switch
            {
                EdgeType.Conditional => "-->|Conditional|",
                EdgeType.Switch => "-->|Switch|",
                EdgeType.ParallelFanOut => "-.->|Parallel|",
                EdgeType.ParallelFanIn => "-.->|Merge|",
                EdgeType.Loop => "-->|Loop|",
                EdgeType.Error => "-->|Error|",
                _ => "-->"
            };
            sb.AppendLine($"    {SanitizeId(edge.SourceExecutorId)} {arrow} {SanitizeId(edge.TargetExecutorId)}");
        }

        sb.AppendLine();

        // Style executed nodes
        if (executedNodes.Count > 0)
        {
            sb.AppendLine("    classDef executed fill:#90EE90,stroke:#228B22");
            sb.AppendLine($"    class {string.Join(",", executedNodes.Select(SanitizeId))} executed");
        }

        // Style nodes with errors
        var errorNodes = result.Errors?.Select(e => e.ExecutorId).Distinct().ToList() ?? [];
        if (errorNodes.Count > 0)
        {
            sb.AppendLine("    classDef error fill:#FF6B6B,stroke:#DC143C");
            sb.AppendLine($"    class {string.Join(",", errorNodes.Select(SanitizeId))} error");
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    /// <summary>
    /// Exports the workflow execution to a timeline format suitable for 
    /// Gantt chart visualization or time-travel debugging.
    /// </summary>
    /// <param name="result">The workflow execution result.</param>
    /// <returns>JSON string of the timeline data.</returns>
    public static string ToTimelineJson(WorkflowExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var timeline = new WorkflowTimelineExport
        {
            TotalDuration = result.TotalDuration,
            Events = []
        };

        // Add step events
        foreach (var step in result.Steps.OrderBy(s => s.StartOffset))
        {
            timeline.Events.Add(new TimelineEvent
            {
                Type = "step-start",
                ExecutorId = step.ExecutorId,
                Timestamp = step.StartOffset,
                Details = new Dictionary<string, object?>
                {
                    ["input"] = step.Input
                }
            });

            timeline.Events.Add(new TimelineEvent
            {
                Type = "step-end",
                ExecutorId = step.ExecutorId,
                Timestamp = step.StartOffset + step.Duration,
                Details = new Dictionary<string, object?>
                {
                    ["output"] = step.Output,
                    ["duration"] = step.Duration.TotalMilliseconds
                }
            });
        }

        // Add edge traversal events
        if (result.Graph?.TraversedEdges != null)
        {
            foreach (var edge in result.Graph.TraversedEdges.OrderBy(e => e.TraversedAt))
            {
                timeline.Events.Add(new TimelineEvent
                {
                    Type = "edge-traversed",
                    ExecutorId = edge.SourceExecutorId,
                    Timestamp = edge.TraversedAt,
                    Details = new Dictionary<string, object?>
                    {
                        ["target"] = edge.TargetExecutorId,
                        ["edgeType"] = edge.EdgeType.ToString(),
                        ["conditionResult"] = edge.ConditionResult,
                        ["routingReason"] = edge.RoutingReason
                    }
                });
            }
        }

        // Sort all events by timestamp and return
        return JsonSerializer.Serialize(new WorkflowTimelineExport
        {
            TotalDuration = result.TotalDuration,
            Events = timeline.Events.OrderBy(e => e.Timestamp).ToList()
        }, JsonOptions);
    }

    /// <summary>
    /// Deserializes a workflow execution from JSON (for replay/comparison).
    /// </summary>
    /// <param name="json">JSON string previously exported via ToJson.</param>
    /// <returns>Deserialized export object.</returns>
    public static WorkflowExecutionExport? FromJson(string json)
    {
        return JsonSerializer.Deserialize<WorkflowExecutionExport>(json, JsonOptions);
    }

    #region Private Helpers

    private static GraphExport ExportGraph(WorkflowGraphSnapshot graph)
    {
        return new GraphExport
        {
            EntryNodeId = graph.EntryNodeId,
            ExitNodeIds = graph.ExitNodeIds,
            Nodes = graph.Nodes.Select(n => new NodeExport
            {
                NodeId = n.NodeId,
                DisplayName = n.DisplayName,
                NodeType = n.ExecutorType,
                IsEntryPoint = n.IsEntryPoint,
                IsExitNode = n.IsExitNode
            }).ToList(),
            Edges = graph.Edges.Select(e => new EdgeExport
            {
                EdgeId = e.EdgeId,
                Source = e.SourceExecutorId,
                Target = e.TargetExecutorId,
                Type = e.EdgeType,
                Condition = e.Condition,
                SwitchLabels = e.SwitchLabel != null ? [e.SwitchLabel] : null
            }).ToList()
        };
    }

    private static ExecutionTraceExport ExportExecutionTrace(WorkflowExecutionResult result)
    {
        return new ExecutionTraceExport
        {
            Steps = result.Steps.Select(s => new StepExport
            {
                StepIndex = s.StepIndex,
                ExecutorId = s.ExecutorId,
                ExecutorName = s.ExecutorName,
                Output = s.Output,
                Input = s.Input,
                StartOffset = s.StartOffset,
                Duration = s.Duration,
                ParallelBranchId = s.ParallelBranchId,
                IncomingEdge = s.IncomingEdge != null ? new EdgeExecutionExport
                {
                    Source = s.IncomingEdge.SourceExecutorId,
                    Target = s.IncomingEdge.TargetExecutorId,
                    Type = s.IncomingEdge.EdgeType,
                    TraversedAt = s.IncomingEdge.TraversedAt,
                    ConditionResult = s.IncomingEdge.ConditionResult,
                    RoutingReason = s.IncomingEdge.RoutingReason
                } : null,
                ToolCalls = s.ToolCalls?.Select(t => new ToolCallExport
                {
                    Name = t.Name,
                    Duration = t.Duration,
                    HasError = t.HasError
                }).ToList()
            }).ToList(),
            TraversedEdges = result.Graph?.TraversedEdges?.Select(e => new EdgeExecutionExport
            {
                Source = e.SourceExecutorId,
                Target = e.TargetExecutorId,
                Type = e.EdgeType,
                TraversedAt = e.TraversedAt,
                ConditionResult = e.ConditionResult,
                RoutingReason = e.RoutingReason,
                MatchedSwitchLabel = e.MatchedSwitchLabel
            }).ToList(),
            RoutingDecisions = result.RoutingDecisions?.Select(r => new RoutingDecisionExport
            {
                Decider = r.DeciderExecutorId,
                PossibleEdges = r.PossibleEdgeIds,
                SelectedEdge = r.SelectedEdgeId,
                EvaluatedValue = r.EvaluatedValue,
                Reason = r.SelectionReason
            }).ToList(),
            ParallelBranches = result.Graph?.ParallelBranches?.Select(b => new ParallelBranchExport
            {
                BranchId = b.BranchId,
                ExecutorIds = b.ExecutorIds,
                StartTime = b.StartOffset,
                EndTime = b.StartOffset + b.Duration,
                IsSuccess = b.IsSuccess
            }).ToList(),
            Errors = result.Errors?.Select(e => new ErrorExport
            {
                ExecutorId = e.ExecutorId,
                Message = e.Message,
                ExceptionType = e.ExceptionType,
                OccurredAt = e.OccurredAt
            }).ToList()
        };
    }

    private static WorkflowGraphSnapshot InferGraphFromSteps(IReadOnlyList<ExecutorStep> steps)
    {
        var nodes = steps.Select(s => new WorkflowNode
        {
            NodeId = s.ExecutorId,
            DisplayName = s.ExecutorName,
            IsEntryPoint = s.StepIndex == 0,
            IsExitNode = s.StepIndex == steps.Count - 1
        }).ToList();

        var edges = new List<WorkflowEdge>();
        for (int i = 0; i < steps.Count - 1; i++)
        {
            edges.Add(new WorkflowEdge
            {
                EdgeId = $"e{i + 1}",
                SourceExecutorId = steps[i].ExecutorId,
                TargetExecutorId = steps[i + 1].ExecutorId,
                EdgeType = steps[i + 1].WasConditionallyRouted ? EdgeType.Conditional : EdgeType.Sequential
            });
        }

        return new WorkflowGraphSnapshot
        {
            Nodes = nodes,
            Edges = edges,
            EntryNodeId = steps.FirstOrDefault()?.ExecutorId ?? string.Empty,
            ExitNodeIds = steps.Count > 0 ? [steps[^1].ExecutorId] : []
        };
    }

    private static string SanitizeId(string id)
    {
        // Mermaid IDs can't have certain characters
        return id.Replace("-", "_").Replace(" ", "_").Replace(".", "_");
    }

    #endregion
}

#region Export Models

/// <summary>
/// Complete workflow execution export for JSON serialization.
/// </summary>
public record WorkflowExecutionExport
{
    public string? WorkflowId { get; init; }
    public DateTimeOffset ExecutedAt { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public string? FinalOutput { get; init; }
    public bool IsSuccess { get; init; }
    public GraphExport? Graph { get; init; }
    public ExecutionTraceExport? ExecutionTrace { get; init; }
}

public record GraphExport
{
    public string? EntryNodeId { get; init; }
    public IReadOnlyList<string>? ExitNodeIds { get; init; }
    public List<NodeExport> Nodes { get; init; } = [];
    public List<EdgeExport> Edges { get; init; } = [];
}

public record NodeExport
{
    public required string NodeId { get; init; }
    public string? DisplayName { get; init; }
    public string? NodeType { get; init; }
    public bool IsEntryPoint { get; init; }
    public bool IsExitNode { get; init; }
}

public record EdgeExport
{
    public string? EdgeId { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public EdgeType Type { get; init; }
    public string? Condition { get; init; }
    public IReadOnlyList<string>? SwitchLabels { get; init; }
}

public record ExecutionTraceExport
{
    public List<StepExport> Steps { get; init; } = [];
    public List<EdgeExecutionExport>? TraversedEdges { get; init; }
    public List<RoutingDecisionExport>? RoutingDecisions { get; init; }
    public List<ParallelBranchExport>? ParallelBranches { get; init; }
    public List<ErrorExport>? Errors { get; init; }
}

public record StepExport
{
    public int StepIndex { get; init; }
    public required string ExecutorId { get; init; }
    public string? ExecutorName { get; init; }
    public string? Output { get; init; }
    public string? Input { get; init; }
    public TimeSpan StartOffset { get; init; }
    public TimeSpan Duration { get; init; }
    public string? ParallelBranchId { get; init; }
    public EdgeExecutionExport? IncomingEdge { get; init; }
    public List<ToolCallExport>? ToolCalls { get; init; }
}

public record EdgeExecutionExport
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public EdgeType Type { get; init; }
    public TimeSpan TraversedAt { get; init; }
    public bool? ConditionResult { get; init; }
    public string? RoutingReason { get; init; }
    public string? MatchedSwitchLabel { get; init; }
}

public record RoutingDecisionExport
{
    public required string Decider { get; init; }
    public IReadOnlyList<string>? PossibleEdges { get; init; }
    public string? SelectedEdge { get; init; }
    public string? EvaluatedValue { get; init; }
    public string? Reason { get; init; }
}

public record ParallelBranchExport
{
    public required string BranchId { get; init; }
    public IReadOnlyList<string>? ExecutorIds { get; init; }
    public TimeSpan StartTime { get; init; }
    public TimeSpan EndTime { get; init; }
    public bool IsSuccess { get; init; }
}

public record ErrorExport
{
    public required string ExecutorId { get; init; }
    public required string Message { get; init; }
    public string? ExceptionType { get; init; }
    public TimeSpan OccurredAt { get; init; }
}

public record ToolCallExport
{
    public required string Name { get; init; }
    public TimeSpan? Duration { get; init; }
    public bool HasError { get; init; }
}

public record WorkflowTimelineExport
{
    public TimeSpan TotalDuration { get; init; }
    public List<TimelineEvent> Events { get; init; } = [];
}

public record TimelineEvent
{
    public required string Type { get; init; }
    public required string ExecutorId { get; init; }
    public TimeSpan Timestamp { get; init; }
    public Dictionary<string, object?>? Details { get; init; }
}

#endregion
