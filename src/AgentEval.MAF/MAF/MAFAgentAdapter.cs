// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentResponse = AgentEval.Core.AgentResponse;

namespace AgentEval.MAF;

/// <summary>
/// Adapts a Microsoft Agent Framework (MAF) AIAgent for testing with AgentEval.
/// </summary>
public class MAFAgentAdapter : IStreamableAgent, ISessionResettableAgent, IHistoryInjectableAgent
{
    private readonly AIAgent _agent;
    private AgentSession? _session;
    private readonly List<ChatMessage> _injectedHistory = new();
    
    /// <summary>
    /// Create an adapter for an AIAgent.
    /// </summary>
    /// <param name="agent">The MAF agent to adapt.</param>
    /// <param name="session">Optional session for conversation context. If null, a new session is lazily created on first invocation and reused for subsequent calls.</param>
    public MAFAgentAdapter(AIAgent agent, AgentSession? session = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _session = session;
    }
    
    /// <inheritdoc/>
    public string Name => _agent.Name ?? string.Empty;
    
    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        _session ??= await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        
        // Build message list: injected history + current prompt
        var messages = BuildMessages(prompt);
        var response = await _agent.RunAsync(messages, _session, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        // Clear injected history after first use — the agent's session tracks it going forward
        _injectedHistory.Clear();
        
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
            TokenUsage = tokenUsage
        };
    }
    
    /// <inheritdoc/>
    public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
        string prompt, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _session ??= await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        TokenUsage? capturedUsage = null;
        
        // Build message list: injected history + current prompt
        var messages = BuildMessages(prompt);
        
        // Clear injected history after first use — the agent's session tracks it going forward
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
        
        yield return new AgentResponseChunk { IsComplete = true, Usage = capturedUsage };
    }
    
    /// <summary>
    /// Reset the conversation session.
    /// </summary>
    public async Task ResetSessionAsync(CancellationToken cancellationToken = default)
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
    private List<ChatMessage> BuildMessages(string prompt)
    {
        var messages = new List<ChatMessage>(_injectedHistory.Count + 1);
        messages.AddRange(_injectedHistory);
        messages.Add(new ChatMessage(ChatRole.User, prompt));
        return messages;
    }
}
