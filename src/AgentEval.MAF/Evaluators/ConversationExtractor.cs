// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentEval.Core;
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
    /// Extracts the evaluated turn's tool usage from conversation messages and response.
    /// Captures tool names, arguments, results and whether each call ran — but NOT timing (light path limitation).
    /// </summary>
    /// <param name="messages">The conversation messages to inspect.</param>
    /// <param name="response">The agent response containing any additional messages.</param>
    /// <param name="logger">Optional logger; warns when approval-gated tool calls were seen that cannot be recorded. When null, diagnostics are silently dropped.</param>
    /// <returns>
    /// The turn's tool calls, or <see langword="null"/> when they are unknown: <paramref name="messages"/> is null, or the
    /// turn shows no tool call. A transcript with no call in it cannot tell "the agent called nothing" from "the caller
    /// passed only the query half" (MAF's own MEAI adapter does), so it is not read as a measured zero. A caller that
    /// knows the conversation is complete uses
    /// <see cref="ExtractToolUsage(IEnumerable{ChatMessage}, ChatResponse, bool, ILogger)"/>.
    /// </returns>
    public static ToolUsageReport? ExtractToolUsage(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        ILogger? logger = null)
        => ExtractToolUsage(messages, response, conversationComplete: false, logger);

    /// <summary>
    /// Extracts the evaluated turn's tool usage from conversation messages and response, through the canonical
    /// <see cref="ToolUsageExtractor"/>.
    /// </summary>
    /// <param name="messages">The conversation messages to inspect.</param>
    /// <param name="response">The agent response containing any additional messages.</param>
    /// <param name="conversationComplete">
    /// <see langword="true"/> when the caller vouches that <paramref name="messages"/> and <paramref name="response"/>
    /// carry the evaluated turn in full (every assistant and tool message, not only the query half). A turn with no tool
    /// call is then an empty report, a recorded "none called"; otherwise it is <see langword="null"/>.
    /// </param>
    /// <param name="logger">Optional logger; warns when approval-gated tool calls were seen that cannot be recorded. When null, diagnostics are silently dropped.</param>
    /// <returns>The turn's tool calls, or <see langword="null"/> when they are unknown.</returns>
    /// <remarks>
    /// <para>
    /// The turn is the messages after the last user prompt, then the response's own messages: a MAF per-turn slice
    /// (<c>EvalItem.PerTurnItems</c>) carries every earlier turn, and their calls are not this turn's.
    /// </para>
    /// <para>
    /// This used to be a parallel copy of the extractor (T10). It never marked a call executed, so every call read as
    /// emitted only; a reused CallId overwrote the earlier unanswered call; a stray result became an invented
    /// <c>unknown_{callId}</c> record; an approval-gated call was invisible; calls were listed in result-arrival order;
    /// and zero calls came back as "not recorded". The canonical extractor pairs each result with the nearest preceding
    /// unpaired call of the same CallId and marks it executed, never invents a record, and keeps message order. It runs
    /// approval-aware: the light path has no opt-in setting, and a gated call the agent made is a call, recorded as not
    /// run (and a rejected one stays not run).
    /// </para>
    /// </remarks>
    public static ToolUsageReport? ExtractToolUsage(
        IEnumerable<ChatMessage> messages,
        ChatResponse response,
        bool conversationComplete,
        ILogger? logger = null)
    {
        if (messages is null) return null;

        var turn = TurnMessages(messages, response);
        var report = ToolUsageExtractor.Extract(turn.Cast<object>().ToList(), includeApprovalGatedCalls: true);

        if (report.DroppedApprovalRequestCount > 0)
        {
            logger?.LogWarning(
                "[AgentEval] Tool usage is INCOMPLETE: {DroppedCount} approval-gated tool call(s) that are not function " +
                "calls (for example hosted MCP tools) were seen and not recorded.",
                report.DroppedApprovalRequestCount);
        }

        // No call in the turn is a recorded "none" only when the caller vouches for the whole turn and nothing gated was
        // dropped; otherwise it is unknown, never a measured zero.
        if (report.Count == 0 && (!conversationComplete || report.DroppedApprovalRequestCount > 0))
            return null;

        return report;
    }

    /// <summary>
    /// The messages of the evaluated turn: those after the last user prompt in <paramref name="messages"/>, then the
    /// response's messages that are not already among them. A user message holding only approval responses continues
    /// the turn, it does not start one.
    /// </summary>
    private static List<ChatMessage> TurnMessages(IEnumerable<ChatMessage> messages, ChatResponse? response)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();

        var start = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (IsUserPrompt(list[i]))
            {
                start = i + 1;
                break;
            }
        }

        var turn = list.Skip(start).ToList();

        // MAF builds an item's conversation from the run's messages and its RawResponse from the last of them, so one
        // message can arrive on both sides. It is read once (by reference, as MAF's own BuildEvalItem de-duplicates).
        if (response?.Messages is { } responseMessages)
        {
            foreach (var message in responseMessages)
            {
                if (!list.Any(m => ReferenceEquals(m, message)))
                    turn.Add(message);
            }
        }

        return turn;
    }

    private static bool IsUserPrompt(ChatMessage message) =>
        message.Role == ChatRole.User
        && (message.Contents.Count == 0 || message.Contents.Any(c => c is not ToolApprovalResponseContent));
}
