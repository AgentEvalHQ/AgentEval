// SPDX-License-Identifier: MIT
// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Tracing;

/// <summary>
/// Replays a recorded AgentTrace as a mock IEvaluableAgent.
/// Enables deterministic, fast, and cost-free test execution.
/// </summary>
/// <example>
/// <code>
/// // Replay mode - uses pre-recorded responses, no API calls
/// var trace = await TraceSerializer.LoadFromFileAsync("./traces/weather_test.trace.json");
/// var replayer = new TraceReplayingAgent(trace);
/// 
/// var response = await replayer.InvokeAsync("What's the weather?");
/// // Response is from the trace, not from the actual agent
/// </code>
/// </example>
public sealed class TraceReplayingAgent : IEvaluableAgent, IStreamableAgent
{
    private readonly AgentTrace _trace;
    private readonly TraceReplayOptions _options;
    private readonly List<(TraceEntry Request, TraceEntry Response)> _pairs;
    private int _currentIndex;

    /// <summary>
    /// Creates a new replaying agent from a recorded trace.
    /// </summary>
    /// <param name="trace">The recorded trace to replay.</param>
    /// <param name="options">Optional replay options.</param>
    public TraceReplayingAgent(AgentTrace trace, TraceReplayOptions? options = null)
    {
        _trace = trace ?? throw new ArgumentNullException(nameof(trace));
        _options = options ?? new TraceReplayOptions();
        _pairs = BuildRequestResponsePairs();
        _currentIndex = 0;
    }

    /// <summary>
    /// Creates a new replaying agent from a trace file.
    /// </summary>
    /// <param name="filePath">Path to the .trace.json file.</param>
    /// <param name="options">Optional replay options.</param>
    public static async Task<TraceReplayingAgent> FromFileAsync(string filePath, TraceReplayOptions? options = null)
    {
        var trace = await TraceSerializer.LoadFromFileAsync(filePath);
        return new TraceReplayingAgent(trace, options);
    }

    /// <summary>
    /// Gets the name of the agent from the trace.
    /// </summary>
    public string Name => _trace.AgentName ?? "ReplayingAgent";

    /// <summary>
    /// Gets the trace being replayed.
    /// </summary>
    public AgentTrace Trace => _trace;

    /// <summary>
    /// Gets the current replay index.
    /// </summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>
    /// Gets the total number of request/response pairs in the trace.
    /// </summary>
    public int TotalPairs => _pairs.Count;

    /// <summary>
    /// Gets whether all recorded entries have been replayed.
    /// </summary>
    public bool IsComplete => _currentIndex >= _pairs.Count;

    /// <summary>
    /// Replays the next recorded response.
    /// </summary>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_currentIndex >= _pairs.Count)
        {
            throw new InvalidOperationException(
                $"Trace exhausted: attempted call {_currentIndex + 1} but trace only has {_pairs.Count} recorded pairs.");
        }

        var (requestEntry, responseEntry) = _pairs[_currentIndex];

        // Validate request matches (if enabled)
        if (_options.ValidateRequests)
        {
            ValidateRequest(prompt, requestEntry, _currentIndex);
        }

        _currentIndex++;

        // Handle error responses
        if (responseEntry.Error != null)
        {
            throw new TraceReplayException(
                $"Recorded error at index {_currentIndex - 1}: {responseEntry.Error.Type} - {responseEntry.Error.Message}");
        }

        // Build the response
        var response = BuildAgentResponse(responseEntry);
        return Task.FromResult(response);
    }

    /// <summary>
    /// Replays the next recorded streaming response.
    /// </summary>
    public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
        string prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_currentIndex >= _pairs.Count)
        {
            throw new InvalidOperationException(
                $"Trace exhausted: attempted call {_currentIndex + 1} but trace only has {_pairs.Count} recorded pairs.");
        }

        var (requestEntry, responseEntry) = _pairs[_currentIndex];

        // Validate request matches (if enabled)
        if (_options.ValidateRequests)
        {
            ValidateRequest(prompt, requestEntry, _currentIndex);
        }

        _currentIndex++;

        // Handle error responses
        if (responseEntry.Error != null)
        {
            throw new TraceReplayException(
                $"Recorded error at index {_currentIndex - 1}: {responseEntry.Error.Type} - {responseEntry.Error.Message}");
        }

        // If we have streaming chunks, replay them with timing
        if (responseEntry.StreamingChunks != null && responseEntry.StreamingChunks.Count > 0)
        {
            var toolCallIndex = 0;
            foreach (var chunk in responseEntry.StreamingChunks)
            {
                // Simulate delay if requested
                if (_options.SimulateStreamingDelay && chunk.DelayMs > 0)
                {
                    await Task.Delay(chunk.DelayMs, cancellationToken);
                }

                ToolCallInfo? toolCallInfo = null;

                // Handle tool call chunks
                if (chunk.IsToolCall && responseEntry.ToolCalls != null && toolCallIndex < responseEntry.ToolCalls.Count)
                {
                    var tc = responseEntry.ToolCalls[toolCallIndex++];
                    toolCallInfo = new ToolCallInfo
                    {
                        Name = tc.Name,
                        CallId = $"trace-{chunk.Index}",
                        Arguments = null // Arguments in trace are stored as string
                    };
                }

                var isLast = chunk.Index == responseEntry.StreamingChunks.Count - 1;
                var responseChunk = new AgentResponseChunk
                {
                    Text = chunk.Text,
                    ToolCallStarted = toolCallInfo,
                    IsComplete = isLast
                };

                yield return responseChunk;
            }
        }
        else
        {
            // No streaming chunks recorded - simulate by breaking up the text
            var text = responseEntry.Text ?? string.Empty;
            var chunkSize = _options.DefaultChunkSize;

            for (var i = 0; i < text.Length; i += chunkSize)
            {
                var chunkText = text.Substring(i, Math.Min(chunkSize, text.Length - i));

                if (_options.SimulateStreamingDelay)
                {
                    await Task.Delay(_options.DefaultChunkDelayMs, cancellationToken);
                }

                var isLast = i + chunkSize >= text.Length;
                yield return new AgentResponseChunk
                {
                    Text = chunkText,
                    IsComplete = isLast
                };
            }

            // If text was empty, yield one empty chunk
            if (string.IsNullOrEmpty(text))
            {
                yield return new AgentResponseChunk
                {
                    Text = string.Empty,
                    IsComplete = true
                };
            }
        }
    }

    /// <summary>
    /// Resets the replayer to the beginning of the trace.
    /// </summary>
    public void Reset()
    {
        _currentIndex = 0;
    }

    private List<(TraceEntry Request, TraceEntry Response)> BuildRequestResponsePairs()
    {
        var pairs = new List<(TraceEntry, TraceEntry)>();

        // Group entries by index
        var byIndex = _trace.Entries
            .GroupBy(e => e.Index)
            .OrderBy(g => g.Key);

        foreach (var group in byIndex)
        {
            var request = group.FirstOrDefault(e => e.Type == TraceEntryType.Request);
            var response = group.FirstOrDefault(e => e.Type == TraceEntryType.Response);

            if (request != null && response != null)
            {
                pairs.Add((request, response));
            }
        }

        return pairs;
    }

    private void ValidateRequest(string actualPrompt, TraceEntry recordedRequest, int index)
    {
        var recordedPrompt = recordedRequest.Prompt ?? string.Empty;

        if (_options.RequestMatchingMode == RequestMatchingMode.Exact)
        {
            if (actualPrompt != recordedPrompt)
            {
                HandleMismatch(actualPrompt, recordedPrompt, index);
            }
        }
        else if (_options.RequestMatchingMode == RequestMatchingMode.Contains)
        {
            if (!actualPrompt.Contains(recordedPrompt) && !recordedPrompt.Contains(actualPrompt))
            {
                HandleMismatch(actualPrompt, recordedPrompt, index);
            }
        }
        // RequestMatchingMode.Any - no validation
    }

    private void HandleMismatch(string actual, string recorded, int index)
    {
        var message = $"Request mismatch at index {index}.\n" +
                      $"Expected: {Truncate(recorded, 100)}\n" +
                      $"Actual: {Truncate(actual, 100)}";

        switch (_options.MismatchBehavior)
        {
            case MismatchBehavior.Throw:
                throw new TraceReplayMismatchException(message, actual, recorded, index);

            case MismatchBehavior.Warn:
                // Log warning (could integrate with ILogger if available)
                Console.WriteLine($"[TraceReplayer Warning] {message}");
                break;

            case MismatchBehavior.Ignore:
                // Continue silently
                break;
        }
    }

    private static AgentResponse BuildAgentResponse(TraceEntry responseEntry)
    {
        return new AgentResponse
        {
            Text = responseEntry.Text ?? string.Empty,
            TokenUsage = responseEntry.TokenUsage != null
                ? new TokenUsage
                {
                    PromptTokens = responseEntry.TokenUsage.PromptTokens,
                    CompletionTokens = responseEntry.TokenUsage.CompletionTokens
                }
                : null
        };
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        return text.Substring(0, maxLength) + "...";
    }
}

/// <summary>
/// Options for trace replay.
/// </summary>
public class TraceReplayOptions
{
    /// <summary>
    /// Whether to validate that incoming requests match recorded requests.
    /// Default is false (any request matches the next recorded response).
    /// </summary>
    public bool ValidateRequests { get; set; } = false;

    /// <summary>
    /// How to match requests to recorded entries.
    /// Default is Any (index-based, any request matches).
    /// </summary>
    public RequestMatchingMode RequestMatchingMode { get; set; } = RequestMatchingMode.Any;

    /// <summary>
    /// What to do when a request doesn't match the recorded request.
    /// Default is Throw.
    /// </summary>
    public MismatchBehavior MismatchBehavior { get; set; } = MismatchBehavior.Throw;

    /// <summary>
    /// Whether to simulate streaming delays from recorded chunk timings.
    /// Default is false (instant replay).
    /// </summary>
    public bool SimulateStreamingDelay { get; set; } = false;

    /// <summary>
    /// Default chunk size when replaying non-streaming responses as streaming.
    /// Default is 10 characters.
    /// </summary>
    public int DefaultChunkSize { get; set; } = 10;

    /// <summary>
    /// Default delay between chunks in milliseconds when simulating streaming.
    /// Default is 20ms.
    /// </summary>
    public int DefaultChunkDelayMs { get; set; } = 20;
}

/// <summary>
/// How to match incoming requests to recorded entries.
/// </summary>
public enum RequestMatchingMode
{
    /// <summary>
    /// Any request matches the next recorded response (index-based).
    /// </summary>
    Any,

    /// <summary>
    /// Request must exactly match the recorded prompt.
    /// </summary>
    Exact,

    /// <summary>
    /// Request must contain or be contained by the recorded prompt.
    /// </summary>
    Contains
}

/// <summary>
/// What to do when a request doesn't match the recorded request.
/// </summary>
public enum MismatchBehavior
{
    /// <summary>
    /// Throw an exception.
    /// </summary>
    Throw,

    /// <summary>
    /// Log a warning but continue.
    /// </summary>
    Warn,

    /// <summary>
    /// Silently ignore the mismatch.
    /// </summary>
    Ignore
}

/// <summary>
/// Exception thrown during trace replay.
/// </summary>
public class TraceReplayException : Exception
{
    public TraceReplayException(string message) : base(message) { }
    public TraceReplayException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when a request doesn't match the recorded request.
/// </summary>
public class TraceReplayMismatchException : TraceReplayException
{
    public string ActualPrompt { get; }
    public string RecordedPrompt { get; }
    public int Index { get; }

    public TraceReplayMismatchException(string message, string actualPrompt, string recordedPrompt, int index)
        : base(message)
    {
        ActualPrompt = actualPrompt;
        RecordedPrompt = recordedPrompt;
        Index = index;
    }
}
