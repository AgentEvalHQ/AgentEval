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

        // L8: pair results to calls in MESSAGE ORDER, one result per call, instead of a single global last-wins map.
        // A CallId may legitimately repeat across turns (multi-turn tool loops); the old flat map paired an
        // emitted-only call in an earlier turn with a later turn's result that REUSED the same id, falsely reading
        // WasExecuted = true (a Behavioral over-claim). We now pair each result to the NEAREST PRECEDING still-unpaired
        // call with the same id (a LIFO stack per id), and remove it so one result cannot satisfy two calls. A
        // null/empty CallId can't be paired, so it stays unpaired (WasExecuted = false — the safe under-claim direction).
        var records = new List<ToolCallRecord>();
        var unpairedByCallId = new Dictionary<string, Stack<ToolCallRecord>>(StringComparer.Ordinal);
        int order = 0;

        foreach (var message in rawMessages.OfType<ChatMessage>())
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    order++;
                    var record = new ToolCallRecord
                    {
                        Name = call.Name,
                        CallId = call.CallId,
                        Arguments = call.Arguments,
                        Order = order,
                    };
                    records.Add(record);
                    if (!string.IsNullOrEmpty(call.CallId))
                    {
                        if (!unpairedByCallId.TryGetValue(call.CallId, out var stack))
                            unpairedByCallId[call.CallId] = stack = new Stack<ToolCallRecord>();
                        stack.Push(record);
                    }
                }
                else if (content is FunctionResultContent result && !string.IsNullOrEmpty(result.CallId)
                         && unpairedByCallId.TryGetValue(result.CallId, out var stack) && stack.Count > 0)
                {
                    var record = stack.Pop();    // nearest preceding unpaired call with this id
                    record.Result = result.Result;
                    record.Exception = result.Exception;
                    record.WasExecuted = true;   // a paired result was observed ⇒ the tool actually ran (even if Result is null)
                }
                // A result with no matching unpaired call (precedes its call, or a stray) is ignored — we never
                // fabricate a tool call from a lone result.
            }
        }

        foreach (var record in records)
            report.AddCall(record);

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
