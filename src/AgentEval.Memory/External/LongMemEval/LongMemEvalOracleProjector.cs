// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Produces an evaluator-owned oracle view containing only labelled evidence sessions.
/// Gold labels are used for selection and then stripped from the projected history.
/// </summary>
internal static class LongMemEvalOracleProjector
{
    internal static LongMemEvalEntry Project(LongMemEvalEntry source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var sourceSessions = source.HaystackSessions ?? [];
        var sourceSessionIds = source.HaystackSessionIds ?? [];
        var answerSessionIds = new HashSet<string>(
            source.AnswerSessionIds ?? [],
            StringComparer.Ordinal);
        var projectedSessions = new List<List<LongMemEvalTurn>>();
        var projectedDates = new List<string>();

        var alignedCount = Math.Min(sourceSessions.Count, sourceSessionIds.Count);
        for (var index = 0; index < alignedCount; index++)
        {
            if (!answerSessionIds.Contains(sourceSessionIds[index]))
                continue;

            projectedSessions.Add(sourceSessions[index]
                .Select(turn => new LongMemEvalTurn
                {
                    Role = turn.Role,
                    Content = turn.Content,
                    HasAnswer = null
                })
                .ToList());
            projectedDates.Add(
                source.HaystackDates is not null && index < source.HaystackDates.Count
                    ? source.HaystackDates[index]
                    : string.Empty);
        }

        return new LongMemEvalEntry
        {
            QuestionId = source.QuestionId,
            QuestionType = source.QuestionType,
            Question = source.Question,
            AnswerRaw = source.AnswerRaw.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? default
                : source.AnswerRaw.Clone(),
            QuestionDate = source.QuestionDate,
            HaystackSessions = projectedSessions,
            HaystackDates = projectedDates,
            HaystackSessionIds = [],
            AnswerSessionIds = []
        };
    }
}
