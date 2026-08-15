// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Serialization;

namespace AgentEval.Memory.External.Models;

/// <summary>
/// A typed outcome vector with its own denominator. Never a percentage.
/// </summary>
/// <remarks>
/// <see cref="N"/> travels with the counts everywhere it is reported, because most of the cells
/// this appears in are diagnostic strata of five to twenty questions (ADR-026 §5) and a rate
/// quoted without its denominator is exactly how a twelve-question stratum gets read as a finding.
/// </remarks>
public sealed class TypedMemEvalOutcomeCounts
{
    /// <summary>Questions in this cell.</summary>
    public required int N { get; init; }

    /// <summary>Answers matching gold.</summary>
    public int Correct { get; init; }

    /// <summary>Answers committing to an incorrect value.</summary>
    public int Wrong { get; init; }

    /// <summary>Answers declining to commit.</summary>
    public int Abstained { get; init; }

    /// <summary>Confident false negatives.</summary>
    public int Missed { get; init; }

    /// <summary>Early firings on a not-yet-true before-arm.</summary>
    public int Premature { get; init; }

    /// <summary>Questions the judge could not decide. Not a measurement.</summary>
    public int Inconclusive { get; init; }

    /// <summary>Questions skipped with notice. Not a measurement.</summary>
    public int Unrun { get; init; }

    /// <summary>
    /// Builds the counts for one cell. Kept as the only construction path so
    /// <see cref="N"/> is always the length of the list the counts came from and the two
    /// cannot drift.
    /// </summary>
    public static TypedMemEvalOutcomeCounts From(IEnumerable<TypedMemEvalOutcome> outcomes)
    {
        var all = outcomes as IReadOnlyList<TypedMemEvalOutcome> ?? outcomes.ToList();
        int Count(TypedMemEvalOutcome o) => all.Count(x => x == o);
        return new TypedMemEvalOutcomeCounts
        {
            N = all.Count,
            Correct = Count(TypedMemEvalOutcome.Correct),
            Wrong = Count(TypedMemEvalOutcome.Wrong),
            Abstained = Count(TypedMemEvalOutcome.Abstained),
            Missed = Count(TypedMemEvalOutcome.Missed),
            Premature = Count(TypedMemEvalOutcome.Premature),
            Inconclusive = Count(TypedMemEvalOutcome.Inconclusive),
            Unrun = Count(TypedMemEvalOutcome.Unrun)
        };
    }
}

/// <summary>
/// Pair-level consistency for the Prospective vertical, where a single question cannot express
/// the finding (ADR-026 §5.1).
/// </summary>
public sealed class TypedMemEvalPairConsistency
{
    /// <summary>Pairs with both arms present in this run.</summary>
    public required int Pairs { get; init; }

    /// <summary>Pairs where both arms were answered correctly — the only fully passing shape.</summary>
    public int BothArmsCorrect { get; init; }

    /// <summary>Pairs whose before-arm fired early.</summary>
    public int PrematureBefore { get; init; }

    /// <summary>Pairs whose after-arm denied a thing that had by then become true.</summary>
    public int MissedAfter { get; init; }

    /// <summary>
    /// Pairs whose two arms are consistent with one fixed answer given to both — the signature of
    /// a system that never received the query time, or ignored it and read a wall clock instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not "the same outcome on both arms". Outcomes are measured against gold, and gold flips
    /// between the arms, so a system that always says "nothing is due yet" scores
    /// <see cref="TypedMemEvalOutcome.Correct"/> on the before-arm and
    /// <see cref="TypedMemEvalOutcome.Missed"/> on the after-arm — two different labels for one
    /// unchanging answer. Counting identical labels would have missed exactly the system this
    /// count exists to find.
    /// </para>
    /// <para>
    /// The two patterns are: correct-then-missed (always "not yet") and premature-then-correct
    /// (always "it happened"). Both are <i>consistent with</i> time-blindness rather than proof of
    /// it — a system can produce one by chance on a single pair — which is why this is a count over
    /// pairs and not a verdict.
    /// </para>
    /// </remarks>
    public int TimeBlindPattern { get; init; }
}

/// <summary>Evidence-attribution totals, with the honesty fields that keep them readable.</summary>
public sealed class TypedMemEvalAttributionSummary
{
    /// <summary>Questions where every gold component was referenced.</summary>
    public int EvidencePresent { get; init; }

    /// <summary>Questions where at least one gold component was not referenced.</summary>
    public int EvidenceAbsent { get; init; }

    /// <summary>Questions with no evidence telemetry at all.</summary>
    public int Unobserved { get; init; }

    /// <summary>
    /// Share of questions for which attribution could be computed, 0–1. A run against an agent
    /// that supplies no evidence telemetry reports 0 here, which is the difference between
    /// "nothing was retrieved" and "we could not see what was retrieved".
    /// </summary>
    public required double ObservedShare { get; init; }

    /// <summary>The strongest observation level reached anywhere in the run.</summary>
    public TypedMemEvalAttributionLevel Level { get; init; }
}

/// <summary>Distribution summary for realised gold coverage across a run.</summary>
public sealed class TypedMemEvalCoverageSummary
{
    /// <summary>Questions for which coverage could be computed.</summary>
    public required int Observed { get; init; }

    /// <summary>
    /// Observed questions that actually have gold to retrieve.
    /// </summary>
    /// <remarks>
    /// A never-known probe has no gold, so its coverage is vacuously 1.0 — it cannot miss what was
    /// never there. Forgetting is 30% such questions, enough to lift a mean noticeably, so the
    /// denominator that excludes them travels beside the one that does not.
    /// </remarks>
    public int ObservedWithGold { get; init; }

    /// <summary>Mean realised coverage over observed questions; null when none were observed.</summary>
    /// <remarks>
    /// Includes the vacuous 1.0s. Read <see cref="MeanOverGoldBearing"/> for the measured figure.
    /// </remarks>
    public double? Mean { get; init; }

    /// <summary>
    /// Mean realised coverage over observed questions that have gold; null when there are none.
    /// This is the number that describes retrieval.
    /// </summary>
    public double? MeanOverGoldBearing { get; init; }

    /// <summary>Lowest realised coverage observed.</summary>
    public double? Minimum { get; init; }

    /// <summary>Highest realised coverage observed.</summary>
    public double? Maximum { get; init; }

    /// <summary>
    /// The corpus's own BM25 floor at <c>K_ref</c>, carried from corpus metadata so a run's
    /// realised coverage can be read against the number the corpus was calibrated to. A system
    /// below the lexical floor is retrieving worse than word matching.
    /// </summary>
    /// <remarks>
    /// Computed over the same gold-bearing questions as <see cref="MeanOverGoldBearing"/>, so the
    /// two are comparable. Comparing a run's measured coverage against a floor that included
    /// vacuous 1.0s would flatter every system by the share of no-gold questions in the corpus.
    /// </remarks>
    public double? CalibratedFloorMean { get; init; }
}

/// <summary>
/// What a TypedMemEval run measured. Attached to
/// <see cref="ExternalBenchmarkResult.TypedOutcomes"/>.
/// </summary>
/// <remarks>
/// <b>Citation rule.</b> Cite results as "TypedMemEval-&lt;Vertical&gt; v1 (AgentEval)".
/// TypedMemEval results are not LongMemEval results and must never be presented as, summed with,
/// or averaged with LongMemEval numbers. The twelve Prospective questions seeded from the
/// time-grounded probe exist in both corpora; a report that runs both must not double-count them.
/// The typed vector below is the citable form of this result —
/// <see cref="ExternalBenchmarkResult.OverallAccuracy"/> stays populated for tooling compatibility
/// and is not a TypedMemEval score.
/// </remarks>
public sealed class TypedMemEvalReport
{
    /// <summary>The vertical this run measured.</summary>
    public required string Vertical { get; init; }

    /// <summary>Corpus identifier, revision included, e.g. <c>agenteval-typedmemeval-prospective-v1</c>.</summary>
    public required string CorpusId { get; init; }

    /// <summary>Dataset revision, stamped into every result.</summary>
    public required string Revision { get; init; }

    /// <summary>Newline-normalized SHA-256 of the corpus text this run used.</summary>
    public required string CorpusSha256 { get; init; }

    /// <summary>The reference retrieval budget the corpus was calibrated against, in sessions.</summary>
    public required int ReferenceBudgetSessions { get; init; }

    /// <summary>Outcome counts across the whole run.</summary>
    public required TypedMemEvalOutcomeCounts Outcomes { get; init; }

    /// <summary>
    /// Outcome counts per diagnostic stratum. These are diagnostics, not claims: the ADR's
    /// n≥30 floor is a per-vertical floor, and most shapes here hold five to twenty questions.
    /// </summary>
    public required IReadOnlyDictionary<string, TypedMemEvalOutcomeCounts> ByShape { get; init; }

    /// <summary>
    /// WorkingMemory only: the outcome × distance curve, keyed by intervening session count.
    /// Reported as a curve rather than an aggregate — averaging over a distance ladder would
    /// destroy the only thing the vertical measures.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<int, TypedMemEvalOutcomeCounts>? ByDistance { get; init; }

    /// <summary>Prospective only: pair-level consistency.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TypedMemEvalPairConsistency? PairConsistency { get; init; }

    /// <summary>
    /// Forgetting only: asserted an invalidated value as current. Counted separately from
    /// <see cref="TypedMemEvalOutcomeCounts.Wrong"/> because a confident stale answer is a
    /// fabrication-shaped error rather than an ordinary miss.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StaleRecall { get; init; }

    /// <summary>
    /// Forgetting only: answered "no longer known" about a still-valid fact. The control
    /// shape exists to catch a system that forgets everything and thereby looks careful.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OverForgetting { get; init; }

    /// <summary>
    /// Forgetting only: claimed to have forgotten something never stated. Symmetric to
    /// <see cref="OverForgetting"/>, and the reason the never-known probes exist — without this
    /// count the three-state discrimination claim could not be checked from the published numbers.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MisattributedForgetting { get; init; }

    /// <summary>Evidence-attribution totals.</summary>
    public required TypedMemEvalAttributionSummary Attribution { get; init; }

    /// <summary>Realised-coverage distribution.</summary>
    public required TypedMemEvalCoverageSummary Coverage { get; init; }

    /// <summary>
    /// Questions reported as unrun with the reason, e.g. duration questions under a
    /// timestamp-free injection mode. Empty on an ordinary run.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnrunReasons { get; init; }
        = new Dictionary<string, string>();

    /// <summary>The judge prompt fingerprint that produced these verdicts.</summary>
    public required string JudgePromptFingerprint { get; init; }
}
