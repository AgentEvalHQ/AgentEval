// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Core;

/// <summary>
/// Capability interface for agents that allow synthetic history injection.
/// This enables memory benchmarks to pre-fill the conversation context without making
/// LLM calls — making evaluations faster and testing memory under realistic context pressure.
/// </summary>
/// <remarks>
/// Used by memory benchmarks to simulate a long conversation before planting facts.
/// The injected messages appear in the agent's context as if they were real exchanges,
/// pushing the context window closer to its limit and testing whether the agent can
/// still recall facts planted after the injection.
/// </remarks>
public interface IHistoryInjectableAgent
{
    /// <summary>
    /// Injects synthetic conversation turns into the agent's history without making LLM calls.
    /// Each tuple is (userMessage, assistantResponse).
    /// </summary>
    /// <param name="conversationTurns">Pairs of (user message, assistant response) to inject.</param>
    void InjectConversationHistory(IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns);
}
