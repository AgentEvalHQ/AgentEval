// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.TestHelpers;

/// <summary>
/// Mock testable agent for benchmark testing. Returns a fixed response
/// with optional simulated tool calls.
/// </summary>
internal class MockTestableAgent : IEvaluableAgent
{
    private readonly string _responseText;
    private readonly (string Name, Dictionary<string, object?> Args)[] _toolCalls;
    
    public string Name { get; }

    /// <summary>The usage each response reports; null (the default) reports none, so a cost cannot be measured.</summary>
    public TokenUsage? TokenUsage { get; init; }
    
    public MockTestableAgent(
        string name, 
        string responseText, 
        params (string Name, Dictionary<string, object?> Args)[] toolCalls)
    {
        Name = name;
        _responseText = responseText;
        _toolCalls = toolCalls;
    }
    
    public Task<AgentResponse> InvokeAsync(string input, CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();
        
        // Add tool call messages if any
        foreach (var (toolName, args) in _toolCalls)
        {
            var toolCallMsg = new ChatMessage(
                ChatRole.Assistant,
                $"Calling {toolName}");
            
            // Add function call content
            var funcCall = new FunctionCallContent(
                callId: Guid.NewGuid().ToString(),
                name: toolName,
                arguments: args);
            toolCallMsg.Contents.Add(funcCall);
            messages.Add(toolCallMsg);
            
            // Add function result
            var funcResult = new FunctionResultContent(funcCall.CallId, "Result");
            var resultMsg = new ChatMessage(
                ChatRole.Tool,
                new AIContent[] { funcResult });
            messages.Add(resultMsg);
        }
        
        // Add final response
        messages.Add(new ChatMessage(ChatRole.Assistant, _responseText));
        
        return Task.FromResult(new AgentResponse
        {
            Text = _responseText,
            RawMessages = messages,
            TokenUsage = TokenUsage
        });
    }
}
