// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Comparison;
using AgentResponse = AgentEval.Core.AgentResponse;

namespace AgentEval.MAF;

/// <summary>
/// Adapts a Microsoft Agent Framework (MAF) AIAgent for testing with AgentEval,
/// with support for model identification for comparison scenarios.
/// </summary>
public class MAFIdentifiableAgentAdapter : IStreamableAgent, IModelIdentifiable
{
    private readonly AIAgent _agent;
    private AgentSession? _session;
    
    /// <summary>
    /// Create an adapter for an AIAgent with model identification.
    /// </summary>
    /// <param name="agent">The MAF agent to adapt.</param>
    /// <param name="modelId">Unique identifier for the model (e.g., "gpt-4o-2024-08-06").</param>
    /// <param name="modelDisplayName">Human-readable model name (e.g., "GPT-4o").</param>
    /// <param name="session">Optional session for conversation context.</param>
    public MAFIdentifiableAgentAdapter(
        AIAgent agent, 
        string modelId,
        string modelDisplayName,
        AgentSession? session = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        ModelDisplayName = modelDisplayName ?? throw new ArgumentNullException(nameof(modelDisplayName));
        _session = session;
    }
    
    /// <inheritdoc/>
    public string Name => _agent.Name ?? string.Empty;
    
    /// <inheritdoc/>
    public string ModelId { get; }
    
    /// <inheritdoc/>
    public string ModelDisplayName { get; }
    
    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var session = _session ?? await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var response = await _agent.RunAsync(prompt, session, cancellationToken: cancellationToken).ConfigureAwait(false);
        
        return new AgentResponse
        {
            Text = response.Text,
            RawMessages = response.Messages.ToList()
        };
    }
    
    /// <inheritdoc/>
    public async IAsyncEnumerable<AgentResponseChunk> InvokeStreamingAsync(
        string prompt, 
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = _session ?? await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        TokenUsage? capturedUsage = null;
        
        await foreach (var update in _agent.RunStreamingAsync(prompt, session, cancellationToken: cancellationToken).ConfigureAwait(false))
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
    }
    
    /// <summary>
    /// Create a new session for fresh conversations.
    /// </summary>
    public async Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default)
        => await _agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
}
