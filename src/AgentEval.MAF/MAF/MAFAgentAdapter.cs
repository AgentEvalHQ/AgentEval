// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentResponse = AgentEval.Core.AgentResponse;

namespace AgentEval.MAF;

/// <summary>
/// Adapts a Microsoft Agent Framework (MAF) AIAgent for testing with AgentEval.
/// </summary>
/// <remarks>
/// <para>
/// This adapter implements <see cref="IHistoryInjectableAgent"/> to support one-time snapshot
/// injection of prior conversation turns before evaluation. Injected messages are prepended to
/// the next invocation only and cleared automatically afterwards (single-use snapshot pattern).
/// </para>
/// <para>
/// <strong>Limitation — AIContextProvider pipeline is bypassed:</strong><br/>
/// Messages injected via <see cref="InjectConversationHistory"/> are prepended directly to the
/// messages list passed to <c>RunAsync</c>/<c>RunStreamingAsync</c>, bypassing any
/// <c>AIContextProvider</c> configured on the agent (e.g. <c>InMemoryChatHistoryProvider</c>).
/// If the agent uses an <c>InMemoryChatHistoryProvider</c> with a compaction strategy, injected
/// messages are not visible to that reducer — they will not be compacted and may exceed the
/// configured context window. Use <see cref="InjectConversationHistory"/> only when the agent
/// has no managed history provider, or when a one-time seed is acceptable for evaluation
/// purposes.
/// </para>
/// </remarks>
public class MAFAgentAdapter : IStreamableAgent, ISessionResettableAgent, IHistoryInjectableAgent
{
    private protected readonly AIAgent _agent;
    private protected AgentSession? _session;
    private readonly List<ChatMessage> _injectedHistory = new();
    
    /// <summary>
    /// Create an adapter for an AIAgent.
    /// </summary>
    /// <param name="agent">The MAF agent to adapt.</param>
    /// <param name="session">Optional session for conversation context. If null, a new session is lazily created on first invocation and reused for all subsequent calls on this adapter instance.</param>
    public MAFAgentAdapter(AIAgent agent, AgentSession? session = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _session = session;
    }
    
    /// <inheritdoc/>
    public string Name => _agent.Name ?? string.Empty;
    
    /// <summary>
    /// Model identifier to embed in responses. Override in subclasses to tag responses with a model ID.
    /// </summary>
    protected virtual string? ResponseModelId => null;
    
    /// <inheritdoc/>
    public virtual async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        _session ??= await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        
        // Build message list: injected history + current prompt
        var messages = BuildMessages(prompt);
        
        // Clear injected history before the call — ensures it is used for exactly one invocation
        // even if RunAsync throws (single-use snapshot pattern).
        _injectedHistory.Clear();
        
        var response = await _agent.RunAsync(messages, _session, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        // Extract token usage from AgentResponse.Usage property
        TokenUsage? tokenUsage = null;
        if (response.Usage != null)
        {
            tokenUsage = new TokenUsage
            {
                PromptTokens = (int)(response.Usage.InputTokenCount ?? 0),
                CompletionTokens = (int)(response.Usage.OutputTokenCount ?? 0)
            };
        }
        
        return new AgentResponse
        {
            Text = response.Text,
            RawMessages = response.Messages.ToList(),
            TokenUsage = tokenUsage,
            ModelId = ResponseModelId,
            FinishReason = response.FinishReason?.ToString(),
            AdditionalProperties = BuildAdditionalProperties(response)
        };
    }
    
    /// <inheritdoc/>
    public virtual async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
        string prompt, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _session ??= await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        TokenUsage? capturedUsage = null;
        
        // Build message list: injected history + current prompt, then clear immediately —
        // ensures it is used for exactly one invocation even if streaming throws or is cancelled.
        var messages = BuildMessages(prompt);
        _injectedHistory.Clear();
        
        await foreach (var update in _agent.RunStreamingAsync(messages, _session, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text when !string.IsNullOrEmpty(text.Text):
                        yield return new AgentResponseChunk { Text = text.Text };
                        break;
                    
                    case FunctionCallContent call:
                        yield return new AgentResponseChunk
                        {
                            ToolCallStarted = new ToolCallInfo
                            {
                                Name = call.Name,
                                CallId = call.CallId,
                                Arguments = call.Arguments
                            }
                        };
                        break;
                    
                    case FunctionResultContent result:
                        yield return new AgentResponseChunk
                        {
                            ToolCallCompleted = new ToolResultInfo
                            {
                                CallId = result.CallId,
                                Result = result.Result,
                                Exception = result.Exception
                            }
                        };
                        break;
                    
                    // Check for UsageContent if provider sends it in streaming
                    case UsageContent usage:
                        capturedUsage = new TokenUsage
                        {
                            PromptTokens = (int)(usage.Details.InputTokenCount ?? 0),
                            CompletionTokens = (int)(usage.Details.OutputTokenCount ?? 0)
                        };
                        break;
                }
            }
        }

        yield return new AgentResponseChunk { IsComplete = true, Usage = capturedUsage, ModelId = ResponseModelId };
    }
    
    /// <summary>
    /// Reset the conversation session.
    /// </summary>
    public virtual async Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        _session = await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        _injectedHistory.Clear();
    }
    
    /// <summary>
    /// Create a new session for fresh conversations.
    /// </summary>
    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        => await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
    
    /// <inheritdoc/>
    /// <remarks>
    /// Injected messages are prepended as user/assistant turn pairs on the very next invocation
    /// only. They bypass any <c>AIContextProvider</c> (e.g. <c>InMemoryChatHistoryProvider</c>)
    /// configured on the underlying agent, so compaction strategies will not see them.
    /// After the first invocation completes, the injected messages are cleared automatically.
    /// </remarks>
    public void InjectConversationHistory(IEnumerable<(string UserMessage, string AssistantResponse)> conversationTurns)
    {
        foreach (var (userMessage, assistantResponse) in conversationTurns)
        {
            _injectedHistory.Add(new ChatMessage(ChatRole.User, userMessage));
            _injectedHistory.Add(new ChatMessage(ChatRole.Assistant, assistantResponse));
        }
    }
    
    /// <summary>
    /// Builds the message list from injected history and current prompt.
    /// </summary>
    protected List<ChatMessage> BuildMessages(string prompt)
    {
        var messages = new List<ChatMessage>(_injectedHistory.Count + 1);
        messages.AddRange(_injectedHistory);
        messages.Add(new ChatMessage(ChatRole.User, prompt));
        return messages;
    }
    
    /// <summary>
    /// Serializes the current agent session for persistence or replay.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no active session exists.</exception>
    public async Task<JsonElement> SerializeSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null)
            throw new InvalidOperationException("No active session to serialize. Invoke the agent at least once to create a session.");
        return await _agent.SerializeSessionAsync(_session, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    
    /// <summary>
    /// Restores a previously serialized session, replacing the current session.
    /// </summary>
    /// <remarks>
    /// Clears any pending injected history so that history injected before the restore
    /// is not prepended to the first invocation on the restored session.
    /// </remarks>
    public async Task RestoreSessionAsync(JsonElement serializedState, CancellationToken cancellationToken = default)
    {
        _session = await _agent.DeserializeSessionAsync(serializedState, cancellationToken: cancellationToken).ConfigureAwait(false);
        _injectedHistory.Clear();
    }

#pragma warning disable MEAI001 // ContinuationToken is experimental
    private static IReadOnlyDictionary<string, object?>? BuildAdditionalProperties(Microsoft.Agents.AI.AgentResponse response)
    {
        if (response.ContinuationToken is null && response.CreatedAt is null)
            return null;

        var props = new Dictionary<string, object?>();
        if (response.ContinuationToken is not null)
            props["ContinuationToken"] = response.ContinuationToken;
        if (response.CreatedAt is not null)
            props["CreatedAt"] = response.CreatedAt;
        return props;
    }
#pragma warning restore MEAI001
}
