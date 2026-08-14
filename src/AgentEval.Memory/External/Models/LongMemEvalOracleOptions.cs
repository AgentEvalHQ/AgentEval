// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Controls what the oracle arm puts in front of the answer model: how much of the labelled
/// evidence it keeps, and how many non-evidence sessions from the same haystack it adds.
/// </summary>
/// <remarks>
/// <para>
/// These live on their own options object rather than on <see cref="ExternalBenchmarkOptions"/>
/// because they mean nothing outside the oracle arm. An option that is silently ignored by the run
/// you actually launched is how a configuration comes to describe a measurement that never happened.
/// </para>
/// <para>
/// Defaults reproduce the gold-only projection exactly: every labelled evidence session, no
/// distractors. That is the ceiling the other arms are measured against, so it stays the default.
/// </para>
/// </remarks>
public sealed class LongMemEvalOracleOptions
{
    /// <summary>The gold-only ceiling: all evidence sessions, no distractors.</summary>
    public static LongMemEvalOracleOptions GoldOnly { get; } = new();

    /// <summary>
    /// Non-evidence sessions to add from the question's <i>own</i> haystack. Default: 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Drawn from the same question's haystack on purpose. Sessions borrowed from another question
    /// are about another user's life and are trivially ignorable, so a run padded with them measures
    /// a strawman rather than the model's ability to find evidence among plausible neighbours.
    /// </para>
    /// <para>
    /// Selection is reproducible under <see cref="ExternalBenchmarkOptions.RandomSeed"/>, and the
    /// realised count is reported per question — a haystack holding fewer non-evidence sessions than
    /// requested yields fewer, and a level that added nothing must not be mistaken for a level that
    /// added something harmless.
    /// </para>
    /// </remarks>
    public int DistractorSessions { get; init; }

    /// <summary>
    /// Fraction of the labelled evidence sessions to keep, in (0, 1]. Default: 1.0 (all of them).
    /// </summary>
    /// <remarks>
    /// Rounded up, never down, and never below one session for a question that has any: rounding a
    /// one-evidence-session question to zero makes it unanswerable by construction, which measures
    /// the arithmetic rather than the model. The realised kept-of-total counts are reported per
    /// question, because a fraction that degraded nothing and a fraction whose degradation did not
    /// matter are different findings that look identical in the score alone.
    /// </remarks>
    public double GoldSessionFraction { get; init; } = 1.0;

    /// <summary>Upper bound on <see cref="DistractorSessions"/>; larger asks are a configuration error.</summary>
    public const int MaximumDistractorSessions = 10_000;

    /// <summary>Validates the controls before any dataset is projected.</summary>
    public void Validate()
    {
        if (DistractorSessions is < 0 or > MaximumDistractorSessions)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DistractorSessions), DistractorSessions,
                $"DistractorSessions must be between 0 and {MaximumDistractorSessions}.");
        }
        if (!double.IsFinite(GoldSessionFraction) || GoldSessionFraction is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(GoldSessionFraction), GoldSessionFraction,
                "GoldSessionFraction must be greater than 0 and at most 1. A fraction of 0 would " +
                "keep no evidence at all, making every question unanswerable by construction.");
        }
    }
}

/// <summary>
/// What the oracle projection actually did to one question's sessions.
/// </summary>
public sealed class OracleQuestionProjection
{
    /// <summary>Question this projection belongs to.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Labelled evidence sessions the dataset has for this question.</summary>
    public required int GoldSessionsAvailable { get; init; }

    /// <summary>Labelled evidence sessions kept in the projection.</summary>
    public required int GoldSessionsKept { get; init; }

    /// <summary>Non-evidence sessions this question's haystack could have contributed.</summary>
    public required int DistractorSessionsAvailable { get; init; }

    /// <summary>Non-evidence sessions actually added.</summary>
    public required int DistractorSessionsAdded { get; init; }

    /// <summary>Sessions in the projected history: kept evidence plus added distractors.</summary>
    public int ProjectedSessions => GoldSessionsKept + DistractorSessionsAdded;
}

/// <summary>
/// Run-level rollup of the oracle projection, so a run states the corpus it actually built rather
/// than the one it was configured to build.
/// </summary>
public sealed class OracleProjectionReport
{
    /// <summary>Distractor sessions requested per question.</summary>
    public required int RequestedDistractorSessions { get; init; }

    /// <summary>Evidence fraction requested.</summary>
    public required double RequestedGoldSessionFraction { get; init; }

    /// <summary>Questions projected.</summary>
    public required int Questions { get; init; }

    /// <summary>Labelled evidence sessions available across all projected questions.</summary>
    public required int GoldSessionsAvailable { get; init; }

    /// <summary>Labelled evidence sessions kept across all projected questions.</summary>
    public required int GoldSessionsKept { get; init; }

    /// <summary>Distractor sessions the questions' haystacks could have contributed.</summary>
    public required int DistractorSessionsAvailable { get; init; }

    /// <summary>Distractor sessions actually added across all projected questions.</summary>
    public required int DistractorSessionsAdded { get; init; }

    /// <summary>Per-question realised counts, in execution order.</summary>
    public required IReadOnlyList<OracleQuestionProjection> ByQuestion { get; init; }

    /// <summary>
    /// Evidence actually kept as a fraction of evidence available, or null when no question had any
    /// labelled evidence — an empty denominator has no fraction, and 1.0 would claim one.
    /// </summary>
    public double? RealisedGoldSessionFraction => GoldSessionsAvailable == 0
        ? null
        : (double)GoldSessionsKept / GoldSessionsAvailable;

    /// <summary>
    /// True when every question received the requested number of distractors. False means at least
    /// one haystack ran out, and the run measured a smaller context than it asked for.
    /// </summary>
    public bool DistractorRequestFullyMet =>
        DistractorSessionsAdded == RequestedDistractorSessions * Questions;

    internal static OracleProjectionReport From(
        LongMemEvalOracleOptions options,
        IReadOnlyList<OracleQuestionProjection> projections) => new()
        {
            RequestedDistractorSessions = options.DistractorSessions,
            RequestedGoldSessionFraction = options.GoldSessionFraction,
            Questions = projections.Count,
            GoldSessionsAvailable = projections.Sum(p => p.GoldSessionsAvailable),
            GoldSessionsKept = projections.Sum(p => p.GoldSessionsKept),
            DistractorSessionsAvailable = projections.Sum(p => p.DistractorSessionsAvailable),
            DistractorSessionsAdded = projections.Sum(p => p.DistractorSessionsAdded),
            ByQuestion = projections
        };
}
