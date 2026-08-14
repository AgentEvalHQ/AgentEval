// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Formats LongMemEval conversation history for injection into an agent.
/// Preserves session boundaries and timestamps matching the official benchmark format.
/// </summary>
public static class LongMemEvalHistoryFormatter
{
    /// <summary>
    /// The assistant turn synthesised to answer a session-boundary marker. Emitted only when
    /// <see cref="ExternalBenchmarkOptions.PreserveSessionBoundaries"/> is set.
    /// </summary>
    /// <remarks>
    /// Exposed as a constant because this text is not from the dataset — it is scaffolding AgentEval
    /// adds — and a memory system that retrieves it will surface it as though it were conversation.
    /// Matching against this constant beats pattern-matching a string copied out of a log.
    /// </remarks>
    public const string SessionBoundaryAcknowledgement = "Understood. Starting a new conversation session.";

    /// <summary>
    /// The assistant turn synthesised for a user turn that has no assistant reply in the dataset.
    /// </summary>
    /// <remarks>
    /// Emitted regardless of <see cref="ExternalBenchmarkOptions.PreserveSessionBoundaries"/>: that
    /// option governs session boundaries, and this filler is not one. Set
    /// <see cref="ExternalBenchmarkOptions.SyntheticTurnMarker"/> to make it identifiable.
    /// </remarks>
    public const string UnpairedUserAcknowledgement = "I understand.";

    /// <summary>The prefix of every synthesised session-boundary user turn.</summary>
    public const string SessionMarkerPrefix = "--- Session ";

    /// <summary>
    /// Formats a LongMemEval entry's haystack sessions as injectable conversation turns.
    /// Inserts session boundary markers and timestamps to preserve structural context.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of turn here are synthesised rather than drawn from the dataset: the
    /// session-boundary pair (suppressed entirely by setting
    /// <see cref="ExternalBenchmarkOptions.PreserveSessionBoundaries"/> to false) and the
    /// <see cref="UnpairedUserAcknowledgement"/> filler. Both are indistinguishable from real
    /// content once a memory system has ingested and retrieved them, which makes a retrieval set
    /// full of scaffolding look like a defect in the system under test. Setting
    /// <see cref="ExternalBenchmarkOptions.SyntheticTurnMarker"/> prefixes every synthesised turn so
    /// they can be excluded by exact prefix instead of by guesswork.
    /// </para>
    /// </remarks>
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
                turns.Add((
                    Mark(marker, options.SyntheticTurnMarker),
                    Mark(SessionBoundaryAcknowledgement, options.SyntheticTurnMarker)));
            }

            // Add turns within this session, stripping has_answer metadata
            EmitSessionTurns(
                session,
                options.SyntheticTurnMarker,
                (user, assistant) => turns.Add((user, assistant)));
        }

        return turns;
    }

    /// <summary>
    /// Formats an entry's haystack as turns that carry real timestamps, for
    /// <see cref="AgentEval.Core.ITimestampedHistoryInjectableAgent"/>.
    /// </summary>
    /// <param name="entry">The entry to format. Every session date must be parseable.</param>
    /// <param name="options">
    /// Options; <see cref="ExternalBenchmarkOptions.TemporalGrounding"/> decides whether the
    /// session-boundary marker still carries its date as text.
    /// </param>
    /// <returns>The timestamped turns together with the instant the question is asked.</returns>
    /// <exception cref="LongMemEvalTemporalGroundingException">
    /// When a session date or the question date cannot be parsed. Nothing is substituted: an
    /// invented timestamp is indistinguishable from a real one to everything downstream, which is
    /// precisely the confusion time-grounding exists to remove.
    /// </exception>
    /// <remarks>
    /// Under <see cref="TemporalGroundingMode.TimestampsOnly"/> the session-boundary marker drops its
    /// date, so the only remaining source of dates is
    /// <see cref="AgentEval.Core.TimestampedConversationTurn.Timestamp"/>. The structural marker
    /// itself stays, because session structure is not a date crutch.
    /// </remarks>
    public static TimestampedConversationHistory FormatTimestamped(
        LongMemEvalEntry entry,
        ExternalBenchmarkOptions options)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);

        var queryTime = LongMemEvalTimestamps.Require(
            entry.QuestionDate, entry.QuestionId, "question_date");
        var keepDateInText =
            options.TemporalGrounding == TemporalGroundingMode.TimestampsAndText &&
            options.IncludeTimestamps;
        var turns = new List<TimestampedConversationTurn>();
        var sessions = entry.HaystackSessions ?? [];

        for (var sessionIdx = 0; sessionIdx < sessions.Count; sessionIdx++)
        {
            var sessionDate = entry.HaystackDates is not null && sessionIdx < entry.HaystackDates.Count
                ? entry.HaystackDates[sessionIdx]
                : null;
            var timestamp = LongMemEvalTimestamps.Require(
                sessionDate, entry.QuestionId, $"haystack_dates[{sessionIdx}]");

            void Add(string user, string assistant) =>
                turns.Add(new TimestampedConversationTurn(user, assistant, timestamp, sessionIdx));

            if (options.PreserveSessionBoundaries)
            {
                var marker = BuildSessionMarker(sessionIdx + 1, entry, sessionIdx, keepDateInText);
                Add(
                    Mark(marker, options.SyntheticTurnMarker),
                    Mark(SessionBoundaryAcknowledgement, options.SyntheticTurnMarker));
            }

            EmitSessionTurns(sessions[sessionIdx], options.SyntheticTurnMarker, Add);
        }

        return new TimestampedConversationHistory { Turns = turns, QueryTime = queryTime };
    }

    /// <summary>
    /// Walks one session, pairing each user turn with the assistant reply that answered it and
    /// synthesising <see cref="UnpairedUserAcknowledgement"/> for one the dataset leaves unanswered.
    /// </summary>
    /// <remarks>
    /// Shared by both injection formats deliberately: the pairing rule decides what a memory system
    /// ingests, and a fix applied to only one format would make the two arms disagree about the same
    /// corpus.
    /// </remarks>
    private static void EmitSessionTurns(
        IReadOnlyList<LongMemEvalTurn> session,
        string? syntheticMarker,
        Action<string, string> emit)
    {
        string? pendingUser = null;
        foreach (var turn in session)
        {
            if (turn.Role == "user")
            {
                if (pendingUser != null)
                    emit(pendingUser, Mark(UnpairedUserAcknowledgement, syntheticMarker));
                pendingUser = turn.Content;
            }
            else if (turn.Role == "assistant" && pendingUser != null)
            {
                emit(pendingUser, turn.Content);
                pendingUser = null;
            }
        }

        if (pendingUser != null)
            emit(pendingUser, Mark(UnpairedUserAcknowledgement, syntheticMarker));
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

    /// <summary>
    /// Prepends the caller's marker verbatim — no separator is inserted, so a caller who wants one
    /// includes it in the marker and an exact prefix match stays exact.
    /// </summary>
    private static string Mark(string text, string? marker)
        => string.IsNullOrEmpty(marker) ? text : marker + text;

    private static string BuildSessionMarker(
        int sessionNumber, LongMemEvalEntry entry, int sessionIdx, ExternalBenchmarkOptions options)
        => BuildSessionMarker(sessionNumber, entry, sessionIdx, options.IncludeTimestamps);

    private static string BuildSessionMarker(
        int sessionNumber, LongMemEvalEntry entry, int sessionIdx, bool includeDate)
    {
        var marker = $"{SessionMarkerPrefix}{sessionNumber}";

        if (includeDate && entry.HaystackDates != null && sessionIdx < entry.HaystackDates.Count)
            marker += $" ({entry.HaystackDates[sessionIdx]})";

        marker += " ---";
        return marker;
    }
}
