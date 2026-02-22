// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// Result of executing a multi-agent workflow.
/// Provides visibility into individual executor steps for detailed testing and analysis.
/// </summary>
public record WorkflowExecutionResult
{
    /// <summary>
    /// The final aggregated output from the workflow.
    /// </summary>
    public required string FinalOutput { get; init; }

    /// <summary>
    /// Individual steps executed by each executor/agent in the workflow.
    /// </summary>
    public required IReadOnlyList<ExecutorStep> Steps { get; init; }

    /// <summary>
    /// Total time from workflow start to completion.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>
    /// The original prompt that started the workflow.
    /// </summary>
    public string? OriginalPrompt { get; init; }

    /// <summary>
    /// Any errors that occurred during workflow execution.
    /// </summary>
    public IReadOnlyList<WorkflowError>? Errors { get; init; }

    /// <summary>
    /// Graph structure and execution path information.
    /// </summary>
    public WorkflowGraphSnapshot? Graph { get; init; }

    /// <summary>
    /// Routing decisions made during conditional/switch execution.
    /// </summary>
    public IReadOnlyList<RoutingDecision>? RoutingDecisions { get; init; }

    /// <summary>
    /// Whether the workflow completed successfully (no errors).
    /// </summary>
    public bool IsSuccess => Errors == null || Errors.Count == 0;

    /// <summary>
    /// Aggregated tool usage across all workflow steps.
    /// Returns null if no tools were called.
    /// </summary>
    public ToolUsageReport? ToolUsage
    {
        get
        {
            var allCalls = Steps
                .Where(s => s.HasToolCalls)
                .SelectMany(s => s.ToolCalls!)
                .ToList();

            if (allCalls.Count == 0) return null;

            var report = new ToolUsageReport();
            int globalOrder = 1;
            foreach (var call in allCalls)
            {
                report.AddCall(new ToolCallRecord
                {
                    Name = call.Name,
                    CallId = call.CallId,
                    Arguments = call.Arguments,
                    Result = call.Result,
                    Exception = call.Exception,
                    Order = globalOrder++,
                    ExecutorId = call.ExecutorId,
                    StartTime = call.StartTime,
                    EndTime = call.EndTime
                });
            }
            return report;
        }
    }

    /// <summary>
    /// Unified tool call timeline across all workflow steps.
    /// Returns null if no tools were called.
    /// </summary>
    public ToolCallTimeline? Timeline
    {
        get
        {
            var hasAnyTools = Steps.Any(s => s.HasToolCalls);
            if (!hasAnyTools) return null;

            var timeline = ToolCallTimeline.Create();
            timeline.TotalDuration = TotalDuration;

            foreach (var step in Steps.Where(s => s.HasToolCalls))
            {
                foreach (var tc in step.ToolCalls!)
                {
                    timeline.AddInvocation(new ToolInvocation
                    {
                        ToolName = $"{step.ExecutorId}/{tc.Name}",
                        StartTime = step.StartOffset + (tc.Duration ?? TimeSpan.Zero),
                        Duration = tc.Duration ?? TimeSpan.Zero,
                        Succeeded = !tc.HasError,
                        ErrorMessage = tc.Exception?.Message,
                        Arguments = tc.GetArgumentsAsJson(),
                        Result = tc.Result?.ToString()
                    });
                }
            }
            return timeline;
        }
    }

    /// <summary>
    /// Whether this workflow used conditional routing.
    /// </summary>
    public bool HasConditionalRouting => Graph?.HasConditionalRouting ?? false;

    /// <summary>
    /// Whether this workflow had parallel execution.
    /// </summary>
    public bool HasParallelExecution => Graph?.HasParallelExecution ?? false;

    /// <summary>
    /// Gets the actual execution path through the graph.
    /// </summary>
    public IEnumerable<string> GetExecutionPath() => Graph?.GetExecutionPath() ?? Steps.Select(s => s.ExecutorId);

    /// <summary>
    /// Gets the step for a specific executor.
    /// </summary>
    public ExecutorStep? GetStep(string executorId) =>
        Steps.FirstOrDefault(s => s.ExecutorId.Equals(executorId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets all steps for a specific executor (if it ran multiple times).
    /// </summary>
    public IEnumerable<ExecutorStep> GetSteps(string executorId) =>
        Steps.Where(s => s.ExecutorId.Equals(executorId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A single step in a workflow execution, representing one executor's contribution.
/// </summary>
public record ExecutorStep
{
    /// <summary>
    /// Unique identifier of the executor/agent that ran this step.
    /// </summary>
    public required string ExecutorId { get; init; }

    /// <summary>
    /// Human-readable name of the executor (if different from ID).
    /// </summary>
    public string? ExecutorName { get; init; }

    /// <summary>
    /// The output produced by this executor.
    /// </summary>
    public required string Output { get; init; }

    /// <summary>
    /// When this step started (relative to workflow start).
    /// </summary>
    public TimeSpan StartOffset { get; init; }

    /// <summary>
    /// How long this step took.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Tool calls made by this executor during the step.
    /// </summary>
    public IReadOnlyList<ToolCallRecord>? ToolCalls { get; init; }

    /// <summary>
    /// Token usage for this step (if available).
    /// </summary>
    public Core.TokenUsage? TokenUsage { get; init; }

    /// <summary>
    /// The input this executor received (may be from previous executor).
    /// </summary>
    public string? Input { get; init; }

    /// <summary>
    /// Order in which this step executed (0-based).
    /// </summary>
    public int StepIndex { get; init; }

    /// <summary>
    /// The edge that led to this step (incoming edge).
    /// </summary>
    public EdgeExecution? IncomingEdge { get; init; }

    /// <summary>
    /// Edges that originated from this step (outgoing edges).
    /// For parallel fan-out, there can be multiple outgoing edges.
    /// </summary>
    public IReadOnlyList<EdgeExecution>? OutgoingEdges { get; init; }

    /// <summary>
    /// If this step is part of a parallel branch, the branch ID.
    /// </summary>
    public string? ParallelBranchId { get; init; }

    /// <summary>
    /// Whether this step made any tool calls.
    /// </summary>
    public bool HasToolCalls => ToolCalls?.Count > 0;

    /// <summary>
    /// Whether this step was reached via conditional routing.
    /// </summary>
    public bool WasConditionallyRouted => IncomingEdge?.EdgeType == EdgeType.Conditional;

    /// <summary>
    /// Whether this step is part of a parallel execution branch.
    /// </summary>
    public bool IsParallelBranch => !string.IsNullOrEmpty(ParallelBranchId);

    /// <summary>
    /// Gets the display name for this step.
    /// </summary>
    public string DisplayName => ExecutorName ?? ExecutorId;
}

/// <summary>
/// An error that occurred during workflow execution.
/// </summary>
public record WorkflowError
{
    /// <summary>
    /// The executor that experienced the error.
    /// </summary>
    public required string ExecutorId { get; init; }

    /// <summary>
    /// Error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Stack trace if available.
    /// </summary>
    public string? StackTrace { get; init; }

    /// <summary>
    /// When the error occurred (relative to workflow start).
    /// </summary>
    public TimeSpan OccurredAt { get; init; }

    /// <summary>
    /// The exception type name.
    /// </summary>
    public string? ExceptionType { get; init; }
}
