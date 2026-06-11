// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;       // IEvaluableAgent, AgentResponse
using AgentEval.Testing;    // Turn (referenced in place — not relocated)

namespace AgentEval.RedTeam;

/// <summary>
/// How faithfully a multi-turn conversation was carried (Wave C, Pillar 2 — anti-overclaim). A flattened conversation
/// is structurally weaker evidence than a native one and is never aggregated as if equal.
/// </summary>
public enum ConversationFidelity
{
    /// <summary>The SUT held a real session across turns (history preserved by the SUT). Highest.</summary>
    Native = 0,

    /// <summary>A plain one-shot agent driven by re-sending the whole transcript as one prompt each turn. Lower.</summary>
    Flattened = 1,
}

/// <summary>
/// Opt-in surface for an agent that can hold a stateful multi-turn conversation (PyRIT <c>PromptChatTarget</c>
/// analogue). Mirrors the <c>IStreamableAgent</c> ISP pattern — <see cref="IEvaluableAgent"/> is untouched, so a
/// plain one-shot agent simply does not implement this and is driven via <see cref="ConversationFidelity.Flattened"/>.
/// </summary>
public interface IConversableAgent : IEvaluableAgent
{
    /// <summary>Begins a fresh, isolated conversation session with the SUT.</summary>
    Task<IAgentConversation> StartConversationAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// A live conversation with a SUT. <see cref="SendAsync"/> sends the next user message and returns the agent's reply
/// with prior turns preserved. Disposing ends/resets the session.
/// </summary>
public interface IAgentConversation : IAsyncDisposable
{
    /// <summary>How faithfully this channel carries history (Native vs Flattened).</summary>
    ConversationFidelity Fidelity { get; }

    /// <summary>The turns exchanged so far (oldest → newest).</summary>
    IReadOnlyList<Turn> History { get; }

    /// <summary>Sends the next user message and returns the agent's reply.</summary>
    Task<AgentResponse> SendAsync(string userMessage, CancellationToken cancellationToken = default);
}
