// SPDX-License-Identifier: MIT
// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Tracing;

/// <summary>
/// Records multi-turn conversations with an agent and produces a ChatExecutionResult.
/// </summary>
/// <remarks>
/// This class extends trace recording to multi-turn scenarios, capturing the full
/// conversation flow including user prompts, agent responses, tool calls, and timing.
/// </remarks>
/// <example>
/// <code>
/// var chatRecorder = new ChatTraceRecorder(agent, "customer_support_test");
/// 
/// // Simulate multi-turn conversation
/// await chatRecorder.AddUserTurnAsync("Hello, I need help with my order");
/// await chatRecorder.AddUserTurnAsync("Order #12345");
/// await chatRecorder.AddUserTurnAsync("I want to return it");
/// 
/// // Get the complete execution result
/// var result = chatRecorder.GetResult();
/// 
/// // Assert on the conversation
/// result.TotalTurnCount.Should().Be(6); // 3 user + 3 agent turns
/// result.AllTurnsSucceeded.Should().BeTrue();
/// </code>
/// </example>
public sealed class ChatTraceRecorder : IAsyncDisposable
{
    private readonly IEvaluableAgent _agent;
    private readonly string _conversationId;
    private readonly ChatTraceRecorderOptions _options;
    private readonly List<ChatTurn> _turns = new();
    private readonly Stopwatch _sessionStopwatch;
    private readonly ToolUsageReport _combinedToolUsage = new();
    private int _totalPromptTokens;
    private int _totalCompletionTokens;
    private int _turnIndex;
    private bool _disposed;

    /// <summary>
    /// Creates a new chat trace recorder.
    /// </summary>
    /// <param name="agent">The agent to converse with.</param>
    /// <param name="conversationId">Optional ID for this conversation.</param>
    /// <param name="options">Optional recording options.</param>
    public ChatTraceRecorder(
        IEvaluableAgent agent,
        string? conversationId = null,
        ChatTraceRecorderOptions? options = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _conversationId = conversationId ?? Guid.NewGuid().ToString("N")[..8];
        _options = options ?? new ChatTraceRecorderOptions();
        _sessionStopwatch = Stopwatch.StartNew();
        _turnIndex = 0;
    }

    /// <summary>
    /// Gets the conversation ID.
    /// </summary>
    public string ConversationId => _conversationId;

    /// <summary>
    /// Gets the current turn count.
    /// </summary>
    public int TurnCount => _turns.Count;

    /// <summary>
    /// Adds a user turn and gets the agent's response.
    /// </summary>
    /// <param name="userMessage">The user's message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent's response text.</returns>
    public async Task<string> AddUserTurnAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (_turns.Count >= _options.MaxTurns)
        {
            throw new InvalidOperationException(
                $"Maximum turn count ({_options.MaxTurns}) reached. Cannot add more conversation turns.");
        }

        // Record user turn
        var userTimestamp = _sessionStopwatch.Elapsed;
        _turns.Add(new ChatTurn
        {
            Role = ChatRole.User,
            Content = userMessage,
            TurnIndex = _turnIndex++,
            Timestamp = userTimestamp
        });

        // Get agent response
        var turnStopwatch = Stopwatch.StartNew();
        AgentResponse response;
        TraceError? error = null;

        try
        {
            response = await _agent.InvokeAsync(userMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            turnStopwatch.Stop();
            error = new TraceError
            {
                Type = ex.GetType().Name,
                Message = ex.Message,
                StackTrace = ex.StackTrace
            };

            // Record failed agent turn
            _turns.Add(new ChatTurn
            {
                Role = ChatRole.Assistant,
                Content = string.Empty,
                TurnIndex = _turnIndex++,
                Timestamp = _sessionStopwatch.Elapsed,
                Duration = turnStopwatch.Elapsed,
                Error = error
            });

            throw;
        }

        turnStopwatch.Stop();

        // Track token usage
        if (response.TokenUsage != null)
        {
            _totalPromptTokens += response.TokenUsage.PromptTokens;
            _totalCompletionTokens += response.TokenUsage.CompletionTokens;
        }

        // Record agent turn
        _turns.Add(new ChatTurn
        {
            Role = ChatRole.Assistant,
            Content = response.Text,
            TurnIndex = _turnIndex++,
            Timestamp = _sessionStopwatch.Elapsed,
            Duration = turnStopwatch.Elapsed,
            TokenUsage = response.TokenUsage != null ? new TraceTokenUsage
            {
                PromptTokens = response.TokenUsage.PromptTokens,
                CompletionTokens = response.TokenUsage.CompletionTokens
            } : null
        });

        return response.Text;
    }

    /// <summary>
    /// Adds a system message at the beginning of the conversation.
    /// </summary>
    /// <param name="systemMessage">The system message.</param>
    public void AddSystemMessage(string systemMessage)
    {
        if (_turns.Any())
        {
            throw new InvalidOperationException("System message must be added before any conversation turns.");
        }

        _turns.Insert(0, new ChatTurn
        {
            Role = ChatRole.System,
            Content = systemMessage,
            TurnIndex = _turnIndex++,
            Timestamp = TimeSpan.Zero
        });
    }

    /// <summary>
    /// Gets the complete chat execution result.
    /// </summary>
    public ChatExecutionResult GetResult()
    {
        _sessionStopwatch.Stop();

        var aggregatePerformance = new PerformanceMetrics
        {
            StartTime = DateTimeOffset.UtcNow - _sessionStopwatch.Elapsed,
            EndTime = DateTimeOffset.UtcNow,
            PromptTokens = _totalPromptTokens,
            CompletionTokens = _totalCompletionTokens
        };

        return new ChatExecutionResult
        {
            Turns = _turns.AsReadOnly(),
            TotalDuration = _sessionStopwatch.Elapsed,
            AggregatePerformance = aggregatePerformance,
            CombinedToolUsage = _combinedToolUsage,
            ConversationId = _conversationId
        };
    }

    /// <summary>
    /// Converts the chat execution to an AgentTrace for file storage.
    /// </summary>
    public AgentTrace ToAgentTrace()
    {
        var entries = new List<TraceEntry>();
        var index = 0;

        foreach (var turn in _turns)
        {
            if (turn.Role == ChatRole.User || turn.Role == ChatRole.System)
            {
                entries.Add(new TraceEntry
                {
                    Type = TraceEntryType.Request,
                    Index = index,
                    Timestamp = DateTimeOffset.UtcNow - (_sessionStopwatch.Elapsed - turn.Timestamp),
                    Prompt = turn.Content
                });
            }
            else if (turn.Role == ChatRole.Assistant)
            {
                entries.Add(new TraceEntry
                {
                    Type = TraceEntryType.Response,
                    Index = index,
                    Timestamp = DateTimeOffset.UtcNow - (_sessionStopwatch.Elapsed - turn.Timestamp),
                    Text = turn.Content,
                    DurationMs = (long?)turn.Duration?.TotalMilliseconds,
                    TokenUsage = turn.TokenUsage,
                    ToolCalls = turn.ToolCalls?.ToList(),
                    Error = turn.Error
                });
                index++;
            }
        }

        return new AgentTrace
        {
            Version = "1.0",
            TraceName = $"chat_{_conversationId}",
            AgentName = _agent.GetType().Name,
            CapturedAt = DateTimeOffset.UtcNow,
            Entries = entries,
            Performance = new TracePerformance
            {
                TotalDurationMs = (long)_sessionStopwatch.Elapsed.TotalMilliseconds,
                TotalPromptTokens = _totalPromptTokens,
                TotalCompletionTokens = _totalCompletionTokens,
                CallCount = _turns.Count(t => t.Role == ChatRole.Assistant)
            },
            Metadata = new Dictionary<string, object>
            {
                ["conversationId"] = _conversationId,
                ["turnCount"] = _turns.Count
            }
        };
    }

    /// <summary>
    /// Saves the conversation trace to a file.
    /// </summary>
    public async Task SaveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var trace = ToAgentTrace();
        await TraceSerializer.SaveToFileAsync(trace, filePath, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _sessionStopwatch.Stop();

        if (_agent is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_agent is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Options for chat trace recording.
/// </summary>
public class ChatTraceRecorderOptions
{
    /// <summary>
    /// Whether to include system messages in the trace.
    /// Default is true.
    /// </summary>
    public bool IncludeSystemMessages { get; set; } = true;

    /// <summary>
    /// Maximum number of turns to record before stopping.
    /// Default is 100.
    /// </summary>
    public int MaxTurns { get; set; } = 100;

    /// <summary>
    /// Whether to track tool calls from agent responses.
    /// Default is true.
    /// </summary>
    public bool TrackToolCalls { get; set; } = true;
}
