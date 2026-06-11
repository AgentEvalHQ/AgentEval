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
        
        // Duplicate-/null-tolerant pairing: a CallId may legitimately repeat across turns (multi-turn tool loops)
        // or be reused/omitted by some providers. Last-wins instead of ToDictionary (which throws on a duplicate
        // key); a null/empty CallId can't be paired, so it stays unpaired (WasExecuted = false — the safe, honest
        // direction: under-claim execution rather than over-claim it).
        var functionResults = new Dictionary<string, FunctionResultContent>(StringComparer.Ordinal);
        foreach (var r in allContents.OfType<FunctionResultContent>())
        {
            if (!string.IsNullOrEmpty(r.CallId))
                functionResults[r.CallId] = r;
        }
        
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
            
            if (!string.IsNullOrEmpty(call.CallId) && functionResults.TryGetValue(call.CallId, out var result))
            {
                record.Result = result.Result;
                record.Exception = result.Exception;
                record.WasExecuted = true;   // a paired result was observed ⇒ the tool actually ran (even if Result is null)
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
