// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Core;

namespace AgentEval.Tracing;

/// <summary>
/// Wraps an IEvaluableAgent to record all invocations for later replay.
/// This enables deterministic, fast, and cost-free test execution.
/// </summary>
/// <example>
/// <code>
/// // Record mode
/// var agent = new ChatClientAgentAdapter(chatClient);
/// await using var recorder = new TraceRecordingAgent(agent, "weather_test");
/// 
/// var response = await recorder.InvokeAsync("What's the weather?");
/// // ... test assertions ...
/// 
/// await recorder.SaveAsync("./traces/weather_test.trace.json");
/// </code>
/// </example>
public sealed class TraceRecordingAgent : IEvaluableAgent, IStreamableAgent, IAsyncDisposable
{
    private readonly IEvaluableAgent _inner;
    private readonly IStreamableAgent? _innerStreaming;
    private readonly AgentTrace _trace;
    private readonly Stopwatch _sessionStopwatch;
    private readonly TraceRecordingOptions _options;
    private int _currentIndex;
    private bool _disposed;

    /// <summary>
    /// Creates a new recording wrapper around the given agent.
    /// </summary>
    /// <param name="inner">The agent to wrap and record.</param>
    /// <param name="traceName">Human-readable name for this trace.</param>
    /// <param name="options">Optional recording options.</param>
    public TraceRecordingAgent(IEvaluableAgent inner, string traceName, TraceRecordingOptions? options = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _innerStreaming = inner as IStreamableAgent;
        _options = options ?? new TraceRecordingOptions();

        _trace = new AgentTrace
        {
            TraceName = traceName,
            AgentName = _options.AgentName ?? inner.Name,
            ModelId = _options.ModelId,
            CapturedAt = DateTimeOffset.UtcNow,
            Metadata = _options.Metadata
        };

        _sessionStopwatch = Stopwatch.StartNew();
        _currentIndex = 0;
    }

    /// <summary>
    /// Gets the name of the agent (from the wrapped agent).
    /// </summary>
    public string Name => _inner.Name;

    /// <summary>
    /// Gets the recorded trace. Available after disposal or explicitly.
    /// </summary>
    public AgentTrace Trace => _trace;

    /// <summary>
    /// Invokes the wrapped agent and records the request/response.
    /// </summary>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var index = _currentIndex++;
        var requestTimestamp = DateTimeOffset.UtcNow;

        // Record the request
        var requestEntry = new TraceEntry
        {
            Type = TraceEntryType.Request,
            Index = index,
            Timestamp = requestTimestamp,
            Prompt = SanitizePrompt(prompt)
        };
        _trace.Entries.Add(requestEntry);

        // Invoke the real agent
        var stopwatch = Stopwatch.StartNew();
        AgentResponse response;
        TraceError? error = null;

        try
        {
            response = await _inner.InvokeAsync(prompt, cancellationToken);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            error = CreateTraceError(ex);

            // Record the error response
            var errorEntry = new TraceEntry
            {
                Type = TraceEntryType.Response,
                Index = index,
                Timestamp = DateTimeOffset.UtcNow,
                DurationMs = stopwatch.ElapsedMilliseconds,
                Error = error
            };
            _trace.Entries.Add(errorEntry);

            throw;
        }

        stopwatch.Stop();

        // Record the response
        var responseEntry = new TraceEntry
        {
            Type = TraceEntryType.Response,
            Index = index,
            Timestamp = DateTimeOffset.UtcNow,
            Text = SanitizeResponse(response.Text),
            DurationMs = stopwatch.ElapsedMilliseconds,
            IsStreaming = false,
            TokenUsage = response.TokenUsage != null ? new TraceTokenUsage
            {
                PromptTokens = response.TokenUsage.PromptTokens,
                CompletionTokens = response.TokenUsage.CompletionTokens
            } : null
        };
        _trace.Entries.Add(responseEntry);

        return response;
    }

    /// <summary>
    /// Invokes the wrapped agent in streaming mode and records chunks.
    /// </summary>
    public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
        string prompt, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_innerStreaming == null)
        {
            throw new NotSupportedException($"The wrapped agent ({_inner.GetType().Name}) does not support streaming.");
        }

        var index = _currentIndex++;
        var requestTimestamp = DateTimeOffset.UtcNow;

        // Record the request
        var requestEntry = new TraceEntry
        {
            Type = TraceEntryType.Request,
            Index = index,
            Timestamp = requestTimestamp,
            Prompt = SanitizePrompt(prompt)
        };
        _trace.Entries.Add(requestEntry);

        // Prepare response entry (we'll build it as chunks arrive)
        var responseEntry = new TraceEntry
        {
            Type = TraceEntryType.Response,
            Index = index,
            IsStreaming = true,
            StreamingChunks = new List<TraceStreamChunk>()
        };

        var stopwatch = Stopwatch.StartNew();
        var previousChunkTime = stopwatch.ElapsedMilliseconds;
        var chunkIndex = 0;
        var fullText = new System.Text.StringBuilder();
        List<TraceToolCall>? toolCalls = null;
        long? timeToFirstToken = null;

        try
        {
            await foreach (var chunk in _innerStreaming.InvokeStreamingAsync(prompt, cancellationToken))
            {
                var currentTime = stopwatch.ElapsedMilliseconds;
                var delay = (int)(currentTime - previousChunkTime);

                // Record time to first token
                if (timeToFirstToken == null && !string.IsNullOrEmpty(chunk.Text))
                {
                    timeToFirstToken = currentTime;
                }

                // Record the chunk
                var traceChunk = new TraceStreamChunk
                {
                    Index = chunkIndex++,
                    Text = chunk.Text,
                    DelayMs = delay,
                    IsToolCall = chunk.ToolCallStarted != null,
                    ToolName = chunk.ToolCallStarted?.Name
                };
                responseEntry.StreamingChunks!.Add(traceChunk);

                // Accumulate text
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    fullText.Append(chunk.Text);
                }

                // Track tool calls
                if (chunk.ToolCallStarted != null)
                {
                    toolCalls ??= new List<TraceToolCall>();
                    toolCalls.Add(new TraceToolCall
                    {
                        Name = chunk.ToolCallStarted.Name,
                        StartedAt = DateTimeOffset.UtcNow
                    });
                }

                // Track tool results
                if (chunk.ToolCallCompleted != null && toolCalls != null)
                {
                    var matchingCall = toolCalls.FirstOrDefault(tc => 
                        tc.Name != null && chunk.ToolCallCompleted.CallId.Contains(tc.Name));
                    if (matchingCall != null)
                    {
                        matchingCall.Result = chunk.ToolCallCompleted.Result?.ToString();
                        matchingCall.Succeeded = chunk.ToolCallCompleted.Exception == null;
                        matchingCall.Error = chunk.ToolCallCompleted.Exception?.Message;
                    }
                }

                // Capture token usage from final chunk
                if (chunk.IsComplete && chunk.Usage != null)
                {
                    responseEntry.TokenUsage = new TraceTokenUsage
                    {
                        PromptTokens = chunk.Usage.PromptTokens,
                        CompletionTokens = chunk.Usage.CompletionTokens
                    };
                }
                
                previousChunkTime = currentTime;
                yield return chunk;
            }
        }
        finally
        {
            stopwatch.Stop();

            // Complete the response entry
            responseEntry.Timestamp = DateTimeOffset.UtcNow;
            responseEntry.Text = SanitizeResponse(fullText.ToString());
            responseEntry.DurationMs = stopwatch.ElapsedMilliseconds;
            responseEntry.ToolCalls = toolCalls;

            _trace.Entries.Add(responseEntry);

            // Update performance metrics
            if (_trace.Performance == null)
            {
                _trace.Performance = new TracePerformance();
            }
            if (timeToFirstToken.HasValue && !_trace.Performance.TimeToFirstTokenMs.HasValue)
            {
                _trace.Performance.TimeToFirstTokenMs = timeToFirstToken.Value;
            }
        }
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
        await TraceSerializer.SerializeAsync(_trace, stream, cancellationToken);
    }

    /// <summary>
    /// Gets the trace as a JSON string.
    /// </summary>
    public async Task<string> ToJsonAsync(CancellationToken cancellationToken = default)
    {
        FinalizeTrace();
        return await TraceSerializer.SerializeToStringAsync(_trace, cancellationToken);
    }

    private void FinalizeTrace()
    {
        _sessionStopwatch.Stop();

        // Calculate aggregate performance
        var responses = _trace.Entries.Where(e => e.Type == TraceEntryType.Response).ToList();

        _trace.Performance = new TracePerformance
        {
            TotalDurationMs = _sessionStopwatch.ElapsedMilliseconds,
            TotalPromptTokens = responses.Sum(r => r.TokenUsage?.PromptTokens ?? 0),
            TotalCompletionTokens = responses.Sum(r => r.TokenUsage?.CompletionTokens ?? 0),
            CallCount = responses.Count,
            ToolCallCount = responses.Sum(r => r.ToolCalls?.Count ?? 0),
            TimeToFirstTokenMs = _trace.Performance?.TimeToFirstTokenMs
        };
    }

    private string SanitizePrompt(string prompt)
    {
        if (!_options.SanitizeSecrets)
            return prompt;

        return ApplySanitizers(prompt);
    }

    private string SanitizeResponse(string? response)
    {
        if (response == null || !_options.SanitizeSecrets)
            return response ?? string.Empty;

        return ApplySanitizers(response);
    }

    private string? SanitizeToolResult(string? result)
    {
        if (result == null || !_options.SanitizeSecrets)
            return result;

        return ApplySanitizers(result);
    }

    private string ApplySanitizers(string text)
    {
        var result = text;
        foreach (var sanitizer in _options.Sanitizers)
        {
            result = sanitizer(result);
        }
        return result;
    }

    private static TraceError CreateTraceError(Exception ex)
    {
        return new TraceError
        {
            Type = ex.GetType().Name,
            Message = ex.Message,
            StackTrace = ex.StackTrace
        };
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
/// Options for trace recording.
/// </summary>
public class TraceRecordingOptions
{
    /// <summary>
    /// Optional name for the agent being recorded.
    /// </summary>
    public string? AgentName { get; set; }

    /// <summary>
    /// Optional model identifier.
    /// </summary>
    public string? ModelId { get; set; }

    /// <summary>
    /// Whether to sanitize secrets from prompts and responses.
    /// Default is true.
    /// </summary>
    public bool SanitizeSecrets { get; set; } = true;

    /// <summary>
    /// Custom sanitizer functions to apply to text.
    /// Each function receives text and returns sanitized text.
    /// </summary>
    public List<Func<string, string>> Sanitizers { get; set; } = new();

    /// <summary>
    /// Optional metadata to store with the trace.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Whether to record streaming chunks with timing.
    /// Default is true.
    /// </summary>
    public bool RecordStreamingChunks { get; set; } = true;
}
