// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;       // IEvaluableAgent, AgentResponse
using AgentEval.Testing;    // Turn (referenced in place — not relocated)

namespace AgentEval.RedTeam;   // CanaryTool / IToolAwareAttack share this namespace (under RedTeam/Harness)

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
/// A live conversation with a SUT. <see cref="SendAsync(string, CancellationToken)"/> sends the next user message and returns the agent's reply
/// with prior turns preserved. Disposing ends/resets the session.
/// </summary>
public interface IAgentConversation : IAsyncDisposable
{
    /// <summary>How faithfully this channel carries history (Native vs Flattened).</summary>
    ConversationFidelity Fidelity { get; }

    /// <summary>Jun14-L12: true when this channel actually routes canary tools to the SUT (i.e. it overrides the
    /// tool-aware <see cref="SendAsync(string, IReadOnlyList{CanaryTool}, CancellationToken)"/> DIM). Default
    /// <c>false</c> — matching the DIM that ignores tools — so the orchestrator only falls back to the flattened tool
    /// path when the native channel genuinely can't carry tools, and a native channel that CAN keeps Native fidelity.</summary>
    bool CarriesTools => false;

    /// <summary>The turns exchanged so far (oldest → newest).</summary>
    IReadOnlyList<Turn> History { get; }

    /// <summary>Sends the next user message and returns the agent's reply.</summary>
    Task<AgentResponse> SendAsync(string userMessage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends the next user message with a set of canary tools available to the agent for this turn — the Wave B↔C
    /// composition that lets a multi-turn <see cref="IToolAwareAttack"/> exercise a real tool boundary over the
    /// conversation channel. The default implementation IGNORES <paramref name="tools"/> and falls back to the
    /// text-only <see cref="SendAsync(string, CancellationToken)"/>: a conversation channel that cannot carry a tool
    /// surface degrades honestly to text-only (Verbal evidence) rather than fabricating tool execution. A tool-capable
    /// conversation overrides this to route the tools to the SUT (Behavioral evidence when a canary is invoked).
    /// </summary>
    Task<AgentResponse> SendAsync(string userMessage, IReadOnlyList<CanaryTool> tools, CancellationToken cancellationToken = default)
        => SendAsync(userMessage, cancellationToken);
}
