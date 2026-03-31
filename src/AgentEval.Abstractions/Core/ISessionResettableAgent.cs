// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>
/// Capability interface for agents that can reset their session/conversation context
/// while preserving long-term memory. This enables cross-session memory evaluation —
/// planting facts in one session, resetting, then testing recall in a new session.
/// </summary>
/// <remarks>
/// <para>
/// This is an optional capability interface, similar to <see cref="IStreamableAgent"/>.
/// Agents implement it alongside <see cref="IEvaluableAgent"/> to signal session reset support.
/// Evaluators check via <c>agent is ISessionResettableAgent</c> pattern matching.
/// </para>
/// <para>
/// Built-in adapters that implement this interface:
/// <list type="bullet">
///   <item><description><c>ChatClientAgentAdapter</c> (Microsoft.Extensions.AI)</description></item>
///   <item><description><c>MAFAgentAdapter</c> (Microsoft Agent Framework)</description></item>
/// </list>
/// </para>
/// </remarks>
public interface ISessionResettableAgent
{
    /// <summary>
    /// Resets the agent's conversation session/context while preserving its long-term memory.
    /// This simulates starting a new conversation with the same agent.
    /// </summary>
    /// <remarks>
    /// Implementations should:
    /// <list type="bullet">
    ///   <item><description>Clear conversation history/context</description></item>
    ///   <item><description>Reset session state</description></item>
    ///   <item><description>Preserve learned facts, user preferences, and long-term memory</description></item>
    /// </list>
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the session reset operation.</returns>
    Task ResetSessionAsync(CancellationToken cancellationToken = default);
}
