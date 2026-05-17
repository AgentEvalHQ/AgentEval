// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Conversation;

/// <summary>
/// One turn in a multi-turn conversation. The canonical element type for the
/// <c>conversation_history</c> metadata key consumed by memory, multi-turn, and
/// self-correction evaluators.
/// </summary>
/// <param name="Role">Common chat roles: <c>"user"</c>, <c>"assistant"</c>, <c>"system"</c>, <c>"tool"</c>.</param>
/// <param name="Content">The turn's text content.</param>
/// <param name="Timestamp">Optional timestamp; null when not available.</param>
public sealed record ConversationTurn(string Role, string Content, DateTimeOffset? Timestamp = null);
