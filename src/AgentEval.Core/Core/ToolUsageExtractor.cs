// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Models;

namespace AgentEval.Core;

/// <summary>
/// Utility class for extracting tool usage information from agent responses.
/// </summary>
public static class ToolUsageExtractor
{
    /// <summary>
    /// Extract tool usage report from raw chat messages.
    /// </summary>
    /// <param name="rawMessages">The raw messages from an agent response.</param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    public static ToolUsageReport Extract(IReadOnlyList<object>? rawMessages)
    {
        var report = new ToolUsageReport();
        
        if (rawMessages == null || rawMessages.Count == 0)
            return report;
        
        var allContents = rawMessages
            .OfType<ChatMessage>()
            .SelectMany(m => m.Contents)
            .ToList();
        
        var functionCalls = allContents
            .OfType<FunctionCallContent>()
            .ToList();
        
        var functionResults = allContents
            .OfType<FunctionResultContent>()
            .ToDictionary(r => r.CallId, r => r);
        
        int order = 0;
        foreach (var call in functionCalls)
        {
            order++;
            var record = new ToolCallRecord
            {
                Name = call.Name,
                CallId = call.CallId,
                Arguments = call.Arguments,
                Order = order
            };
            
            if (functionResults.TryGetValue(call.CallId, out var result))
            {
                record.Result = result.Result;
                record.Exception = result.Exception;
            }
            
            report.AddCall(record);
        }
        
        return report;
    }
    
    /// <summary>
    /// Extract tool usage report from an agent response.
    /// </summary>
    /// <param name="response">The agent response.</param>
    /// <returns>A tool usage report containing all tool calls.</returns>
    public static ToolUsageReport Extract(AgentResponse response)
    {
        _ = response ?? throw new ArgumentNullException(nameof(response));
        return Extract(response.RawMessages);
    }
}
