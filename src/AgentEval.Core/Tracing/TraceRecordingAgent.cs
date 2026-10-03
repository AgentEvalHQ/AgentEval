// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Core;

// Aliased with a Meai prefix: AgentEval.Tracing declares types whose names collide with MEAI ones (ChatRole),
// and a type in this namespace would silently win over a same-named using alias.
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;

namespace AgentEval.Tracing;

/// <summary>
/// Wraps an IEvaluableAgent to record all invocations for later replay.
/// This enables deterministic, fast, and cost-free test execution.
/// </summary>
/// <remarks>
/// <b>Threading:</b> a recorder instance is NOT thread-safe and is designed to record a single
/// conversation/session invoked sequentially. It mutates an unsynchronised index and entry list, so
/// invoking one instance from multiple threads concurrently can corrupt the trace or throw. For
/// parallel evaluation, use a separate recorder instance per concurrent flow (BUG-58).
/// <para>
/// <b>Agent-boundary account.</b> Each response entry records what the wrapped agent itself reported, so the
/// trace can stand as the agent side of a Trace Fidelity reconciliation. On the non-streaming path that is the
/// finish reason (<see cref="AgentResponse.FinishReason"/>) and the tool calls in
/// <see cref="AgentResponse.RawMessages"/> (every <c>FunctionCallContent</c>, with arguments serialized the same
/// way the chat-boundary recorder serializes them, and each paired with its result by call id). An agent that
/// surfaces no messages, or no finish reason, is recorded as reporting none. Approval-gated calls (wrapped in
/// <c>ToolApprovalRequestContent</c>) are not recorded as calls. On the streaming path the tool calls come from
/// each chunk's <see cref="AgentResponseChunk.ToolCallStarted"/> (name and arguments) and results are paired by
/// call id; no finish reason is recorded, because <see cref="AgentResponseChunk"/> carries none, so reconciling a
/// streaming trace counts every <c>content_filter</c>/<c>length</c> chat turn as a suppressed finish reason.
/// </para>
/// </remarks>
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
    private long? _timeToFirstTokenMs;
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

        // Record the response, including the finish reason and tool calls the agent reported. Without them a
        // trace recorded here, used as the agent boundary in Trace Fidelity, would report its own recording gap
        // as a suppressed finish reason on every content_filter/length turn and a missing call for every tool.
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
            } : null,
            ToolCalls = ExtractToolCalls(response.RawMessages),
            FinishReason = response.FinishReason
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
            StreamingChunks = _options.RecordStreamingChunks ? new List<TraceStreamChunk>() : null
        };

        var stopwatch = Stopwatch.StartNew();
        var previousChunkTime = stopwatch.ElapsedMilliseconds;
        var chunkIndex = 0;
        var fullText = new System.Text.StringBuilder();
        List<TraceToolCall>? toolCalls = null;
        var unpairedToolCalls = new Dictionary<string, Stack<TraceToolCall>>(StringComparer.Ordinal);
        long? timeToFirstToken = null;

        try
        {
            // Enumerate manually (rather than `await foreach`) so we can catch a fault or
            // cancellation raised by MoveNextAsync. An iterator cannot `yield return` inside a
            // try/catch, so the per-chunk catch wraps only the advance; yielding stays outside.
            await using var enumerator = _innerStreaming
                .InvokeStreamingAsync(prompt, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            while (true)
            {
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                }
                catch (Exception ex)
                {
                    // The stream faulted or was cancelled mid-enumeration. Record the error on
                    // the response entry so replay reproduces the failure instead of healing it
                    // into a successful run (BUG-24); the finally still finalizes + appends it.
                    responseEntry.Error = CreateTraceError(ex);
                    throw;
                }

                var chunk = enumerator.Current;
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
                if (_options.RecordStreamingChunks)
                {
                    responseEntry.StreamingChunks!.Add(traceChunk);
                }

                // Accumulate text
                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    fullText.Append(chunk.Text);
                }

                // Track tool calls, with their arguments (an argument-less record would read as
                // argument_drift against the chat boundary for every call that had arguments).
                if (chunk.ToolCallStarted != null)
                {
                    var started = chunk.ToolCallStarted;
                    var traceCall = ToTraceToolCall(started.CallId, started.Name, started.Arguments);
                    traceCall.StartedAt = DateTimeOffset.UtcNow;
                    toolCalls ??= new List<TraceToolCall>();
                    toolCalls.Add(traceCall);
                    TrackUnpaired(unpairedToolCalls, started.CallId, traceCall);
                }

                // Track tool results: pair each result with the call it answers by call id. (Matching a call
                // whose name occurs inside the result's call id could attach the result to a different tool.)
                if (chunk.ToolCallCompleted != null
                    && TakeUnpaired(unpairedToolCalls, chunk.ToolCallCompleted.CallId) is { } matchingCall)
                {
                    RecordToolResult(matchingCall, chunk.ToolCallCompleted.Result, chunk.ToolCallCompleted.Exception);
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

            // Keep the first streamed time-to-first-token for FinalizeTrace. It is held here rather than on a
            // partial Trace.Performance: a TracePerformance created now would carry zero token totals until the
            // trace is finalized, and Trace Fidelity reads those totals in preference to the entries, so it would
            // report the recorded tokens as under-reported.
            if (timeToFirstToken.HasValue && !_timeToFirstTokenMs.HasValue)
            {
                _timeToFirstTokenMs = timeToFirstToken.Value;
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
            TimeToFirstTokenMs = _timeToFirstTokenMs ?? _trace.Performance?.TimeToFirstTokenMs
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

    // The tool calls a non-streaming response reports: every FunctionCallContent in RawMessages (where
    // MAFAgentAdapter and ChatClientAgentAdapter put the run's messages), duplicates kept so the count matches
    // the chat boundary, each paired with its FunctionResultContent by call id. Null when the response carries
    // no MEAI messages or no tool calls.
    private List<TraceToolCall>? ExtractToolCalls(IReadOnlyList<object>? rawMessages)
    {
        if (rawMessages == null || rawMessages.Count == 0)
            return null;

        List<TraceToolCall>? toolCalls = null;
        var unpairedToolCalls = new Dictionary<string, Stack<TraceToolCall>>(StringComparer.Ordinal);
        foreach (var message in rawMessages.OfType<MeaiChatMessage>())
        {
            foreach (var content in message.Contents)
            {
                if (content is MeaiFunctionCallContent call)
                {
                    var traceCall = TraceMapping.ToToolCall(call);
                    toolCalls ??= new List<TraceToolCall>();
                    toolCalls.Add(traceCall);
                    TrackUnpaired(unpairedToolCalls, call.CallId, traceCall);
                }
                else if (content is MeaiFunctionResultContent result
                    && TakeUnpaired(unpairedToolCalls, result.CallId) is { } answered)
                {
                    RecordToolResult(answered, result.Result, result.Exception);
                }
            }
        }

        return toolCalls;
    }

    // Maps a streamed tool call through TraceMapping, the mapping the chat-boundary recorder
    // (TraceRecordingChatClient) also uses, so the same arguments serialize to the same string on both layers.
    private static TraceToolCall ToTraceToolCall(string callId, string name, IDictionary<string, object?>? arguments)
        => TraceMapping.ToToolCall(new MeaiFunctionCallContent(callId ?? string.Empty, name ?? string.Empty, arguments));

    // Remembers a recorded call under its call id until its result arrives. A call without a call id cannot be
    // paired; it keeps no result, and its Succeeded stays at the schema default.
    private static void TrackUnpaired(Dictionary<string, Stack<TraceToolCall>> unpaired, string? callId, TraceToolCall call)
    {
        if (string.IsNullOrEmpty(callId))
            return;

        if (!unpaired.TryGetValue(callId, out var pending))
        {
            pending = new Stack<TraceToolCall>();
            unpaired[callId] = pending;
        }
        pending.Push(call);
    }

    // Takes the nearest preceding call with this call id that has no result yet, so one result never answers
    // two calls and a call id reused in a later turn pairs with that turn's call. Null when there is none.
    private static TraceToolCall? TakeUnpaired(Dictionary<string, Stack<TraceToolCall>> unpaired, string? callId)
        => !string.IsNullOrEmpty(callId) && unpaired.TryGetValue(callId, out var pending) && pending.Count > 0
            ? pending.Pop()
            : null;

    private void RecordToolResult(TraceToolCall call, object? result, Exception? exception)
    {
        call.Result = SanitizeToolResult(result?.ToString());
        call.Succeeded = exception == null;
        call.Error = SanitizeToolResult(exception?.Message);
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
