// SPDX-License-Identifier: MIT
// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using AgentEval.Models;

namespace AgentEval.Tracing;

/// <summary>
/// Represents a complete recorded trace of workflow execution.
/// Extends AgentTrace with workflow-specific data (steps, routing, graph structure).
/// </summary>
public sealed class WorkflowTrace
{
    /// <summary>
    /// Schema version for forward compatibility.
    /// </summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Human-readable name for this trace.
    /// </summary>
    [JsonPropertyName("traceName")]
    public string TraceName { get; set; } = string.Empty;

    /// <summary>
    /// When the trace was captured.
    /// </summary>
    [JsonPropertyName("capturedAt")]
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The workflow type/pattern (e.g., "PromptChaining", "Routing", "Parallel").
    /// </summary>
    [JsonPropertyName("workflowType")]
    public string? WorkflowType { get; set; }

    /// <summary>
    /// The original prompt that started the workflow.
    /// </summary>
    [JsonPropertyName("originalPrompt")]
    public string? OriginalPrompt { get; set; }

    /// <summary>
    /// The final aggregated output from the workflow.
    /// </summary>
    [JsonPropertyName("finalOutput")]
    public string? FinalOutput { get; set; }

    /// <summary>
    /// All executor steps recorded during execution.
    /// </summary>
    [JsonPropertyName("steps")]
    public List<WorkflowTraceStep> Steps { get; set; } = new();

    /// <summary>
    /// Routing decisions made during conditional/switch execution.
    /// </summary>
    [JsonPropertyName("routingDecisions")]
    public List<WorkflowTraceRoutingDecision>? RoutingDecisions { get; set; }

    /// <summary>
    /// Graph structure snapshot.
    /// </summary>
    [JsonPropertyName("graph")]
    public WorkflowTraceGraph? Graph { get; set; }

    /// <summary>
    /// Errors that occurred during execution.
    /// </summary>
    [JsonPropertyName("errors")]
    public List<WorkflowTraceError>? Errors { get; set; }

    /// <summary>
    /// Aggregate performance metrics.
    /// </summary>
    [JsonPropertyName("performance")]
    public WorkflowTracePerformance? Performance { get; set; }

    /// <summary>
    /// Optional metadata stored with the trace.
    /// </summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Executor IDs that participated in this workflow.
    /// </summary>
    [JsonPropertyName("executorIds")]
    public List<string> ExecutorIds { get; set; } = new();
}

/// <summary>
/// A recorded step in the workflow execution.
/// </summary>
public class WorkflowTraceStep
{
    /// <summary>
    /// Unique identifier of the executor/agent that ran this step.
    /// </summary>
    [JsonPropertyName("executorId")]
    public string ExecutorId { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable name of the executor.
    /// </summary>
    [JsonPropertyName("executorName")]
    public string? ExecutorName { get; set; }

    /// <summary>
    /// Order in which this step executed (0-based).
    /// </summary>
    [JsonPropertyName("stepIndex")]
    public int StepIndex { get; set; }

    /// <summary>
    /// The input this executor received.
    /// </summary>
    [JsonPropertyName("input")]
    public string? Input { get; set; }

    /// <summary>
    /// The output produced by this executor.
    /// </summary>
    [JsonPropertyName("output")]
    public string? Output { get; set; }

    /// <summary>
    /// When this step started (relative to workflow start).
    /// </summary>
    [JsonPropertyName("startOffsetMs")]
    public long StartOffsetMs { get; set; }

    /// <summary>
    /// How long this step took in milliseconds.
    /// </summary>
    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>
    /// Tool calls made by this executor.
    /// </summary>
    [JsonPropertyName("toolCalls")]
    public List<TraceToolCall>? ToolCalls { get; set; }

    /// <summary>
    /// Token usage for this step.
    /// </summary>
    [JsonPropertyName("tokenUsage")]
    public TraceTokenUsage? TokenUsage { get; set; }

    /// <summary>
    /// The edge that led to this step.
    /// </summary>
    [JsonPropertyName("incomingEdge")]
    public WorkflowTraceEdge? IncomingEdge { get; set; }

    /// <summary>
    /// Edges that originated from this step.
    /// </summary>
    [JsonPropertyName("outgoingEdges")]
    public List<WorkflowTraceEdge>? OutgoingEdges { get; set; }

    /// <summary>
    /// Parallel branch ID if this step is part of parallel execution.
    /// </summary>
    [JsonPropertyName("parallelBranchId")]
    public string? ParallelBranchId { get; set; }

    /// <summary>
    /// Error that occurred during this step, if any.
    /// </summary>
    [JsonPropertyName("error")]
    public TraceError? Error { get; set; }
}

/// <summary>
/// A routing decision made during workflow execution.
/// </summary>
public class WorkflowTraceRoutingDecision
{
    /// <summary>
    /// The executor/router that made the decision.
    /// </summary>
    [JsonPropertyName("routerId")]
    public string RouterId { get; set; } = string.Empty;

    /// <summary>
    /// The condition or criteria evaluated.
    /// </summary>
    [JsonPropertyName("condition")]
    public string? Condition { get; set; }

    /// <summary>
    /// The result of the evaluation.
    /// </summary>
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    /// <summary>
    /// The executor chosen by the routing decision.
    /// </summary>
    [JsonPropertyName("selectedExecutorId")]
    public string SelectedExecutorId { get; set; } = string.Empty;

    /// <summary>
    /// When this decision was made (relative to workflow start).
    /// </summary>
    [JsonPropertyName("timestampOffsetMs")]
    public long TimestampOffsetMs { get; set; }
}

/// <summary>
/// An edge execution in the workflow graph.
/// </summary>
public class WorkflowTraceEdge
{
    /// <summary>
    /// Source executor ID.
    /// </summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// Target executor ID.
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Type of edge (Default, Conditional, Parallel).
    /// </summary>
    [JsonPropertyName("edgeType")]
    public string EdgeType { get; set; } = "Default";

    /// <summary>
    /// Condition label for conditional edges.
    /// </summary>
    [JsonPropertyName("condition")]
    public string? Condition { get; set; }
}

/// <summary>
/// Graph structure snapshot for replay.
/// </summary>
public class WorkflowTraceGraph
{
    /// <summary>
    /// All nodes (executors) in the graph.
    /// </summary>
    [JsonPropertyName("nodes")]
    public List<string> Nodes { get; set; } = new();

    /// <summary>
    /// All edges in the graph.
    /// </summary>
    [JsonPropertyName("edges")]
    public List<WorkflowTraceEdge> Edges { get; set; } = new();

    /// <summary>
    /// Entry point executor ID.
    /// </summary>
    [JsonPropertyName("entryPoint")]
    public string? EntryPoint { get; set; }

    /// <summary>
    /// Exit point executor ID(s).
    /// </summary>
    [JsonPropertyName("exitPoints")]
    public List<string>? ExitPoints { get; set; }

    /// <summary>
    /// Whether this workflow has conditional routing.
    /// </summary>
    [JsonPropertyName("hasConditionalRouting")]
    public bool HasConditionalRouting { get; set; }

    /// <summary>
    /// Whether this workflow has parallel execution.
    /// </summary>
    [JsonPropertyName("hasParallelExecution")]
    public bool HasParallelExecution { get; set; }
}

/// <summary>
/// A workflow error for trace.
/// </summary>
public class WorkflowTraceError
{
    /// <summary>
    /// The executor that experienced the error.
    /// </summary>
    [JsonPropertyName("executorId")]
    public string ExecutorId { get; set; } = string.Empty;

    /// <summary>
    /// Error message.
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Exception type name.
    /// </summary>
    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; set; }

    /// <summary>
    /// Stack trace (optional).
    /// </summary>
    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }

    /// <summary>
    /// When the error occurred (relative to workflow start).
    /// </summary>
    [JsonPropertyName("occurredAtMs")]
    public long OccurredAtMs { get; set; }
}

/// <summary>
/// Aggregate performance metrics for workflow execution.
/// </summary>
public class WorkflowTracePerformance
{
    /// <summary>
    /// Total duration of the workflow in milliseconds.
    /// </summary>
    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; set; }

    /// <summary>
    /// Number of steps executed.
    /// </summary>
    [JsonPropertyName("stepCount")]
    public int StepCount { get; set; }

    /// <summary>
    /// Total tool calls across all steps.
    /// </summary>
    [JsonPropertyName("totalToolCalls")]
    public int TotalToolCalls { get; set; }

    /// <summary>
    /// Total prompt tokens across all steps.
    /// </summary>
    [JsonPropertyName("totalPromptTokens")]
    public int TotalPromptTokens { get; set; }

    /// <summary>
    /// Total completion tokens across all steps.
    /// </summary>
    [JsonPropertyName("totalCompletionTokens")]
    public int TotalCompletionTokens { get; set; }

    /// <summary>
    /// Total tokens used.
    /// </summary>
    [JsonPropertyName("totalTokens")]
    public int TotalTokens => TotalPromptTokens + TotalCompletionTokens;

    /// <summary>
    /// Per-executor duration breakdown.
    /// </summary>
    [JsonPropertyName("durationByExecutor")]
    public Dictionary<string, long>? DurationByExecutor { get; set; }
}
