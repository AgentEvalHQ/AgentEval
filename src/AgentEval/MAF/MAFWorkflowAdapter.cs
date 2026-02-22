// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using AgentEval.Core;
using AgentEval.Models;
using MAFWorkflows = Microsoft.Agents.AI.Workflows;

namespace AgentEval.MAF;

/// <summary>
/// Adapts a Microsoft Agent Framework Workflow for testing.
/// Provides visibility into individual executor steps during workflow execution.
/// </summary>
/// <remarks>
/// <para>
/// This adapter wraps MAF Workflows and captures detailed execution information
/// including per-executor output, timing, tool calls, and graph structure with edges.
/// This enables assertions on workflow structure, routing decisions, and behavior.
/// </para>
/// <para>
/// For agents that are not MAF Workflows, use <see cref="MAFAgentAdapter"/> instead.
/// </para>
/// </remarks>
public class MAFWorkflowAdapter : IWorkflowEvaluableAgent
{
    private readonly Func<string, CancellationToken, IAsyncEnumerable<WorkflowEvent>> _workflowExecutor;
    private readonly List<string> _executorIds;
    private readonly WorkflowGraphSnapshot? _graphDefinition;

    /// <summary>
    /// Creates a new workflow adapter with a custom workflow executor function.
    /// </summary>
    /// <param name="name">Human-readable name for this workflow.</param>
    /// <param name="workflowExecutor">
    /// Function that executes the workflow and yields events.
    /// This enables testing with mock workflows or real MAF workflows.
    /// </param>
    /// <param name="executorIds">Optional list of expected executor IDs.</param>
    /// <param name="workflowType">Optional workflow pattern type (e.g., "PromptChaining").</param>
    /// <param name="graphDefinition">Optional pre-defined graph structure.</param>
    public MAFWorkflowAdapter(
        string name,
        Func<string, CancellationToken, IAsyncEnumerable<WorkflowEvent>> workflowExecutor,
        IEnumerable<string>? executorIds = null,
        string? workflowType = null,
        WorkflowGraphSnapshot? graphDefinition = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(workflowExecutor);

        Name = name;
        _workflowExecutor = workflowExecutor;
        _executorIds = executorIds?.ToList() ?? [];
        WorkflowType = workflowType;
        _graphDefinition = graphDefinition;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlyList<string> ExecutorIds => _executorIds;

    /// <inheritdoc />
    public string? WorkflowType { get; }

    /// <summary>
    /// Gets the pre-defined graph structure, if available.
    /// </summary>
    public WorkflowGraphSnapshot? GraphDefinition => _graphDefinition;

    /// <inheritdoc />
    public async Task<WorkflowExecutionResult> ExecuteWorkflowAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var steps = new List<ExecutorStep>();
        var errors = new List<WorkflowError>();
        var traversedEdges = new List<EdgeExecution>();
        var routingDecisions = new List<RoutingDecision>();
        var parallelBranches = new List<ParallelBranch>();
        var overallStopwatch = Stopwatch.StartNew();

        // Track current executor state
        string? currentExecutorId = null;
        var currentOutput = new StringBuilder();
        var executorStopwatch = new Stopwatch();
        var stepStartOffset = TimeSpan.Zero;
        var stepIndex = 0;
        var currentToolCalls = new List<ToolCallRecord>();
        EdgeExecution? stepIncomingEdge = null; // Incoming edge for current step
        EdgeExecution? pendingIncomingEdge = null; // Edge waiting for next step
        var currentOutgoingEdges = new List<EdgeExecution>();
        string? currentBranchId = null;
        string? stepBranchId = null; // Branch ID captured when step started

        try
        {
            await foreach (var evt in _workflowExecutor(prompt, cancellationToken))
            {
                switch (evt)
                {
                    case ExecutorOutputEvent outputEvent:
                        // When executor changes, save previous step
                        if (currentExecutorId != outputEvent.ExecutorId && currentExecutorId != null)
                        {
                            steps.Add(CreateStep(
                                currentExecutorId,
                                currentOutput.ToString(),
                                stepStartOffset,
                                executorStopwatch.Elapsed,
                                stepIndex++,
                                currentToolCalls,
                                stepIncomingEdge,
                                currentOutgoingEdges,
                                stepBranchId));
                            
                            currentToolCalls = [];
                            currentOutgoingEdges = [];
                        }

                        if (currentExecutorId != outputEvent.ExecutorId)
                        {
                            // Start tracking new executor
                            currentExecutorId = outputEvent.ExecutorId;
                            currentOutput.Clear();
                            stepStartOffset = overallStopwatch.Elapsed;
                            executorStopwatch.Restart();
                            stepBranchId = currentBranchId; // Capture branch at step start
                            stepIncomingEdge = pendingIncomingEdge; // Use pending edge as incoming
                            pendingIncomingEdge = null; // Reset pending

                            // Track executor ID if not already known
                            if (!_executorIds.Contains(currentExecutorId))
                            {
                                _executorIds.Add(currentExecutorId);
                            }
                        }

                        currentOutput.Append(outputEvent.Output ?? string.Empty);
                        break;

                    case EdgeTraversedEvent edgeEvent:
                        var edgeExecution = new EdgeExecution
                        {
                            EdgeId = edgeEvent.EdgeId ?? $"{edgeEvent.SourceExecutorId}->{edgeEvent.TargetExecutorId}",
                            SourceExecutorId = edgeEvent.SourceExecutorId,
                            TargetExecutorId = edgeEvent.TargetExecutorId,
                            EdgeType = edgeEvent.EdgeType,
                            TraversedAt = overallStopwatch.Elapsed,
                            ConditionResult = edgeEvent.ConditionResult,
                            MatchedSwitchLabel = edgeEvent.MatchedSwitchLabel,
                            TransferredData = edgeEvent.TransferredData,
                            SourceStepIndex = stepIndex,
                            TargetStepIndex = stepIndex + 1,
                            RoutingReason = edgeEvent.RoutingReason
                        };
                        traversedEdges.Add(edgeExecution);
                        currentOutgoingEdges.Add(edgeExecution);
                        
                        // Set as pending incoming edge for next step
                        pendingIncomingEdge = edgeExecution;
                        break;

                    case RoutingDecisionEvent routingEvent:
                        routingDecisions.Add(new RoutingDecision
                        {
                            DeciderExecutorId = routingEvent.DeciderExecutorId,
                            PossibleEdgeIds = routingEvent.PossibleEdgeIds,
                            SelectedEdgeId = routingEvent.SelectedEdgeId,
                            EvaluatedValue = routingEvent.EvaluatedValue,
                            SelectionReason = routingEvent.SelectionReason,
                            DecisionTime = overallStopwatch.Elapsed
                        });
                        break;

                    case ParallelBranchStartEvent branchStartEvent:
                        currentBranchId = branchStartEvent.BranchId;
                        break;

                    case ParallelBranchEndEvent branchEndEvent:
                        parallelBranches.Add(new ParallelBranch
                        {
                            BranchId = branchEndEvent.BranchId,
                            ExecutorIds = branchEndEvent.ExecutorIds,
                            StartOffset = branchEndEvent.StartOffset,
                            Duration = branchEndEvent.Duration,
                            IsSuccess = branchEndEvent.IsSuccess,
                            Output = branchEndEvent.Output
                        });
                        currentBranchId = null;
                        break;

                    case ExecutorToolCallEvent toolEvent:
                        currentToolCalls.Add(new ToolCallRecord
                        {
                            Name = toolEvent.ToolName,
                            CallId = toolEvent.CallId ?? Guid.NewGuid().ToString(),
                            Arguments = toolEvent.Arguments,
                            Result = toolEvent.Result,
                            StartTime = DateTimeOffset.UtcNow - toolEvent.Duration,
                            EndTime = DateTimeOffset.UtcNow,
                            Order = currentToolCalls.Count + 1,
                            ExecutorId = currentExecutorId
                        });
                        break;

                    case ExecutorErrorEvent errorEvent:
                        errors.Add(new WorkflowError
                        {
                            ExecutorId = errorEvent.ExecutorId ?? "unknown",
                            Message = errorEvent.Message ?? "Unknown error",
                            StackTrace = errorEvent.StackTrace,
                            ExceptionType = errorEvent.ExceptionType,
                            OccurredAt = overallStopwatch.Elapsed
                        });
                        break;

                    case WorkflowCompleteEvent:
                        // Workflow complete - save final step
                        if (currentExecutorId != null)
                        {
                            steps.Add(CreateStep(
                                currentExecutorId,
                                currentOutput.ToString(),
                                stepStartOffset,
                                executorStopwatch.Elapsed,
                                stepIndex,
                                currentToolCalls,
                                stepIncomingEdge,
                                currentOutgoingEdges,
                                stepBranchId));
                        }
                        goto done; // Exit the foreach
                }
            }

            // If no WorkflowCompleteEvent was received, save final step
            if (currentExecutorId != null && !steps.Any(s => s.ExecutorId == currentExecutorId && s.StepIndex == stepIndex))
            {
                steps.Add(CreateStep(
                    currentExecutorId,
                    currentOutput.ToString(),
                    stepStartOffset,
                    executorStopwatch.Elapsed,
                    stepIndex,
                    currentToolCalls,
                    stepIncomingEdge,
                    currentOutgoingEdges,
                    stepBranchId));
            }

            done:
            overallStopwatch.Stop();
        }
        catch (Exception ex)
        {
            overallStopwatch.Stop();
            errors.Add(new WorkflowError
            {
                ExecutorId = currentExecutorId ?? "workflow",
                Message = ex.Message,
                StackTrace = ex.StackTrace,
                ExceptionType = ex.GetType().Name,
                OccurredAt = overallStopwatch.Elapsed
            });
        }

        return new WorkflowExecutionResult
        {
            FinalOutput = steps.LastOrDefault()?.Output ?? string.Empty,
            Steps = steps,
            TotalDuration = overallStopwatch.Elapsed,
            OriginalPrompt = prompt,
            Errors = errors.Count > 0 ? errors : null,
            RoutingDecisions = routingDecisions.Count > 0 ? routingDecisions : null,
            Graph = BuildGraph(steps, traversedEdges, parallelBranches)
        };
    }

    private WorkflowGraphSnapshot? BuildGraph(
        List<ExecutorStep> steps,
        List<EdgeExecution> traversedEdges,
        List<ParallelBranch> parallelBranches)
    {
        if (steps.Count == 0)
            return null;

        // Build nodes from executed steps
        var nodes = steps
            .Select(s => s.ExecutorId)
            .Distinct()
            .Select((id, idx) => new WorkflowNode
            {
                NodeId = id,
                DisplayName = id,
                IsEntryPoint = idx == 0,
                IsExitNode = id == steps.Last().ExecutorId
            })
            .ToList();

        // Use predefined edges if available, otherwise infer from execution
        var edges = _graphDefinition?.Edges.ToList() ?? InferEdges(steps);

        return new WorkflowGraphSnapshot
        {
            Nodes = nodes,
            Edges = edges,
            TraversedEdges = traversedEdges.Count > 0 ? traversedEdges : null,
            ParallelBranches = parallelBranches.Count > 0 ? parallelBranches : null,
            EntryNodeId = nodes.First().NodeId,
            ExitNodeIds = [nodes.Last().NodeId]
        };
    }

    private static List<WorkflowEdge> InferEdges(List<ExecutorStep> steps)
    {
        var edges = new List<WorkflowEdge>();
        
        for (int i = 0; i < steps.Count - 1; i++)
        {
            var source = steps[i];
            var target = steps[i + 1];
            
            edges.Add(new WorkflowEdge
            {
                EdgeId = $"{source.ExecutorId}->{target.ExecutorId}",
                SourceExecutorId = source.ExecutorId,
                TargetExecutorId = target.ExecutorId,
                EdgeType = EdgeType.Sequential
            });
        }

        return edges;
    }

    /// <inheritdoc />
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteWorkflowAsync(prompt, cancellationToken);
        return new AgentResponse
        {
            Text = result.FinalOutput,
            RawMessages = null
        };
    }

    private static ExecutorStep CreateStep(
        string executorId,
        string output,
        TimeSpan startOffset,
        TimeSpan duration,
        int stepIndex,
        List<ToolCallRecord> toolCalls,
        EdgeExecution? incomingEdge = null,
        List<EdgeExecution>? outgoingEdges = null,
        string? branchId = null)
    {
        return new ExecutorStep
        {
            ExecutorId = executorId,
            Output = output,
            StartOffset = startOffset,
            Duration = duration,
            StepIndex = stepIndex,
            ToolCalls = toolCalls.Count > 0 ? toolCalls.ToList() : null,
            IncomingEdge = incomingEdge,
            OutgoingEdges = outgoingEdges?.Count > 0 ? outgoingEdges.ToList() : null,
            ParallelBranchId = branchId
        };
    }

    /// <summary>
    /// Creates a workflow adapter from a simple step sequence (for testing).
    /// </summary>
    /// <param name="name">Workflow name.</param>
    /// <param name="steps">Sequence of (executorId, output) tuples.</param>
    /// <returns>A workflow adapter that executes the predefined steps.</returns>
    public static MAFWorkflowAdapter FromSteps(
        string name,
        params (string executorId, string output)[] steps)
    {
        return new MAFWorkflowAdapter(
            name,
            (prompt, ct) => ExecuteSteps(steps),
            steps.Select(s => s.executorId).Distinct(),
            "Predefined");
    }

    /// <summary>
    /// Creates a workflow adapter with explicit graph structure (for testing complex workflows).
    /// </summary>
    /// <param name="name">Workflow name.</param>
    /// <param name="graph">The workflow graph definition.</param>
    /// <param name="workflowExecutor">Function that executes the workflow.</param>
    /// <returns>A workflow adapter with the predefined graph structure.</returns>
    public static MAFWorkflowAdapter WithGraph(
        string name,
        WorkflowGraphSnapshot graph,
        Func<string, CancellationToken, IAsyncEnumerable<WorkflowEvent>> workflowExecutor)
    {
        return new MAFWorkflowAdapter(
            name,
            workflowExecutor,
            graph.Nodes.Select(n => n.NodeId),
            "Structured",
            graph);
    }

    /// <summary>
    /// Creates a workflow adapter from steps with conditional routing (for testing).
    /// </summary>
    public static MAFWorkflowAdapter FromConditionalSteps(
        string name,
        (string executorId, string output)[] steps,
        (string sourceId, string targetId, EdgeType edgeType, string? condition)[] edges)
    {
        return new MAFWorkflowAdapter(
            name,
            (prompt, ct) => ExecuteStepsWithEdges(steps, edges),
            steps.Select(s => s.executorId).Distinct(),
            "Conditional");
    }

    /// <summary>
    /// Creates a workflow adapter from a real MAF Workflow built with
    /// <see cref="MAFWorkflows.WorkflowBuilder"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses <see cref="MAFWorkflows.InProcessExecution"/> to stream events and
    /// <see cref="MAFWorkflowEventBridge"/> to translate MAF's class-based events
    /// to AgentEval's record-based events.
    /// </para>
    /// <para>
    /// The <paramref name="executorIds"/> must be provided explicitly because
    /// <c>Workflow.ExecutorBindings</c> is internal to the MAF assembly.
    /// </para>
    /// </remarks>
    /// <param name="workflow">The built MAF Workflow.</param>
    /// <param name="name">Human-readable name for this workflow.</param>
    /// <param name="executorIds">
    /// Ordered list of executor IDs in the workflow. Required because
    /// <c>Workflow.ExecutorBindings</c> is internal.
    /// </param>
    /// <param name="workflowType">Optional workflow pattern type (e.g., "PromptChaining").</param>
    /// <returns>A workflow adapter wrapping real MAF workflow execution.</returns>
    public static MAFWorkflowAdapter FromMAFWorkflow(
        MAFWorkflows.Workflow workflow,
        string name,
        IEnumerable<string> executorIds,
        string? workflowType = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(executorIds);

        var cleanNames = executorIds.ToList();

        // Discover real MAF executor IDs from ReflectEdges + StartExecutorId.
        // MAF generates IDs in the form "Name_<32hexGuid>" (via GetDescriptiveId).
        // We build a mapping from full MAF ID → user-provided clean name.
        var reflectedEdges = workflow.ReflectEdges();
        var allMafIds = new HashSet<string>(reflectedEdges.Keys);

        // Also add sink IDs from edges (targets may not appear as source keys)
        foreach (var edgeInfos in reflectedEdges.Values)
        {
            foreach (var edgeInfo in edgeInfos)
            {
                foreach (var sinkId in edgeInfo.Connection.SinkIds)
                    allMafIds.Add(sinkId);
                foreach (var sourceId in edgeInfo.Connection.SourceIds)
                    allMafIds.Add(sourceId);
            }
        }

        // Include the start executor ID
        if (!string.IsNullOrEmpty(workflow.StartExecutorId))
            allMafIds.Add(workflow.StartExecutorId);

        // Build fullId → cleanName mapping by matching prefix
        var idMap = new Dictionary<string, string>();
        foreach (var fullId in allMafIds)
        {
            foreach (var cleanName in cleanNames)
            {
                // MAF format: "{Name}_{Guid32hex}" — match by prefix
                if (string.Equals(fullId, cleanName, StringComparison.Ordinal)
                    || fullId.StartsWith(cleanName + "_", StringComparison.Ordinal))
                {
                    idMap[fullId] = cleanName;
                    break;
                }
            }

            // If no clean name matched, map to itself (passthrough)
            if (!idMap.ContainsKey(fullId))
                idMap[fullId] = fullId;
        }

        // Use real MAF IDs for graph extraction (they match ReflectEdges keys),
        // but pass the mapping so node/edge IDs are translated to clean names.
        var realMafIds = allMafIds.ToList();
        var graph = MAFGraphExtractor.ExtractGraph(workflow, realMafIds, idMap);

        return new MAFWorkflowAdapter(
            name,
            (prompt, ct) => MAFWorkflowEventBridge.StreamAsAgentEvalEvents(workflow, prompt, ct, idMap),
            cleanNames,
            workflowType,
            graph);
    }

    private static async IAsyncEnumerable<WorkflowEvent> ExecuteSteps(
        (string executorId, string output)[] steps)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            var (executorId, output) = steps[i];
            
            // Emit edge event for transitions (after first step)
            if (i > 0)
            {
                yield return new EdgeTraversedEvent(
                    steps[i - 1].executorId,
                    executorId,
                    EdgeType.Sequential);
            }
            
            yield return new ExecutorOutputEvent(executorId, output);
            await Task.Yield();
        }
        yield return new WorkflowCompleteEvent();
    }

    private static async IAsyncEnumerable<WorkflowEvent> ExecuteStepsWithEdges(
        (string executorId, string output)[] steps,
        (string sourceId, string targetId, EdgeType edgeType, string? condition)[] edges)
    {
        for (int i = 0; i < steps.Length; i++)
        {
            var (executorId, output) = steps[i];
            
            // Emit edge event for transitions (after first step)
            if (i > 0)
            {
                var edgeDef = edges.FirstOrDefault(e => 
                    e.sourceId == steps[i - 1].executorId && e.targetId == executorId);
                
                yield return new EdgeTraversedEvent(
                    steps[i - 1].executorId,
                    executorId,
                    edgeDef.edgeType,
                    ConditionResult: edgeDef.edgeType == EdgeType.Conditional ? true : null);
            }
            
            yield return new ExecutorOutputEvent(executorId, output);
            await Task.Yield();
        }
        yield return new WorkflowCompleteEvent();
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// WORKFLOW EVENTS
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Base class for workflow execution events.
/// </summary>
public abstract record WorkflowEvent;

/// <summary>
/// Event indicating output from an executor.
/// </summary>
public record ExecutorOutputEvent(string ExecutorId, string? Output) : WorkflowEvent;

/// <summary>
/// Event indicating a tool call within an executor.
/// </summary>
public record ExecutorToolCallEvent(
    string ExecutorId, 
    string ToolName, 
    string? CallId = null,
    IDictionary<string, object?>? Arguments = null,
    object? Result = null,
    TimeSpan Duration = default) : WorkflowEvent;

/// <summary>
/// Event indicating an error in an executor.
/// </summary>
public record ExecutorErrorEvent(
    string? ExecutorId, 
    string? Message, 
    string? StackTrace = null,
    string? ExceptionType = null) : WorkflowEvent;

/// <summary>
/// Event indicating workflow completion.
/// </summary>
public record WorkflowCompleteEvent : WorkflowEvent;

/// <summary>
/// Event indicating an edge was traversed between executors.
/// </summary>
public record EdgeTraversedEvent(
    string SourceExecutorId,
    string TargetExecutorId,
    EdgeType EdgeType = EdgeType.Sequential,
    string? EdgeId = null,
    bool? ConditionResult = null,
    string? MatchedSwitchLabel = null,
    string? TransferredData = null,
    string? RoutingReason = null) : WorkflowEvent;

/// <summary>
/// Event indicating a routing decision was made at a conditional/switch point.
/// </summary>
public record RoutingDecisionEvent(
    string DeciderExecutorId,
    IReadOnlyList<string> PossibleEdgeIds,
    string SelectedEdgeId,
    string? EvaluatedValue = null,
    string? SelectionReason = null) : WorkflowEvent;

/// <summary>
/// Event indicating a parallel branch has started.
/// </summary>
public record ParallelBranchStartEvent(
    string BranchId,
    IReadOnlyList<string> ExecutorIds) : WorkflowEvent;

/// <summary>
/// Event indicating a parallel branch has completed.
/// </summary>
public record ParallelBranchEndEvent(
    string BranchId,
    IReadOnlyList<string> ExecutorIds,
    TimeSpan StartOffset,
    TimeSpan Duration,
    bool IsSuccess = true,
    string? Output = null) : WorkflowEvent;
