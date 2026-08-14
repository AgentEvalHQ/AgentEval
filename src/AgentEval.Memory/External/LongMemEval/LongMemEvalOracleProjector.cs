// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Produces an evaluator-owned oracle view of a question: only the sessions the projection selects,
/// with the gold labels used for selection and then stripped from what the model sees.
/// </summary>
/// <remarks>
/// <para>
/// Public because the oracle arm is a property of the dataset rather than of any memory system —
/// no store, no retrieval — and it is the ceiling every other arm is measured against. A ceiling
/// each caller re-implements is a different number per caller, which is the one thing a ceiling
/// must not be.
/// </para>
/// <para>
/// Two labels drive selection and neither survives into the projection: <c>answer_session_ids</c>
/// chooses the evidence sessions, and each turn's <c>has_answer</c> flag is cleared. Session ids are
/// dropped as well, so a projected entry cannot be re-scored against the labels that built it.
/// </para>
/// </remarks>
public static class LongMemEvalOracleProjector
{
    /// <summary>A projected entry together with what the projection realised for that question.</summary>
    /// <param name="Entry">The projected, label-stripped entry the oracle reader answers from.</param>
    /// <param name="Realised">Sessions available and sessions kept or added, for this question.</param>
    public readonly record struct Projection(
        LongMemEvalEntry Entry,
        OracleQuestionProjection Realised);

    /// <summary>
    /// Projects the gold-only ceiling: every labelled evidence session, no distractors.
    /// </summary>
    /// <param name="source">The dataset entry to project. Required.</param>
    /// <returns>The projected entry and its realised session counts.</returns>
    public static Projection Project(LongMemEvalEntry source)
        => Project(source, LongMemEvalOracleOptions.GoldOnly, randomSeed: null);

    /// <summary>
    /// Projects an entry under the requested oracle controls.
    /// </summary>
    /// <param name="source">The dataset entry to project. Required.</param>
    /// <param name="options">
    /// Evidence fraction and distractor count. Null uses <see cref="LongMemEvalOracleOptions.GoldOnly"/>.
    /// </param>
    /// <param name="randomSeed">
    /// Seed for the per-question draw. Null draws non-deterministically; a value makes the selection
    /// reproducible and is derived per question id, so adding or removing questions does not shift
    /// which sessions another question kept.
    /// </param>
    /// <returns>The projected entry and its realised session counts.</returns>
    /// <remarks>
    /// Selected sessions are emitted in their original haystack order. Appending distractors after
    /// the evidence would put every gold session first in every question, and a model that answers
    /// well under that layout has been measured on position rather than on retrieval.
    /// </remarks>
    public static Projection Project(
        LongMemEvalEntry source,
        LongMemEvalOracleOptions? options,
        int? randomSeed)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= LongMemEvalOracleOptions.GoldOnly;
        options.Validate();

        var sourceSessions = source.HaystackSessions ?? [];
        var sourceSessionIds = source.HaystackSessionIds ?? [];
        var answerSessionIds = new HashSet<string>(
            source.AnswerSessionIds ?? [],
            StringComparer.Ordinal);

        // Only sessions whose id is known can be classified as evidence or not, so alignment is the
        // boundary of what the projection can honestly reason about.
        var alignedCount = Math.Min(sourceSessions.Count, sourceSessionIds.Count);
        var goldIndices = new List<int>();
        var distractorIndices = new List<int>();
        for (var index = 0; index < alignedCount; index++)
        {
            if (answerSessionIds.Contains(sourceSessionIds[index]))
                goldIndices.Add(index);
            else
                distractorIndices.Add(index);
        }

        // Two independent generators, so lowering the evidence fraction does not also change which
        // distractors were drawn: comparing half the evidence against all of it at one distractor
        // level has to hold the distractors fixed, or the comparison confounds the two controls.
        // Deliberately not cryptographic — reproducibility under a seed is the whole requirement and
        // a CSPRNG cannot replay a draw. Mirrors LongMemEvalDataLoader.
        var goldRng = Generator(randomSeed, source.QuestionId, "gold");
        var distractorRng = Generator(randomSeed, source.QuestionId, "distractor");

        var keptGold = SelectSubset(goldIndices, GoldSessionsToKeep(goldIndices.Count, options), goldRng);
        var addedDistractors = SelectSubset(
            distractorIndices,
            Math.Min(options.DistractorSessions, distractorIndices.Count),
            distractorRng);

        var selected = keptGold.Concat(addedDistractors).ToList();
        selected.Sort();

        var projectedSessions = new List<List<LongMemEvalTurn>>(selected.Count);
        var projectedDates = new List<string>(selected.Count);
        foreach (var index in selected)
        {
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

        var entry = new LongMemEvalEntry
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

        return new Projection(
            entry,
            new OracleQuestionProjection
            {
                QuestionId = source.QuestionId,
                GoldSessionsAvailable = goldIndices.Count,
                GoldSessionsKept = keptGold.Count,
                DistractorSessionsAvailable = distractorIndices.Count,
                DistractorSessionsAdded = addedDistractors.Count
            });
    }

    /// <summary>
    /// Rounds up and never below one for a question that has evidence at all. Rounding down would
    /// silently produce questions no system could answer, and score them anyway.
    /// </summary>
    internal static int GoldSessionsToKeep(int available, LongMemEvalOracleOptions options)
    {
        if (available <= 0)
            return 0;

        var keep = (int)Math.Ceiling(available * options.GoldSessionFraction);
        return Math.Clamp(keep, 1, available);
    }

    /// <summary>
    /// Derives a per-question seed so a question's draw depends on its own id, not on its position
    /// in the sample. Without that, adding one question re-rolls every later question's sessions and
    /// two runs that shared 49 of 50 questions would not share 49 of 50 projections.
    /// </summary>
    internal static Random Generator(int? seed, string questionId, string purpose)
        => seed.HasValue
            ? new Random(DeriveSeed(seed.Value, $"{questionId}|{purpose}")) // DevSkim: ignore DS148264
            : Random.Shared;

    internal static int DeriveSeed(int seed, string questionId)
    {
        var payload = Encoding.UTF8.GetBytes($"{seed} {questionId}");
        var hash = SHA256.HashData(payload);
        // Masked to a non-negative int: Random's constructor treats int.MinValue specially and a
        // stable positive value keeps the derivation obvious in a repro.
        return BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
    }

    /// <summary>
    /// Takes <paramref name="count"/> indices via a partial Fisher-Yates shuffle, leaving the
    /// caller to restore dataset order.
    /// </summary>
    private static List<int> SelectSubset(List<int> indices, int count, Random rng)
    {
        var limit = Math.Clamp(count, 0, indices.Count);
        var pool = new List<int>(indices);
        for (var i = 0; i < limit; i++)
        {
            var j = rng.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool.Take(limit).ToList();
    }
}
