// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Comparison;

namespace AgentEval.MAF;

/// <summary>
/// Adapts a Microsoft Agent Framework (MAF) AIAgent for testing with AgentEval,
/// with support for model identification for comparison scenarios.
/// </summary>
public class MAFIdentifiableAgentAdapter : IStreamableAgent, IModelIdentifiable
{
    private readonly AIAgent _agent;
    private AgentThread? _thread;
    
    /// <summary>
    /// Create an adapter for an AIAgent with model identification.
    /// </summary>
    /// <param name="agent">The MAF agent to adapt.</param>
    /// <param name="modelId">Unique identifier for the model (e.g., "gpt-4o-2024-08-06").</param>
    /// <param name="modelDisplayName">Human-readable model name (e.g., "GPT-4o").</param>
    /// <param name="thread">Optional thread for conversation context.</param>
    public MAFIdentifiableAgentAdapter(
        AIAgent agent, 
        string modelId,
        string modelDisplayName,
        AgentThread? thread = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        ModelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        ModelDisplayName = modelDisplayName ?? throw new ArgumentNullException(nameof(modelDisplayName));
        _thread = thread;
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
        var thread = _thread ?? _agent.GetNewThread();
        var response = await _agent.RunAsync(prompt, thread, cancellationToken: cancellationToken);
        
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
        var thread = _thread ?? _agent.GetNewThread();
        
        await foreach (var update in _agent.RunStreamingAsync(prompt, thread, cancellationToken: cancellationToken))
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
                }
            }
        }
        
        yield return new AgentResponseChunk { IsComplete = true };
    }
    
    /// <summary>
    /// Reset the conversation thread.
    /// </summary>
    public void ResetThread()
    {
        _thread = _agent.GetNewThread();
    }
    
    /// <summary>
    /// Get a new thread for fresh conversations.
    /// </summary>
    public AgentThread GetNewThread() => _agent.GetNewThread();
}
