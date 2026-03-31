// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Formats LongMemEval conversation history for injection into an agent.
/// Preserves session boundaries and timestamps matching the official benchmark format.
/// </summary>
public static class LongMemEvalHistoryFormatter
{
    /// <summary>
    /// Formats a LongMemEval entry's haystack sessions as injectable conversation turns.
    /// Inserts session boundary markers and timestamps to preserve structural context.
    /// </summary>
    /// <param name="entry">The LongMemEval entry with haystack sessions.</param>
    /// <param name="options">Options controlling boundary and timestamp inclusion.</param>
    /// <returns>Conversation turns suitable for IHistoryInjectableAgent.InjectConversationHistory.</returns>
    public static IReadOnlyList<(string UserMessage, string AssistantResponse)> Format(
        LongMemEvalEntry entry,
        ExternalBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.HaystackSessions == null || entry.HaystackSessions.Count == 0)
            return [];

        var turns = new List<(string UserMessage, string AssistantResponse)>();

        for (int sessionIdx = 0; sessionIdx < entry.HaystackSessions.Count; sessionIdx++)
        {
            var session = entry.HaystackSessions[sessionIdx];

            // Insert session boundary marker
            if (options.PreserveSessionBoundaries)
            {
                var marker = BuildSessionMarker(sessionIdx + 1, entry, sessionIdx, options);
                turns.Add((marker, "Understood. Starting a new conversation session."));
            }

            // Add turns within this session, stripping has_answer metadata
            string? pendingUser = null;
            foreach (var turn in session)
            {
                if (turn.Role == "user")
                {
                    if (pendingUser != null)
                        turns.Add((pendingUser, "I understand."));
                    pendingUser = turn.Content;
                }
                else if (turn.Role == "assistant" && pendingUser != null)
                {
                    turns.Add((pendingUser, turn.Content));
                    pendingUser = null;
                }
            }

            if (pendingUser != null)
                turns.Add((pendingUser, "I understand."));
        }

        return turns;
    }

    /// <summary>
    /// Formats the haystack as a flat text blob matching the official LongMemEval prompt format.
    /// Used when the agent doesn't support IHistoryInjectableAgent and needs a single prompt.
    /// </summary>
    public static string FormatAsTextBlob(LongMemEvalEntry entry, ExternalBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.HaystackSessions == null || entry.HaystackSessions.Count == 0)
            return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("I will give you several history chats between you and a user. Please answer the question based on the relevant chat history.");
        sb.AppendLine();
        sb.AppendLine("History Chats:");

        for (int i = 0; i < entry.HaystackSessions.Count; i++)
        {
            sb.AppendLine();
            sb.Append($"### Session {i + 1}:");

            if (options.IncludeTimestamps && entry.HaystackDates != null && i < entry.HaystackDates.Count)
                sb.AppendLine($"\nSession Date: {entry.HaystackDates[i]}");
            else
                sb.AppendLine();

            sb.AppendLine("Session Content:");

            foreach (var turn in entry.HaystackSessions[i])
            {
                // Strip has_answer metadata — must not be visible to model
                sb.AppendLine($"{turn.Role}: {turn.Content}");
            }
        }

        if (!string.IsNullOrEmpty(entry.QuestionDate))
        {
            sb.AppendLine();
            sb.AppendLine($"Current Date: {entry.QuestionDate}");
        }

        return sb.ToString();
    }

    private static string BuildSessionMarker(
        int sessionNumber, LongMemEvalEntry entry, int sessionIdx, ExternalBenchmarkOptions options)
    {
        var marker = $"--- Session {sessionNumber}";

        if (options.IncludeTimestamps && entry.HaystackDates != null && sessionIdx < entry.HaystackDates.Count)
            marker += $" ({entry.HaystackDates[sessionIdx]})";

        marker += " ---";
        return marker;
    }
}
