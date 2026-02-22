// SPDX-License-Identifier: MIT
// Copyright (c) 2025 AgentEval. All rights reserved.
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Tracing;

/// <summary>
/// Result of a multi-turn chat/conversation execution.
/// Captures both user (simulated) and agent turns for complete traceability.
/// </summary>
/// <remarks>
/// This is the compound result for multi-turn conversations, analogous to 
/// <see cref="WorkflowExecutionResult"/> for workflow executions.
/// It enables assertions and analysis across the entire conversation.
/// </remarks>
public sealed class ChatExecutionResult
{
    /// <summary>
    /// All turns in the conversation in order.
    /// </summary>
    public required IReadOnlyList<ChatTurn> Turns { get; init; }

    /// <summary>
    /// Total duration from first user message to final agent response.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>
    /// Aggregate performance across all agent turns.
    /// </summary>
    public PerformanceMetrics? AggregatePerformance { get; init; }

    /// <summary>
    /// All tool calls across all agent turns.
    /// </summary>
    public ToolUsageReport? CombinedToolUsage { get; init; }

    /// <summary>
    /// Conversation ID for linking related operations.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Number of user turns (test inputs).
    /// </summary>
    public int UserTurnCount => Turns.Count(t => t.Role == ChatRole.User);

    /// <summary>
    /// Number of agent turns (responses).
    /// </summary>
    public int AgentTurnCount => Turns.Count(t => t.Role == ChatRole.Assistant);

    /// <summary>
    /// Total number of turns in the conversation.
    /// </summary>
    public int TotalTurnCount => Turns.Count;

    /// <summary>
    /// Gets the final agent response text.
    /// </summary>
    public string? FinalResponse => Turns
        .LastOrDefault(t => t.Role == ChatRole.Assistant)?.Content;

    /// <summary>
    /// Gets all agent response texts in order.
    /// </summary>
    public IEnumerable<string> AgentResponses => Turns
        .Where(t => t.Role == ChatRole.Assistant)
        .Select(t => t.Content);

    /// <summary>
    /// Gets all user prompts in order.
    /// </summary>
    public IEnumerable<string> UserPrompts => Turns
        .Where(t => t.Role == ChatRole.User)
        .Select(t => t.Content);

    /// <summary>
    /// Whether all agent turns completed successfully (no errors).
    /// </summary>
    public bool AllTurnsSucceeded => Turns
        .Where(t => t.Role == ChatRole.Assistant)
        .All(t => t.Error == null);

    /// <summary>
    /// Gets any turns that had errors.
    /// </summary>
    public IEnumerable<ChatTurn> FailedTurns => Turns
        .Where(t => t.Error != null);
}

/// <summary>
/// A single turn in a multi-turn conversation.
/// </summary>
public sealed class ChatTurn
{
    /// <summary>
    /// Role: User (test input) or Assistant (agent response).
    /// </summary>
    public required ChatRole Role { get; init; }

    /// <summary>
    /// Content of the turn.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Turn index (0-based).
    /// </summary>
    public int TurnIndex { get; init; }

    /// <summary>
    /// When this turn occurred (relative to conversation start).
    /// </summary>
    public TimeSpan Timestamp { get; init; }

    /// <summary>
    /// Duration of this turn (for agent turns: time to generate response).
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// For agent turns: token usage for this response.
    /// </summary>
    public TraceTokenUsage? TokenUsage { get; init; }

    /// <summary>
    /// For agent turns: tool calls made during this response.
    /// </summary>
    public IReadOnlyList<TraceToolCall>? ToolCalls { get; init; }

    /// <summary>
    /// Error information if this turn failed.
    /// </summary>
    public TraceError? Error { get; init; }

    /// <summary>
    /// Whether this turn is from the user.
    /// </summary>
    public bool IsUserTurn => Role == ChatRole.User;

    /// <summary>
    /// Whether this turn is from the assistant/agent.
    /// </summary>
    public bool IsAgentTurn => Role == ChatRole.Assistant;
}

/// <summary>
/// Role of a participant in a chat conversation.
/// </summary>
public enum ChatRole
{
    /// <summary>User/human participant (test input).</summary>
    User,

    /// <summary>Assistant/agent participant (response).</summary>
    Assistant,

    /// <summary>System message (initial context).</summary>
    System
}
