// SPDX-License-Identifier: MIT
// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Tracing;

/// <summary>
/// Wraps an IWorkflowEvaluableAgent to record all workflow executions for later replay.
/// This enables deterministic, fast, and cost-free test execution of multi-agent workflows.
/// </summary>
/// <example>
/// <code>
/// // Record a workflow execution
/// var workflow = new MafWorkflowAdapter(mafWorkflow);
/// await using var recorder = new WorkflowTraceRecorder(workflow, "research_workflow");
/// 
/// var result = await recorder.ExecuteWorkflowAsync("Research AI testing trends");
/// // ... test assertions ...
/// 
/// await recorder.SaveAsync("./traces/research_workflow.trace.json");
/// </code>
/// </example>
public sealed class WorkflowTraceRecorder : IWorkflowEvaluableAgent, IAsyncDisposable
{
    private readonly IWorkflowEvaluableAgent _inner;
    private readonly WorkflowTrace _trace;
    private readonly Stopwatch _sessionStopwatch;
    private readonly WorkflowTraceRecorderOptions _options;
    private bool _disposed;

    /// <summary>
    /// Creates a new recording wrapper around the given workflow agent.
    /// </summary>
    /// <param name="inner">The workflow agent to wrap and record.</param>
    /// <param name="traceName">Human-readable name for this trace.</param>
    /// <param name="options">Optional recording options.</param>
    public WorkflowTraceRecorder(
        IWorkflowEvaluableAgent inner,
        string traceName,
        WorkflowTraceRecorderOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _options = options ?? new WorkflowTraceRecorderOptions();

        _trace = new WorkflowTrace
        {
            TraceName = traceName,
            WorkflowType = inner.WorkflowType,
            CapturedAt = DateTimeOffset.UtcNow,
            ExecutorIds = inner.ExecutorIds.ToList(),
            Metadata = _options.Metadata
        };

        _sessionStopwatch = Stopwatch.StartNew();
    }

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public IReadOnlyList<string> ExecutorIds => _inner.ExecutorIds;

    /// <inheritdoc/>
    public string? WorkflowType => _inner.WorkflowType;

    /// <summary>
    /// Gets the recorded workflow trace. Available after disposal or explicitly.
    /// </summary>
    public WorkflowTrace Trace => _trace;

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        // For simple invocation, delegate to ExecuteWorkflowAsync and extract final output
        var result = await ExecuteWorkflowAsync(prompt, cancellationToken);
        return new AgentResponse
        {
            Text = result.FinalOutput
        };
    }

    /// <inheritdoc/>
    public async Task<WorkflowExecutionResult> ExecuteWorkflowAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        _trace.OriginalPrompt = SanitizeText(prompt);
        var workflowStartTime = _sessionStopwatch.ElapsedMilliseconds;

        WorkflowExecutionResult result;
        try
        {
            result = await _inner.ExecuteWorkflowAsync(prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            // Record the error
            _trace.Errors ??= new List<WorkflowTraceError>();
            _trace.Errors.Add(new WorkflowTraceError
            {
                ExecutorId = "workflow",
                Message = ex.Message,
                ExceptionType = ex.GetType().Name,
                StackTrace = _options.IncludeStackTraces ? ex.StackTrace : null,
                OccurredAtMs = _sessionStopwatch.ElapsedMilliseconds - workflowStartTime
            });
            throw;
        }

        // Record the result
        RecordWorkflowResult(result, workflowStartTime);

        return result;
    }

    private void RecordWorkflowResult(WorkflowExecutionResult result, long workflowStartTime)
    {
        _trace.FinalOutput = SanitizeText(result.FinalOutput);

        // Record each step
        foreach (var step in result.Steps)
        {
            var traceStep = new WorkflowTraceStep
            {
                ExecutorId = step.ExecutorId,
                ExecutorName = step.ExecutorName,
                StepIndex = step.StepIndex,
                Input = SanitizeText(step.Input),
                Output = SanitizeText(step.Output),
                StartOffsetMs = (long)step.StartOffset.TotalMilliseconds,
                DurationMs = (long)step.Duration.TotalMilliseconds,
                ParallelBranchId = step.ParallelBranchId
            };

            // Record tool calls
            if (step.ToolCalls != null && step.ToolCalls.Count > 0)
            {
                traceStep.ToolCalls = step.ToolCalls.Select(tc => new TraceToolCall
                {
                    Name = tc.Name,
                    Arguments = tc.Arguments != null
                        ? JsonSerializer.Serialize(tc.Arguments)
                        : null,
                    Result = SanitizeText(tc.Result?.ToString()),
                    Succeeded = tc.Exception == null,
                    Error = tc.Exception?.Message,
                    DurationMs = (long?)tc.Duration?.TotalMilliseconds
                }).ToList();
            }

            // Record token usage
            if (step.TokenUsage != null)
            {
                traceStep.TokenUsage = new TraceTokenUsage
                {
                    PromptTokens = step.TokenUsage.PromptTokens,
                    CompletionTokens = step.TokenUsage.CompletionTokens
                };
            }

            // Record edge information
            if (step.IncomingEdge != null)
            {
                traceStep.IncomingEdge = new WorkflowTraceEdge
                {
                    From = step.IncomingEdge.SourceExecutorId,
                    To = step.IncomingEdge.TargetExecutorId,
                    EdgeType = step.IncomingEdge.EdgeType.ToString(),
                    Condition = step.IncomingEdge.RoutingReason
                };
            }

            if (step.OutgoingEdges != null && step.OutgoingEdges.Count > 0)
            {
                traceStep.OutgoingEdges = step.OutgoingEdges.Select(e => new WorkflowTraceEdge
                {
                    From = e.SourceExecutorId,
                    To = e.TargetExecutorId,
                    EdgeType = e.EdgeType.ToString(),
                    Condition = e.RoutingReason
                }).ToList();
            }

            _trace.Steps.Add(traceStep);
        }

        // Record routing decisions
        if (result.RoutingDecisions != null && result.RoutingDecisions.Count > 0)
        {
            _trace.RoutingDecisions = result.RoutingDecisions.Select(rd => new WorkflowTraceRoutingDecision
            {
                RouterId = rd.DeciderExecutorId,
                Condition = rd.EvaluatedValue,
                Result = rd.SelectionReason,
                SelectedExecutorId = rd.SelectedEdgeId,
                TimestampOffsetMs = (long)rd.DecisionTime.TotalMilliseconds
            }).ToList();
        }

        // Record graph structure
        if (result.Graph != null)
        {
            _trace.Graph = new WorkflowTraceGraph
            {
                Nodes = result.Graph.Nodes.Select(n => n.NodeId).ToList(),
                EntryPoint = result.Graph.EntryNodeId,
                ExitPoints = result.Graph.ExitNodeIds?.ToList(),
                HasConditionalRouting = result.Graph.HasConditionalRouting,
                HasParallelExecution = result.Graph.HasParallelExecution,
                Edges = result.Graph.Edges.Select(e => new WorkflowTraceEdge
                {
                    From = e.SourceExecutorId,
                    To = e.TargetExecutorId,
                    EdgeType = e.EdgeType.ToString(),
                    Condition = e.Condition
                }).ToList()
            };
        }

        // Record errors
        if (result.Errors != null && result.Errors.Count > 0)
        {
            _trace.Errors = result.Errors.Select(e => new WorkflowTraceError
            {
                ExecutorId = e.ExecutorId,
                Message = e.Message,
                ExceptionType = e.ExceptionType,
                StackTrace = _options.IncludeStackTraces ? e.StackTrace : null,
                OccurredAtMs = (long)e.OccurredAt.TotalMilliseconds
            }).ToList();
        }

        // Calculate performance metrics
        _trace.Performance = new WorkflowTracePerformance
        {
            TotalDurationMs = (long)result.TotalDuration.TotalMilliseconds,
            StepCount = result.Steps.Count,
            TotalToolCalls = result.Steps.Sum(s => s.ToolCalls?.Count ?? 0),
            TotalPromptTokens = result.Steps.Sum(s => s.TokenUsage?.PromptTokens ?? 0),
            TotalCompletionTokens = result.Steps.Sum(s => s.TokenUsage?.CompletionTokens ?? 0),
            DurationByExecutor = result.Steps
                .GroupBy(s => s.ExecutorId)
                .ToDictionary(
                    g => g.Key,
                    g => (long)g.Sum(s => s.Duration.TotalMilliseconds))
        };
    }

    /// <summary>
    /// Saves the trace to a file.
    /// </summary>
    /// <param name="filePath">Path to save the trace file (.trace.json).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        FinalizeTrace();

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await WorkflowTraceSerializer.SerializeAsync(_trace, stream, cancellationToken);
    }

    /// <summary>
    /// Gets the trace as a JSON string.
    /// </summary>
    public async Task<string> ToJsonAsync(CancellationToken cancellationToken = default)
    {
        FinalizeTrace();
        return await WorkflowTraceSerializer.SerializeToStringAsync(_trace, cancellationToken);
    }

    /// <summary>
    /// Converts this workflow trace to a standard AgentTrace (for compatibility).
    /// </summary>
    public AgentTrace ToAgentTrace()
    {
        var agentTrace = new AgentTrace
        {
            Version = _trace.Version,
            TraceName = _trace.TraceName,
            CapturedAt = _trace.CapturedAt,
            AgentName = _trace.WorkflowType ?? "Workflow",
            Metadata = _trace.Metadata
        };

        // Add original prompt as request
        if (!string.IsNullOrEmpty(_trace.OriginalPrompt))
        {
            agentTrace.Entries.Add(new TraceEntry
            {
                Type = TraceEntryType.Request,
                Index = 0,
                Timestamp = _trace.CapturedAt,
                Prompt = _trace.OriginalPrompt
            });
        }

        // Add final output as response
        if (!string.IsNullOrEmpty(_trace.FinalOutput))
        {
            agentTrace.Entries.Add(new TraceEntry
            {
                Type = TraceEntryType.Response,
                Index = 0,
                Timestamp = _trace.CapturedAt.AddMilliseconds(_trace.Performance?.TotalDurationMs ?? 0),
                Text = _trace.FinalOutput,
                DurationMs = _trace.Performance?.TotalDurationMs,
                TokenUsage = _trace.Performance != null
                    ? new TraceTokenUsage
                    {
                        PromptTokens = _trace.Performance.TotalPromptTokens,
                        CompletionTokens = _trace.Performance.TotalCompletionTokens
                    }
                    : null
            });
        }

        // Set performance
        if (_trace.Performance != null)
        {
            agentTrace.Performance = new TracePerformance
            {
                TotalDurationMs = _trace.Performance.TotalDurationMs,
                TotalPromptTokens = _trace.Performance.TotalPromptTokens,
                TotalCompletionTokens = _trace.Performance.TotalCompletionTokens,
                CallCount = 1,
                ToolCallCount = _trace.Performance.TotalToolCalls
            };
        }

        return agentTrace;
    }

    private void FinalizeTrace()
    {
        _sessionStopwatch.Stop();
    }

    private string? SanitizeText(string? text)
    {
        if (text == null || !_options.SanitizeSecrets)
            return text;

        var result = text;
        foreach (var sanitizer in _options.Sanitizers)
        {
            result = sanitizer(result);
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        FinalizeTrace();

        // If the inner agent is disposable, dispose it
        if (_inner is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Options for workflow trace recording.
/// </summary>
public class WorkflowTraceRecorderOptions
{
    /// <summary>
    /// Whether to sanitize secrets from inputs and outputs.
    /// Default is true.
    /// </summary>
    public bool SanitizeSecrets { get; set; } = true;

    /// <summary>
    /// Custom sanitizer functions to apply to text.
    /// </summary>
    public List<Func<string, string>> Sanitizers { get; set; } = new();

    /// <summary>
    /// Whether to include stack traces in error records.
    /// Default is false.
    /// </summary>
    public bool IncludeStackTraces { get; set; } = false;

    /// <summary>
    /// Optional metadata to store with the trace.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>
/// Serializer for WorkflowTrace objects.
/// </summary>
public static class WorkflowTraceSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serializes a workflow trace to a stream.
    /// </summary>
    public static async Task SerializeAsync(
        WorkflowTrace trace,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        await JsonSerializer.SerializeAsync(stream, trace, JsonOptions, cancellationToken);
    }

    /// <summary>
    /// Serializes a workflow trace to a string.
    /// </summary>
    public static async Task<string> SerializeToStringAsync(
        WorkflowTrace trace,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        await SerializeAsync(trace, stream, cancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>
    /// Deserializes a workflow trace from a stream.
    /// </summary>
    public static async Task<WorkflowTrace> DeserializeAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        return await JsonSerializer.DeserializeAsync<WorkflowTrace>(stream, JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Failed to deserialize workflow trace");
    }

    /// <summary>
    /// Loads a workflow trace from a file.
    /// </summary>
    public static async Task<WorkflowTrace> LoadFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        return await DeserializeAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Saves a workflow trace to a file.
    /// </summary>
    public static async Task SaveToFileAsync(
        WorkflowTrace trace,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(filePath);
        await SerializeAsync(trace, stream, cancellationToken);
    }
}
