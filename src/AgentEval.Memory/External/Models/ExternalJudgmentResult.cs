// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Text.Json.Serialization;

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Result of an external benchmark judge evaluation.
/// Carries both binary correctness (for official compatibility) and a raw score (for analysis).
/// </summary>
public class ExternalJudgmentResult
{
    private JudgeOutcomeStatus? _status;

    /// <summary>
    /// Typed judge outcome. Only Yes and No are ordinary quality judgments.
    /// Legacy successful JSON without this field infers the status from Correct.
    /// </summary>
    public JudgeOutcomeStatus Status
    {
        get => _status ?? Correct switch
        {
            true => JudgeOutcomeStatus.Yes,
            false => JudgeOutcomeStatus.No,
            null => JudgeOutcomeStatus.Invalid
        };
        init => _status = value;
    }

    /// <summary>Binary correctness; null when the judge was inconclusive.</summary>
    public required bool? Correct { get; init; }

    /// <summary>Raw score 0-100; null when the judge was inconclusive.</summary>
    public required double? RawScore { get; init; }

    /// <summary>Optional explanation from the judge.</summary>
    public string? Explanation { get; init; }

    /// <summary>Tokens consumed by the judge call.</summary>
    public int TokensUsed { get; init; }

    /// <summary>Number of provider calls attempted, including retries.</summary>
    public int LlmCallCount { get; init; }

    /// <summary>
    /// Number of provider calls attempted, including retries. Identical to
    /// <see cref="LlmCallCount"/> despite the name — it counts calls, not attempts. Use
    /// <see cref="AttemptsUsed"/> for the number of logical judge attempts.
    /// </summary>
    public int AttemptCount => LlmCallCount;

    /// <summary>
    /// Provider calls made by the first judge attempt, including any response-format fallback calls
    /// that attempt needed.
    /// </summary>
    public int PrimaryLlmCallCount { get; init; }

    /// <summary>
    /// Provider calls made by retry attempts — the calls AgentEval chose to make after the first
    /// attempt failed to produce a binary verdict. Zero on a clean judgment.
    /// </summary>
    /// <remarks>
    /// Always equal to <see cref="LlmCallCount"/> minus <see cref="PrimaryLlmCallCount"/>. Reported
    /// so a caller reconciling an exact expected call count can subtract retries instead of
    /// rejecting a run whose only sin was that AgentEval retried internally.
    /// </remarks>
    public int RetryLlmCallCount { get; init; }

    /// <summary>
    /// Logical judge attempts made: 1 when the first attempt produced a verdict, more when retries
    /// were used. Distinct from <see cref="LlmCallCount"/>, which one attempt can increase by more
    /// than one when the provider rejects a response format.
    /// </summary>
    public int AttemptsUsed { get; init; }

    /// <summary>
    /// The provider's backend build identifier for the judge call (OpenAI's
    /// <c>system_fingerprint</c>), or null when the provider did not return one.
    /// </summary>
    /// <remarks>
    /// Null means "not reported", never "unchanged". Determinism holds only while the backend build
    /// is unchanged, so two runs that differ here were not comparable however identical their
    /// configuration looked.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemFingerprint { get; init; }

    /// <summary>Bounded AgentEval-owned failure code; never provider exception text.</summary>
    public string? SafeFailureCode { get; init; }

    /// <summary>
    /// Bounded raw response. Emitted when <see cref="JudgeEvidenceMode.Raw"/> is enabled, or when
    /// <see cref="ExternalBenchmarkOptions.RetainRawJudgeResponse"/> is set independently of evidence
    /// mode so a short explanation can be rendered while the full text stays available for diagnosis.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawResponse { get; init; }

    /// <summary>
    /// Judge reasoning recovered from its own field under
    /// <see cref="JudgeVerdictProtocol.StructuredJson"/>. Null under the free-text protocol, where
    /// reasoning and verdict are not separable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reasoning { get; init; }

    /// <summary>
    /// Per-predicate outcomes under <see cref="JudgeDecompositionMode.PerPredicate"/>; null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JudgePredicateResult>? PredicateResults { get; init; }

    /// <summary>
    /// The rule that combined <see cref="PredicateResults"/> into <see cref="Status"/>; null when the
    /// verdict came from a single judge call. Recorded rather than implied so a stored result carries
    /// the rule that produced it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PredicateCombinationRule? PredicateCombinationRule { get; init; }
}
