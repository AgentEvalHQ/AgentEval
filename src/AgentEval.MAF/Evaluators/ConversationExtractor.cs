// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Models;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Extracts AgentEval-compatible data from MEAI <see cref="ChatMessage"/> conversation types.
/// Used by the light path adapters to convert MAF orchestration data into AgentEval's
/// <see cref="AgentEval.Core.EvaluationContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// The light path receives a conversation transcript from MAF's orchestration layer.
/// This class extracts input text (last-turn split) and tool usage from
/// <see cref="FunctionCallContent"/>/<see cref="FunctionResultContent"/> pairs.
/// </para>
/// <para>
/// <b>Limitation:</b> Tool call timing is NOT available through the light path.
/// For tool timing, use the deep path via <see cref="AgentEval.MAF.MAFEvaluationHarness"/>.
/// </para>
/// </remarks>
public static class ConversationExtractor
{
    /// <summary>
    /// Extracts the last user message text from a conversation (last-turn split).
    /// This matches ADR-0020's default split strategy.
    /// </summary>
    public static string ExtractLastUserMessage(IEnumerable<ChatMessage> messages)
    {
        if (messages == null)
            return "";

        var list = messages as IList<ChatMessage> ?? messages.ToList();
        if (list.Count == 0)
            return "";

        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.User)
                return list[i].Text ?? "";
        }

        return list[0].Text ?? "";
    }

    /// <summary>
    /// Extracts all user messages concatenated (full-conversation split).
    /// </summary>
    public static string ExtractAllUserMessages(IEnumerable<ChatMessage> messages)
    {
        if (messages == null || !messages.Any())
            return "";

        return string.Join("\n", messages
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.Text ?? "")
            .Where(t => !string.IsNullOrEmpty(t)));
    }

    /// <summary>
    /// Extracts tool usage from conversation messages and response.
    /// Captures tool names, arguments, and results — but NOT timing (light path limitation).
    /// </summary>
    public static ToolUsageReport? ExtractToolUsage(
        IEnumerable<ChatMessage> messages,
        ChatResponse response)
    {
        var report = new ToolUsageReport();
        var pendingCalls = new Dictionary<string, ToolCallRecord>();
        int order = 0;

        IEnumerable<ChatMessage> allMessages = messages;
        if (response.Messages != null)
            allMessages = allMessages.Concat(response.Messages);

        foreach (var message in allMessages)
        {
            if (message.Contents == null)
                continue;

            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call)
                {
                    var callId = call.CallId ?? Guid.NewGuid().ToString("N");
                    var record = new ToolCallRecord
                    {
                        Name = call.Name,
                        CallId = callId,
                        Arguments = call.Arguments,
                        Order = ++order,
                    };
                    pendingCalls[callId] = record;
                }
                else if (content is FunctionResultContent result)
                {
                    var callId = result.CallId ?? "";
                    if (pendingCalls.TryGetValue(callId, out var pending))
                    {
                        pending.Result = result.Result;
                        pending.Exception = result.Exception;
                        report.AddCall(pending);
                        pendingCalls.Remove(callId);
                    }
                    else
                    {
                        report.AddCall(new ToolCallRecord
                        {
                            Name = $"unknown_{callId}",
                            CallId = callId,
                            Result = result.Result,
                            Exception = result.Exception,
                            Order = ++order,
                        });
                    }
                }
            }
        }

        foreach (var pending in pendingCalls.Values.OrderBy(c => c.Order))
            report.AddCall(pending);

        return report.Count > 0 ? report : null;
    }
}
