// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Tracing;

/// <summary>
/// Replays a recorded WorkflowTrace as a mock IWorkflowEvaluableAgent.
/// Enables deterministic, fast, and cost-free test execution of multi-agent workflows.
/// </summary>
/// <example>
/// <code>
/// // Replay mode - uses pre-recorded workflow execution, no API calls
/// var replayer = await WorkflowTraceReplayingAgent.FromFileAsync("./traces/research_workflow.trace.json");
/// 
/// var result = await replayer.ExecuteWorkflowAsync("Research AI testing trends");
/// // Result is from the trace, not from actual agent execution
/// 
/// result.Steps.Should().HaveCount(3);
/// </code>
/// </example>
public sealed class WorkflowTraceReplayingAgent : IWorkflowEvaluableAgent
{
    private readonly WorkflowTrace _trace;
    private readonly WorkflowTraceReplayOptions _options;
    private int _executionCount;

    /// <summary>
    /// Creates a new replaying agent from a recorded workflow trace.
    /// </summary>
    /// <param name="trace">The recorded workflow trace to replay.</param>
    /// <param name="options">Optional replay options.</param>
    public WorkflowTraceReplayingAgent(WorkflowTrace trace, WorkflowTraceReplayOptions? options = null)
    {
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _options = options ?? new WorkflowTraceReplayOptions();
        _executionCount = 0;
    }

    /// <summary>
    /// Creates a new replaying agent from a trace file.
    /// </summary>
    /// <param name="filePath">Path to the .trace.json file.</param>
    /// <param name="options">Optional replay options.</param>
    public static async Task<WorkflowTraceReplayingAgent> FromFileAsync(
        string filePath,
        WorkflowTraceReplayOptions? options = null)
    {
        var trace = await WorkflowTraceSerializer.LoadFromFileAsync(filePath);
        return new WorkflowTraceReplayingAgent(trace, options);
    }

    /// <inheritdoc/>
    public string Name => _trace.WorkflowType ?? "ReplayingWorkflow";

    /// <inheritdoc/>
    public IReadOnlyList<string> ExecutorIds => _trace.ExecutorIds;

    /// <inheritdoc/>
    public string? WorkflowType => _trace.WorkflowType;

    /// <summary>
    /// Gets the trace being replayed.
    /// </summary>
    public WorkflowTrace Trace => _trace;

    /// <summary>
    /// Gets the number of times the workflow has been replayed.
    /// </summary>
    public int ExecutionCount => _executionCount;

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Validate prompt if enabled
        if (_options.ValidatePrompt && !string.IsNullOrEmpty(_trace.OriginalPrompt))
        {
            ValidatePrompt(prompt);
        }

        _executionCount++;

        return Task.FromResult(new AgentResponse
        {
            Text = _trace.FinalOutput ?? string.Empty
        });
    }

    /// <inheritdoc/>
    public async Task<WorkflowExecutionResult> ExecuteWorkflowAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Validate prompt if enabled
        if (_options.ValidatePrompt && !string.IsNullOrEmpty(_trace.OriginalPrompt))
        {
            ValidatePrompt(prompt);
        }

        // Simulate execution delay if requested
        if (_options.SimulateExecutionDelay && _trace.Performance != null)
        {
            await Task.Delay((int)(_trace.Performance.TotalDurationMs * _options.DelayMultiplier), cancellationToken);
        }

        _executionCount++;

        // Build the WorkflowExecutionResult from the trace
        return BuildWorkflowResult();
    }

    /// <summary>
    /// Resets the execution counter.
    /// </summary>
    public void Reset()
    {
        _executionCount = 0;
    }

    private WorkflowExecutionResult BuildWorkflowResult()
    {
        var steps = _trace.Steps.Select(s => new ExecutorStep
        {
            ExecutorId = s.ExecutorId,
            ExecutorName = s.ExecutorName,
            StepIndex = s.StepIndex,
            Input = s.Input,
            Output = s.Output ?? string.Empty,
            StartOffset = TimeSpan.FromMilliseconds(s.StartOffsetMs),
            Duration = TimeSpan.FromMilliseconds(s.DurationMs),
            ParallelBranchId = s.ParallelBranchId,
            ToolCalls = s.ToolCalls?.Select(tc => new ToolCallRecord
            {
                Name = tc.Name,
                CallId = $"replay-{tc.Name}-{s.StepIndex}",
                Arguments = !string.IsNullOrEmpty(tc.Arguments)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(tc.Arguments)
                    : null,
                Result = tc.Result,
                Exception = tc.Succeeded ? null : new Exception(tc.Error ?? "Unknown error"),
                Order = 0,
                StartTime = tc.StartedAt,
                EndTime = tc.StartedAt?.AddMilliseconds(tc.DurationMs ?? 0)
            }).ToList(),
            TokenUsage = s.TokenUsage != null
                ? new TokenUsage
                {
                    PromptTokens = s.TokenUsage.PromptTokens,
                    CompletionTokens = s.TokenUsage.CompletionTokens
                }
                : null,
            IncomingEdge = s.IncomingEdge != null
                ? new EdgeExecution
                {
                    EdgeId = $"{s.IncomingEdge.From}->{s.IncomingEdge.To}",
                    SourceExecutorId = s.IncomingEdge.From,
                    TargetExecutorId = s.IncomingEdge.To,
                    EdgeType = ParseEdgeType(s.IncomingEdge.EdgeType),
                    RoutingReason = s.IncomingEdge.Condition
                }
                : null,
            OutgoingEdges = s.OutgoingEdges?.Select(e => new EdgeExecution
            {
                EdgeId = $"{e.From}->{e.To}",
                SourceExecutorId = e.From,
                TargetExecutorId = e.To,
                EdgeType = ParseEdgeType(e.EdgeType),
                RoutingReason = e.Condition
            }).ToList()
        }).ToList();

        var routingDecisions = _trace.RoutingDecisions?.Select(rd => new RoutingDecision
        {
            DeciderExecutorId = rd.RouterId,
            PossibleEdgeIds = new List<string> { rd.SelectedExecutorId },
            SelectedEdgeId = rd.SelectedExecutorId,
            EvaluatedValue = rd.Condition,
            SelectionReason = rd.Result,
            DecisionTime = TimeSpan.FromMilliseconds(rd.TimestampOffsetMs)
        }).ToList();

        var graph = _trace.Graph != null
            ? new WorkflowGraphSnapshot
            {
                Nodes = _trace.Graph.Nodes.Select(nodeId => new WorkflowNode
                {
                    NodeId = nodeId,
                    IsEntryPoint = nodeId == _trace.Graph.EntryPoint,
                    IsExitNode = _trace.Graph.ExitPoints?.Contains(nodeId) ?? false
                }).ToList(),
                EntryNodeId = _trace.Graph.EntryPoint ?? _trace.Graph.Nodes.FirstOrDefault() ?? string.Empty,
                ExitNodeIds = _trace.Graph.ExitPoints ?? new List<string>(),
                Edges = _trace.Graph.Edges.Select(e => new WorkflowEdge
                {
                    EdgeId = $"{e.From}->{e.To}",
                    SourceExecutorId = e.From,
                    TargetExecutorId = e.To,
                    EdgeType = ParseEdgeType(e.EdgeType),
                    Condition = e.Condition
                }).ToList()
            }
            : null;

        var errors = _trace.Errors?.Select(e => new WorkflowError
        {
            ExecutorId = e.ExecutorId,
            Message = e.Message,
            ExceptionType = e.ExceptionType,
            StackTrace = e.StackTrace,
            OccurredAt = TimeSpan.FromMilliseconds(e.OccurredAtMs)
        }).ToList();

        return new WorkflowExecutionResult
        {
            FinalOutput = _trace.FinalOutput ?? string.Empty,
            Steps = steps,
            TotalDuration = TimeSpan.FromMilliseconds(_trace.Performance?.TotalDurationMs ?? 0),
            OriginalPrompt = _trace.OriginalPrompt,
            RoutingDecisions = routingDecisions,
            Graph = graph,
            Errors = errors
        };
    }

    private void ValidatePrompt(string actualPrompt)
    {
        var recordedPrompt = _trace.OriginalPrompt ?? string.Empty;

        if (_options.PromptMatchingMode == PromptMatchingMode.Exact)
        {
            if (actualPrompt != recordedPrompt)
            {
                HandleMismatch(actualPrompt, recordedPrompt);
            }
        }
        else if (_options.PromptMatchingMode == PromptMatchingMode.Contains)
        {
            if (!actualPrompt.Contains(recordedPrompt) && !recordedPrompt.Contains(actualPrompt))
            {
                HandleMismatch(actualPrompt, recordedPrompt);
            }
        }
        // PromptMatchingMode.Any - no validation
    }

    private void HandleMismatch(string actual, string recorded)
    {
        var message = $"Prompt mismatch.\n" +
                      $"Expected: {Truncate(recorded, 100)}\n" +
                      $"Actual: {Truncate(actual, 100)}";

        switch (_options.MismatchBehavior)
        {
            case MismatchBehavior.Throw:
                throw new WorkflowTraceReplayMismatchException(message, actual, recorded);

            case MismatchBehavior.Warn:
                Console.WriteLine($"[WorkflowTraceReplayer Warning] {message}");
                break;

            case MismatchBehavior.Ignore:
                // Continue silently
                break;
        }
    }

    private static EdgeType ParseEdgeType(string? edgeType)
    {
        if (string.IsNullOrEmpty(edgeType))
            return EdgeType.Sequential;

        return Enum.TryParse<EdgeType>(edgeType, ignoreCase: true, out var result)
            ? result
            : EdgeType.Sequential;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        return text.Substring(0, maxLength) + "...";
    }
}

/// <summary>
/// Options for workflow trace replay.
/// </summary>
public class WorkflowTraceReplayOptions
{
    /// <summary>
    /// Whether to validate that the incoming prompt matches the recorded prompt.
    /// Default is false.
    /// </summary>
    public bool ValidatePrompt { get; set; } = false;

    /// <summary>
    /// How to match prompts to the recorded prompt.
    /// Default is Any.
    /// </summary>
    public PromptMatchingMode PromptMatchingMode { get; set; } = PromptMatchingMode.Any;

    /// <summary>
    /// What to do when a prompt doesn't match the recorded prompt.
    /// Default is Throw.
    /// </summary>
    public MismatchBehavior MismatchBehavior { get; set; } = MismatchBehavior.Throw;

    /// <summary>
    /// Whether to simulate execution delays from recorded timings.
    /// Default is false (instant replay).
    /// </summary>
    public bool SimulateExecutionDelay { get; set; } = false;

    /// <summary>
    /// Multiplier for execution delay simulation.
    /// Use 0.1 to simulate 10x faster, 1.0 for real-time.
    /// Default is 0.1.
    /// </summary>
    public double DelayMultiplier { get; set; } = 0.1;
}

/// <summary>
/// How to match incoming prompts to recorded prompt.
/// </summary>
public enum PromptMatchingMode
{
    /// <summary>
    /// Any prompt matches (no validation).
    /// </summary>
    Any,

    /// <summary>
    /// Prompt must exactly match the recorded prompt.
    /// </summary>
    Exact,

    /// <summary>
    /// Prompt must contain or be contained by the recorded prompt.
    /// </summary>
    Contains
}

/// <summary>
/// Exception thrown when a prompt doesn't match the recorded prompt.
/// </summary>
public class WorkflowTraceReplayMismatchException : Exception
{
    public string ActualPrompt { get; }
    public string RecordedPrompt { get; }

    public WorkflowTraceReplayMismatchException(string message, string actualPrompt, string recordedPrompt)
        : base(message)
    {
        ActualPrompt = actualPrompt;
        RecordedPrompt = recordedPrompt;
    }
}
