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
        var indexedEdges = new List<EdgeExecution>();
        string? workflowOutput = null;
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
        var pendingToolRecords = new Dictionary<(string ExecutorId, string CallId), ToolCallRecord>();
        EdgeExecution? stepIncomingEdge = null; // Incoming edge for current step
        EdgeExecution? pendingIncomingEdge = null; // Edge waiting for next step
        var currentOutgoingEdges = new List<EdgeExecution>();
        string? currentBranchId = null;
        string? stepBranchId = null; // Branch ID captured when step started

        // The open step's timing as the event source measured it (MAFWorkflowEventBridge reports when MAF actually
        // ran the executor), taken from the step's latest output event that carries it. Without it the step is
        // timed by event arrival: from the edge or output event that opened it to the edge or output that closes it.
        // The bridge yields a step's output only once the step has finished, so a step its own output opens (the
        // first one) measured ~0 that way, starting at its own end.
        (TimeSpan StartOffset, TimeSpan Duration)? measuredStepTiming = null;
        string? timingNotMeasuredReason = null;

        (TimeSpan StartOffset, TimeSpan Duration) StepTiming() =>
            measuredStepTiming ?? (stepStartOffset, executorStopwatch.Elapsed);

        // Glass Box Part 2 (P2.B1): per-executor ledger of the token usage + finish reason MAF only
        // surfaces on a complete AgentResponse (ExecutorAgentResponseEvent). Keyed by executor id;
        // consumed (and removed) when that executor's step is flushed, so a loop workflow that revisits
        // an executor does NOT re-report an earlier iteration's usage on a later step.
        var ledger = new Dictionary<string, (TokenUsage? Usage, string? FinishReason)>(StringComparer.Ordinal);

        // Look up and REMOVE an executor's ledger entry (consume-on-flush). Returns (null, null) if absent.
        (TokenUsage? Usage, string? FinishReason) TakeLedger(string executorId)
        {
            if (ledger.TryGetValue(executorId, out var value))
            {
                ledger.Remove(executorId);
                return value;
            }

            return (null, null);
        }

        // The output-accumulation + executor-switch bookkeeping shared by ExecutorOutputEvent and its
        // ExecutorAgentResponseEvent subclass. Flushes the previous executor's step (with its ledger
        // values) when the executor changes, then begins tracking the new one.
        void HandleExecutorOutput(ExecutorOutputEvent e)
        {
            if ((e.StartsNewStep || currentExecutorId != e.ExecutorId) && currentExecutorId != null)
            {
                var (usage, finishReason) = TakeLedger(currentExecutorId);
                var (startOffset, duration) = StepTiming();
                steps.Add(CreateStep(
                    currentExecutorId,
                    currentOutput.ToString(),
                    startOffset,
                    duration,
                    stepIndex++,
                    currentToolCalls,
                    stepIncomingEdge,
                    currentOutgoingEdges,
                    stepBranchId,
                    usage,
                    finishReason,
                    timingNotMeasuredReason));

                currentToolCalls = [];
                currentOutgoingEdges = [];
            }

            if (e.StartsNewStep || currentExecutorId != e.ExecutorId)
            {
                currentExecutorId = e.ExecutorId;
                currentOutput.Clear();
                stepStartOffset = overallStopwatch.Elapsed;
                executorStopwatch.Restart();
                measuredStepTiming = null;
                timingNotMeasuredReason = null;
                stepBranchId = e.StartsNewStep ? e.ParallelBranchId : currentBranchId;
                stepIncomingEdge = pendingIncomingEdge;
                pendingIncomingEdge = null;

                if (!_executorIds.Contains(currentExecutorId))
                {
                    _executorIds.Add(currentExecutorId);
                }
            }

            currentOutput.Append(e.Output ?? string.Empty);
            if (e.TimingNotMeasuredReason is { } reason)
            {
                timingNotMeasuredReason = reason;
                measuredStepTiming = (e.StartOffset ?? TimeSpan.Zero, TimeSpan.Zero);
            }
            else if (e.StartOffset is { } measuredStart && e.Duration is { } measuredDuration)
            {
                timingNotMeasuredReason = null;
                measuredStepTiming = (measuredStart, measuredDuration);
            }
        }

        try
        {
            var workflowComplete = false;
            await foreach (var evt in _workflowExecutor(prompt, cancellationToken))
            {
                switch (evt)
                {
                    // Derived case MUST precede the ExecutorOutputEvent case (C# first-match): capture the
                    // per-executor token usage + finish reason MAF only surfaces on a complete AgentResponse,
                    // then run the identical output bookkeeping. The event always arrives while this executor's
                    // step is still open (before any flush), so the ledger is populated before it is consumed.
                    case ExecutorAgentResponseEvent responseEvent:
                        HandleExecutorOutput(responseEvent);
                        if (ledger.TryGetValue(responseEvent.ExecutorId, out var prior))
                        {
                            // Repeat event for the same open step (retry / streamed-then-completed): SUM the
                            // tokens, keep the FIRST non-null finish reason.
                            ledger[responseEvent.ExecutorId] = (
                                SumUsage(prior.Usage, responseEvent.Usage),
                                prior.FinishReason ?? responseEvent.FinishReason);
                        }
                        else
                        {
                            ledger[responseEvent.ExecutorId] = (responseEvent.Usage, responseEvent.FinishReason);
                        }
                        break;

                    case ExecutorOutputEvent outputEvent:
                        HandleExecutorOutput(outputEvent);
                        break;

                    case EdgeTraversedEvent edgeEvent:
                        var edgeExecution = new EdgeExecution
                        {
                            EdgeId = edgeEvent.EdgeId ?? $"{edgeEvent.SourceExecutorId}->{edgeEvent.TargetExecutorId}",
                            SourceExecutorId = edgeEvent.SourceExecutorId,
                            TargetExecutorId = edgeEvent.TargetExecutorId,
                            EdgeType = edgeEvent.EdgeType,
                            TraversedAt = edgeEvent.ObservedAt ?? overallStopwatch.Elapsed,
                            ConditionResult = edgeEvent.ConditionResult,
                            MatchedSwitchLabel = edgeEvent.MatchedSwitchLabel,
                            TransferredData = edgeEvent.TransferredData,
                            SourceStepIndex = edgeEvent.SourceStepIndex ?? stepIndex,
                            TargetStepIndex = edgeEvent.TargetStepIndex ?? stepIndex + 1,
                            RoutingReason = edgeEvent.RoutingReason
                        };
                        traversedEdges.Add(edgeExecution);
                        if (edgeEvent.SourceStepIndex.HasValue && edgeEvent.TargetStepIndex.HasValue)
                        {
                            // The bridge resolves activation indices independently of display order.
                            // Attach these edges after every step exists; an edge is not a step boundary.
                            indexedEdges.Add(edgeExecution);
                            break;
                        }
                        currentOutgoingEdges.Add(edgeExecution);

                        // Flush the source executor's step immediately.
                        // Tool calls from the TARGET executor arrive AFTER this event but
                        // BEFORE the target's first ExecutorOutputEvent, so we must advance
                        // currentExecutorId NOW — otherwise those calls end up attributed to
                        // the source executor's step (the original off-by-one attribution bug).
                        if (currentExecutorId != null)
                        {
                            var (edgeUsage, edgeFinishReason) = TakeLedger(currentExecutorId);
                            var (edgeStartOffset, edgeDuration) = StepTiming();
                            steps.Add(CreateStep(
                                currentExecutorId,
                                currentOutput.ToString(),
                                edgeStartOffset,
                                edgeDuration,
                                stepIndex++,
                                currentToolCalls,
                                stepIncomingEdge,
                                currentOutgoingEdges,
                                stepBranchId,
                                edgeUsage,
                                edgeFinishReason,
                                timingNotMeasuredReason));

                            currentToolCalls = [];
                            currentOutgoingEdges = [];
                            currentOutput.Clear();
                        }

                        // Transition to the target executor so subsequent tool calls are
                        // correctly attributed before its first ExecutorOutputEvent arrives.
                        currentExecutorId = edgeEvent.TargetExecutorId;
                        stepStartOffset = overallStopwatch.Elapsed;
                        executorStopwatch.Restart();
                        measuredStepTiming = null;
                        timingNotMeasuredReason = null;
                        stepBranchId = currentBranchId;
                        stepIncomingEdge = edgeExecution;
                        pendingIncomingEdge = null;

                        if (!_executorIds.Contains(currentExecutorId))
                            _executorIds.Add(currentExecutorId);
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
                        var toolExecutorId = toolEvent.ExecutorId ?? currentExecutorId ?? "unknown";
                        var toolCallId = toolEvent.CallId ?? Guid.NewGuid().ToString();
                        var toolKey = (toolExecutorId, toolCallId);
                        var observedAt = DateTimeOffset.UtcNow;
                        if (toolEvent.HasResult && pendingToolRecords.Remove(toolKey, out var pendingCall))
                        {
                            pendingCall.Result = toolEvent.Result;
                            pendingCall.WasExecuted = true;
                            pendingCall.StartTime = observedAt - toolEvent.Duration;
                            pendingCall.EndTime = observedAt;
                            break;
                        }

                        // An agent can fail after requesting a tool but before producing any text.
                        // Keep that attempted call and its executor visible even without a result.
                        if (currentExecutorId is null)
                            HandleExecutorOutput(new ExecutorOutputEvent(toolExecutorId, string.Empty));
                        var toolRecord = new ToolCallRecord
                        {
                            Name = toolEvent.ToolName,
                            CallId = toolCallId,
                            Arguments = toolEvent.Arguments,
                            Result = toolEvent.Result,
                            WasExecuted = toolEvent.HasResult,
                            StartTime = toolEvent.HasResult ? observedAt - toolEvent.Duration : observedAt,
                            EndTime = toolEvent.HasResult ? observedAt : null,
                            Order = currentToolCalls.Count + 1,
                            // Use the executor ID embedded in the event (set by the bridge from the
                            // MAF streaming update that contained the tool call), falling back to
                            // currentExecutorId only when the event carries no ID itself.
                            ExecutorId = toolExecutorId
                        };
                        currentToolCalls.Add(toolRecord);
                        if (!toolEvent.HasResult)
                            pendingToolRecords[toolKey] = toolRecord;
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

                    case WorkflowCompleteEvent completeEvent:
                        workflowOutput = completeEvent.FinalOutput;
                        // Workflow complete - save final step. This is the MOST COMMON terminal flush
                        // (the bridge always yields WorkflowCompleteEvent), so it MUST take the ledger too,
                        // or the final executor of every workflow loses its tokens/finish reason.
                        if (currentExecutorId != null)
                        {
                            var (completeUsage, completeFinishReason) = TakeLedger(currentExecutorId);
                            var (completeStartOffset, completeDuration) = StepTiming();
                            steps.Add(CreateStep(
                                currentExecutorId,
                                currentOutput.ToString(),
                                completeStartOffset,
                                completeDuration,
                                stepIndex,
                                currentToolCalls,
                                stepIncomingEdge,
                                currentOutgoingEdges,
                                stepBranchId,
                                completeUsage,
                                completeFinishReason,
                                timingNotMeasuredReason));
                        }
                        workflowComplete = true;
                        break;
                }

                if (workflowComplete)
                    break;
            }

            // If no WorkflowCompleteEvent was received, save final step
            if (currentExecutorId != null && !steps.Any(s => s.ExecutorId == currentExecutorId && s.StepIndex == stepIndex))
            {
                var (finalUsage, finalFinishReason) = TakeLedger(currentExecutorId);
                var (finalStartOffset, finalDuration) = StepTiming();
                steps.Add(CreateStep(
                    currentExecutorId,
                    currentOutput.ToString(),
                    finalStartOffset,
                    finalDuration,
                    stepIndex,
                    currentToolCalls,
                    stepIncomingEdge,
                    currentOutgoingEdges,
                    stepBranchId,
                    finalUsage,
                    finalFinishReason,
                    timingNotMeasuredReason));
            }

            overallStopwatch.Stop();
        }
        catch (OperationCanceledException)
        {
            // Cancellation (caller-requested OR per-test timeout) must propagate, not be recorded
            // as a normal workflow error and returned as a completed-with-errors result. The
            // caller/harness decides whether it was a timeout or an external cancel (BUG-20).
            overallStopwatch.Stop();
            throw;
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

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var outgoing = indexedEdges.Where(edge => edge.SourceStepIndex == step.StepIndex).ToArray();
            var incoming = indexedEdges.FirstOrDefault(edge => edge.TargetStepIndex == step.StepIndex);
            if (outgoing.Length > 0 || incoming is not null)
                steps[index] = step with
                {
                    IncomingEdge = step.IncomingEdge ?? incoming,
                    OutgoingEdges = (step.OutgoingEdges ?? []).Concat(outgoing).ToArray()
                };
        }

        return new WorkflowExecutionResult
        {
            FinalOutput = workflowOutput ?? steps.LastOrDefault()?.Output ?? string.Empty,
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
        string? branchId = null,
        TokenUsage? tokenUsage = null,
        string? finishReason = null,
        string? timingNotMeasuredReason = null)
    {
        return new ExecutorStep
        {
            ExecutorId = executorId,
            Output = output,
            StartOffset = startOffset,
            Duration = duration,
            TimingNotMeasuredReason = timingNotMeasuredReason,
            StepIndex = stepIndex,
            ToolCalls = toolCalls.Count > 0 ? toolCalls.ToList() : null,
            IncomingEdge = incomingEdge,
            OutgoingEdges = outgoingEdges?.Count > 0 ? outgoingEdges.ToList() : null,
            ParallelBranchId = branchId,
            TokenUsage = tokenUsage,
            FinishReason = finishReason
        };
    }

    /// <summary>
    /// Sums two per-executor token-usage snapshots (Glass Box Part 2, P2.B1). Null-tolerant: a null operand
    /// is treated as the identity. <see cref="TokenUsage"/> is init-only, so summing constructs a new instance.
    /// </summary>
    private static TokenUsage? SumUsage(TokenUsage? a, TokenUsage? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new TokenUsage
        {
            PromptTokens = a.PromptTokens + b.PromptTokens,
            CompletionTokens = a.CompletionTokens + b.CompletionTokens,
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

        // Prefer the most specific name, so Fetch cannot consume Fetch_Retry's ID.
        var namesBySpecificity = cleanNames.OrderByDescending(n => n.Length).ToList();
        var idMap = new Dictionary<string, string>();
        foreach (var fullId in allMafIds)
        {
            foreach (var cleanName in namesBySpecificity)
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
            (prompt, ct) => MAFWorkflowEventBridge.StreamAsAgentEvalEventsCore(workflow, prompt, ct, idMap, cleanNames, graph),
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
/// <remarks>
/// An event source that knows when the executor actually ran sets <see cref="StartOffset"/> and
/// <see cref="Duration"/> together (<see cref="MAFWorkflowEventBridge"/> does), and the adapter records them as the
/// step's timing. When they are absent the adapter times the step by event arrival, which only measures how far
/// apart the source yielded its events.
/// </remarks>
public record ExecutorOutputEvent(string ExecutorId, string? Output) : WorkflowEvent
{
    /// <summary>Starts a distinct activation, including another turn of the same executor.</summary>
    public bool StartsNewStep { get; init; }

    /// <summary>The observed parallel branch owning this activation, when <see cref="StartsNewStep"/> is true.</summary>
    public string? ParallelBranchId { get; init; }

    /// <summary>
    /// When the step this output belongs to started, relative to the start of the event stream, as the event source
    /// measured it; <c>null</c> when the source did not measure it.
    /// </summary>
    public TimeSpan? StartOffset { get; init; }

    /// <summary>
    /// How long the step had run when this output was complete, as the event source measured it; <c>null</c> when the
    /// source did not measure it. When a step has several output events, the latest one that carries timing wins.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Why this activation could not be timed. When set, this takes precedence over numeric timing on the event.
    /// A later event carrying complete measured timing clears the reason.
    /// </summary>
    public string? TimingNotMeasuredReason { get; init; }
}

/// <summary>
/// Event indicating a tool call within an executor. A call with <see cref="HasResult"/> false records intent;
/// a later event with the same executor and call ID updates it when a paired result arrives.
/// </summary>
public record ExecutorToolCallEvent(
    string ExecutorId, 
    string ToolName, 
    string? CallId = null,
    IDictionary<string, object?>? Arguments = null,
    object? Result = null,
    TimeSpan Duration = default) : WorkflowEvent
{
    /// <summary>
    /// Whether a paired result was observed, including a result whose value is null. Defaults to true for
    /// compatibility with event sources that emit only completed calls. False never certifies execution or timing.
    /// </summary>
    public bool HasResult { get; init; } = true;
}

/// <summary>
/// Event indicating an error in an executor.
/// </summary>
public record ExecutorErrorEvent(
    string? ExecutorId, 
    string? Message, 
    string? StackTrace = null,
    string? ExceptionType = null) : WorkflowEvent;

/// <summary>
/// Event indicating a complete (non-streaming) agent response from an executor.
/// Captures token usage and finish reason that streaming events cannot provide.
/// </summary>
/// <remarks>
/// This event is emitted when MAF fires an <c>AgentResponseEvent</c> (non-streaming
/// complete response). It extends <see cref="ExecutorOutputEvent"/> with additional
/// per-executor token usage and finish reason data.
/// </remarks>
public record ExecutorAgentResponseEvent(
    string ExecutorId,
    string? Output,
    TokenUsage? Usage = null,
    string? FinishReason = null) : ExecutorOutputEvent(ExecutorId, Output);

/// <summary>
/// Event indicating workflow completion.
/// </summary>
public record WorkflowCompleteEvent : WorkflowEvent
{
    /// <summary>
    /// Output of the last output-producing activation. Null preserves the legacy last-step fallback.
    /// A later empty activation does not erase an earlier explicit workflow output.
    /// </summary>
    public string? FinalOutput { get; init; }
}

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
    string? RoutingReason = null) : WorkflowEvent
{
    /// <summary>Explicit source activation index. Set together with <see cref="TargetStepIndex"/>.</summary>
    public int? SourceStepIndex { get; init; }

    /// <summary>Explicit target activation index; indexed edges do not open or close steps.</summary>
    public int? TargetStepIndex { get; init; }

    /// <summary>Observed transition time relative to run start, when measured by the event source.</summary>
    public TimeSpan? ObservedAt { get; init; }
}

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
