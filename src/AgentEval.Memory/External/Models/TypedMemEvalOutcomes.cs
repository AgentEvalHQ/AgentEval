// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Serialization;

namespace AgentEval.Memory.External.Models;

/// <summary>
/// What a TypedMemEval answer did, relative to gold (ADR-026 §6, axis 1).
/// </summary>
/// <remarks>
/// <para>
/// The first five members are the benchmark's measurement vector — the thing a TypedMemEval
/// result reports instead of one percentage. The last two are not outcomes at all: they record
/// that the run failed to produce a measurement, and they exist as their own members so a
/// judge failure or a skipped question can never be quietly absorbed into
/// <see cref="Wrong"/> and make a system look worse than the evidence shows.
/// </para>
/// <para>
/// Outcomes describe deviation from gold, not surface form. On a never-known probe or a
/// correctly-held before-arm, the "negative" answer <i>is</i> gold and scores
/// <see cref="Correct"/>.
/// </para>
/// </remarks>
public enum TypedMemEvalOutcome
{
    /// <summary>Matches gold. For an invalidated Forgetting fact, that means saying it is no longer valid.</summary>
    Correct = 0,

    /// <summary>Commits to an incorrect value. Includes Forgetting stale recall, which is also counted separately.</summary>
    Wrong = 1,

    /// <summary>Declines to commit to any value — "I don't know", "I have no record".</summary>
    Abstained = 2,

    /// <summary>
    /// Confident false negative: asserts that nothing is there ("nothing is due", "you never
    /// told me") when gold says otherwise. The denial half of the uncertainty/denial line.
    /// </summary>
    Missed = 3,

    /// <summary>
    /// Prospective before-arms only: fires early, asserting the not-yet-true thing as already
    /// true. Only a before/after pair can see this, which is why the vertical is built in pairs.
    /// </summary>
    Premature = 4,

    /// <summary>
    /// The judge returned no verdict. Not a measurement — reported separately so the five
    /// measurement outcomes stay a description of the system under test rather than of the harness.
    /// </summary>
    Inconclusive = 5,

    /// <summary>
    /// The question was skipped with notice — an Arithmetic duration question under a
    /// timestamp-free injection mode, where the answer is unrecoverable by construction (ADR §5.3).
    /// Counting these as failures would report a harness artefact as a memory result.
    /// </summary>
    Unrun = 6
}

/// <summary>
/// Whether the question's gold evidence was observably in front of the answer model
/// (ADR-026 §6, axis 2).
/// </summary>
/// <remarks>
/// Named for what it is: <b>reference-level presence, necessary but not sufficient</b>. A gold
/// session id in the answer context does not prove the gold value survived the memory system's
/// own summarization or truncation. Exactly one causal reading is safe —
/// <see cref="TypedMemEvalOutcome.Wrong"/> with <see cref="EvidenceAbsent"/> <i>is</i> a
/// retrieval-side failure. The mirror reading (<see cref="TypedMemEvalOutcome.Missed"/> with
/// <see cref="EvidencePresent"/> means a synthesis failure) is an inference, not a fact, because
/// a compression loss inside the store looks identical from here.
/// </remarks>
public enum TypedMemEvalEvidenceAttribution
{
    /// <summary>No evidence telemetry was supplied. Never guessed, and reported as its own share.</summary>
    Unobserved = 0,

    /// <summary>Every gold component was referenced in what reached the answer model.</summary>
    EvidencePresent = 1,

    /// <summary>At least one gold component was not referenced.</summary>
    EvidenceAbsent = 2
}

/// <summary>How strong the evidence observation behind an attribution was.</summary>
public enum TypedMemEvalAttributionLevel
{
    /// <summary>Nothing observed.</summary>
    None = 0,

    /// <summary>Gold session identifiers matched. Necessary, not sufficient.</summary>
    Reference = 1,

    /// <summary>
    /// The gold text itself was found in the surfaced content
    /// (<see cref="EvidenceCaptureMode.Full"/>). Upgrades the inference from "was referenced" to
    /// "was actually present".
    /// </summary>
    Content = 2
}

/// <summary>Which adapter-supplied list realised coverage was computed from.</summary>
public enum TypedMemEvalCoverageSource
{
    /// <summary>No usable evidence list was supplied.</summary>
    None = 0,

    /// <summary>Computed from what the adapter said reached the answer model. Preferred.</summary>
    AnswerContext = 1,

    /// <summary>
    /// Computed from the retrieval list because the adapter supplied no answer-context list.
    /// Reported rather than silently substituted: retrieval is an upper bound on what the model saw.
    /// </summary>
    Retrieved = 2
}

/// <summary>Per-component coverage for questions whose gold is more than one session.</summary>
/// <remarks>
/// Forgetting's signature failure is asymmetric retrieval — surfacing the statement but not the
/// invalidation, which yields a confident stale answer. Arithmetic's is a missing input, which
/// wrongs the sum rather than degrading it. Neither is visible from a single per-question number,
/// so components are reported individually.
/// </remarks>
public sealed class TypedMemEvalComponentCoverage
{
    /// <summary>What this component is: <c>statement</c>, <c>invalidation</c>, or <c>input</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Index of the session carrying it, within the question's own haystack.</summary>
    public required int SessionIndex { get; init; }

    /// <summary>Whether the component was referenced in what reached the answer model.</summary>
    public required bool Present { get; init; }
}

/// <summary>
/// The family's per-question detail, carried on <see cref="QuestionResult.TypedOutcome"/>.
/// </summary>
public sealed class TypedMemEvalQuestionDetail
{
    /// <summary>The verbal outcome (axis 1).</summary>
    public required TypedMemEvalOutcome Outcome { get; init; }

    /// <summary>Evidence attribution (axis 2).</summary>
    public required TypedMemEvalEvidenceAttribution Attribution { get; init; }

    /// <summary>How strong the observation behind <see cref="Attribution"/> was.</summary>
    public TypedMemEvalAttributionLevel AttributionLevel { get; init; }

    /// <summary>
    /// Gold sessions observably surfaced, divided by the question's gold count; null when no
    /// evidence telemetry was supplied. Null means "not observed" and must not be read as zero.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RealisedGoldCoverage { get; init; }

    /// <summary>Which list <see cref="RealisedGoldCoverage"/> came from.</summary>
    public TypedMemEvalCoverageSource CoverageSource { get; init; }

    /// <summary>Per-component presence, where the question has more than one gold component.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TypedMemEvalComponentCoverage>? ComponentCoverage { get; init; }

    /// <summary>The diagnostic stratum this question belongs to, e.g. <c>due-later-reminder</c>.</summary>
    public required string Shape { get; init; }

    /// <summary>Links the two arms of a before/after or invalidated/control pair.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PairId { get; init; }

    /// <summary>Which arm of its pair this question is: before, after, control, invalidated.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arm { get; init; }

    /// <summary>WorkingMemory ladder position — intervening sessions between statement and query.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DistanceSessions { get; init; }

    /// <summary>
    /// The time-grounded probe question this one was carried from, for the twelve
    /// Prospective seed questions. Lineage, so the overlap in the citation rule is checkable
    /// from the result rather than only from the documentation.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SeededFrom { get; init; }

    /// <summary>
    /// Forgetting: asserted the invalidated value as current. The dangerous-error class — it is a
    /// fabrication-shaped failure, not an ordinary miss, and is surfaced as its own count.
    /// </summary>
    public bool IsStaleRecall { get; init; }

    /// <summary>Forgetting: answered "no longer known" about a fact that is still valid.</summary>
    public bool IsOverForgetting { get; init; }

    /// <summary>
    /// Forgetting: answered "no longer known" about a fact never stated — confabulating a
    /// forgetting history the system never had.
    /// </summary>
    public bool IsMisattributedForgetting { get; init; }

    /// <summary>
    /// Episodic list-order: share of observed item pairs placed in the right order, or null when
    /// the question is not list-order. Scored conditionally on coverage (ADR §6 rule 3) so a
    /// budget-limited system is measured on the ordering of what it actually saw.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? PairwiseOrderAccuracy { get; init; }
}
