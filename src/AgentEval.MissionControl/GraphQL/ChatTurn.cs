// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// GraphQL projection of a single chat-boundary round-trip (Glass Box) for Mission Control's per-turn
/// drill-down. Hot Chocolate exposes this via convention discovery (no explicit <c>ObjectType</c>).
/// </summary>
/// <param name="Index">Round-trip index (pairing key).</param>
/// <param name="CorrelationId">Per-invocation correlation id, if any.</param>
/// <param name="FinishReason">Provider finish reason (stop / tool_calls / length / content_filter).</param>
/// <param name="Text">The model's per-turn text.</param>
/// <param name="ToolNames">Tool calls the model emitted on this turn.</param>
/// <param name="ToolDefinitions">Tool definitions advertised to the model on this turn.</param>
/// <param name="PromptTokens">Prompt tokens for this turn.</param>
/// <param name="CompletionTokens">Completion tokens for this turn.</param>
/// <param name="LatencyMs">Wall-clock latency for this turn.</param>
public sealed record ChatTurn(
    int Index,
    string? CorrelationId,
    string? FinishReason,
    string? Text,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> ToolDefinitions,
    int PromptTokens,
    int CompletionTokens,
    long LatencyMs);

/// <summary>Projects a rich <see cref="AgentTrace"/> into the chat-turn timeline Mission Control renders.</summary>
public static class ChatTurnProjection
{
    /// <summary>
    /// Returns one <see cref="ChatTurn"/> per chat-boundary Response entry (<see cref="TraceEntryScope.ChatTurn"/>),
    /// merging the matching Request entry (by <c>Index</c>) for its tool definitions. Empty for v1.0 traces.
    /// </summary>
    public static IReadOnlyList<ChatTurn> FromTrace(AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var requestsByIndex = trace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Request)
            .GroupBy(e => e.Index)
            .ToDictionary(g => g.Key, g => g.First());

        return trace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
            .OrderBy(e => e.Index)
            .Select(response =>
            {
                requestsByIndex.TryGetValue(response.Index, out var request);
                var toolDefs = request?.ToolDefinitions?.Select(d => d.Name).ToList() ?? new List<string>();
                var toolNames = response.ToolCalls?.Select(c => c.Name).ToList() ?? new List<string>();
                return new ChatTurn(
                    Index: response.Index,
                    CorrelationId: response.CorrelationId,
                    FinishReason: response.FinishReason,
                    Text: response.Text,
                    ToolNames: toolNames,
                    ToolDefinitions: toolDefs,
                    PromptTokens: response.TokenUsage?.PromptTokens ?? 0,
                    CompletionTokens: response.TokenUsage?.CompletionTokens ?? 0,
                    LatencyMs: response.DurationMs ?? 0);
            })
            .ToList();
    }
}
