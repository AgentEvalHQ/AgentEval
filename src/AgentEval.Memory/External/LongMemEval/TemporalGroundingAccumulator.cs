// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Counts what time-grounding actually delivered, question by question, as the run proceeds.
/// </summary>
/// <remarks>
/// Counted from the histories that were injected rather than from the options that requested them,
/// for the same reason the sample composition is counted from the questions that ran: a report
/// derived from the request can describe a corpus the run never built.
/// </remarks>
internal sealed class TemporalGroundingAccumulator(TemporalGroundingMode mode)
{
    private int _questions;
    private int _sessions;
    private int _turns;
    private int _datedSessions;
    private DateTimeOffset? _earliest;
    private DateTimeOffset? _latest;

    internal void Observe(LongMemEvalEntry entry, TimestampedConversationHistory history)
    {
        _questions++;
        _turns += history.Turns.Count;

        var sessions = entry.HaystackSessions ?? [];
        _sessions += sessions.Count;
        foreach (var session in sessions)
        {
            if (session.Any(turn => LongMemEvalTimestamps.LooksDated(turn.Content)))
                _datedSessions++;
        }

        foreach (var turn in history.Turns)
        {
            if (_earliest is null || turn.Timestamp < _earliest)
                _earliest = turn.Timestamp;
            if (_latest is null || turn.Timestamp > _latest)
                _latest = turn.Timestamp;
        }
    }

    internal TemporalGroundingReport Build() => new()
    {
        Mode = mode,
        Questions = _questions,
        SessionsTimestamped = _sessions,
        TurnsTimestamped = _turns,
        InTextDatesRemoved = mode == TemporalGroundingMode.TimestampsOnly,
        EarliestSessionTimestamp = _earliest,
        LatestSessionTimestamp = _latest,
        SessionsWithDateLikeContent = _datedSessions
    };
}
