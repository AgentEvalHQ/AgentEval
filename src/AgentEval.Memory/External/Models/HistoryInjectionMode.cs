// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Controls how conversation history is provided to the agent during external benchmarks.
/// </summary>
/// <remarks>
/// <para>
/// <b>StructuredChatHistory</b> injects history as structured ChatMessage pairs into the
/// agent's conversation history via <c>IHistoryInjectableAgent</c>. This is faster (0 LLM calls)
/// but note that MAF <c>AIContextProvider</c>s process only the user message, not chat history —
/// so injected history won't trigger context providers or memory pipelines.
/// </para>
/// <para>
/// <b>TextBlob</b> prepends the entire history as a text blob in the user message, matching the
/// original LongMemEval paper's prompt format. This ensures all history is visible to
/// context providers, memory pipelines, and any middleware that processes user messages.
/// </para>
/// </remarks>
public enum HistoryInjectionMode
{
    /// <summary>
    /// Automatically choose the best mode based on agent capabilities.
    /// Uses StructuredChatHistory if the agent implements IHistoryInjectableAgent,
    /// otherwise falls back to TextBlob. This is the default behavior.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Inject history as structured (user, assistant) ChatMessage pairs into the agent's
    /// conversation history. Requires the agent to implement IHistoryInjectableAgent.
    /// Fast (0 LLM calls for injection) but history won't be visible to AIContextProviders.
    /// </summary>
    StructuredChatHistory,

    /// <summary>
    /// Prepend the entire conversation history as a text blob in the user message.
    /// Matches the original LongMemEval paper's prompt format. History is visible to
    /// all middleware and context providers that process the user message.
    /// Works with any agent (no interface requirement).
    /// </summary>
    TextBlob
}
