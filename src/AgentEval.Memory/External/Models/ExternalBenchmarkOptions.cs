// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Configuration options for running an external benchmark.
/// </summary>
public class ExternalBenchmarkOptions
{
    /// <summary>Maximum supported judge retries.</summary>
    public const int MaximumJudgeRetries = 3;

    /// <summary>Maximum number of questions to run (null = all, runs every question in the dataset). Default: null.</summary>
    public int? MaxQuestions { get; init; } = null;

    /// <summary>
    /// Use stratified sampling to ensure proportional representation of each question type.
    /// When false, questions are shuffled then truncated. Default: true.
    /// </summary>
    public bool StratifiedSampling { get; init; } = true;

    /// <summary>
    /// Preserve session boundaries when formatting history for injection.
    /// Default: true.
    /// </summary>
    public bool PreserveSessionBoundaries { get; init; } = true;

    /// <summary>
    /// Include timestamps in injected history (from dataset metadata).
    /// Default: true.
    /// </summary>
    public bool IncludeTimestamps { get; init; } = true;

    /// <summary>Upper bound on <see cref="SyntheticTurnMarker"/>.</summary>
    public const int MaximumSyntheticTurnMarkerLength = 64;

    /// <summary>
    /// Random seed for reproducible sampling. Null = non-deterministic.
    /// </summary>
    /// <remarks>
    /// A fixed seed makes a sample reproducible, which also means repeating a run with the same seed
    /// re-draws the <i>same questions</i> rather than sampling the dataset again. Repeated runs under
    /// one seed measure that one sample many times; they do not widen coverage. Vary the seed to draw
    /// a different sample, and see <see cref="IncludeQuestionTypes"/> and
    /// <see cref="AbstentionPolicy"/> to control composition directly rather than by re-rolling.
    /// </remarks>
    public int? RandomSeed { get; init; }

    /// <summary>
    /// Restricts sampling to these question types. Null or empty applies no filter and reproduces
    /// historical selection exactly. Default: null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stratified sampling spreads a budget proportionally across all types, so a 50-question subset
    /// of LongMemEval yields about 6 <c>single-session-assistant</c> questions — enough to contribute
    /// to an overall score, not enough to carry a per-type claim. Naming the types moves the whole
    /// budget onto them: 30 questions of one type instead of 50 questions of which 6 are.
    /// </para>
    /// <para>
    /// Stratification still applies <i>within</i> the requested set, and selection remains
    /// reproducible under <see cref="RandomSeed"/>. Types are matched ordinally and are case
    /// sensitive, matching the dataset's own <c>question_type</c> values.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string>? IncludeQuestionTypes { get; init; }

    /// <summary>
    /// How abstention questions are sampled. Default:
    /// <see cref="AbstentionSamplingPolicy.AsSampled"/>, which is historical behaviour.
    /// </summary>
    public AbstentionSamplingPolicy AbstentionPolicy { get; init; } = AbstentionSamplingPolicy.AsSampled;

    /// <summary>
    /// Requested abstention share of the sample, 0.0-1.0. Required by — and valid only with —
    /// <see cref="AbstentionSamplingPolicy.TargetProportion"/>.
    /// </summary>
    /// <remarks>
    /// The share is a request, not a guarantee: when the pool holds fewer abstention questions than
    /// the target, the shortfall is <i>not</i> topped up with ordinary questions, because that would
    /// quietly change what the run measured. Compare against
    /// <see cref="SampleComposition.RealisedAbstentionProportion"/> to see what was actually drawn.
    /// </remarks>
    public double? AbstentionTargetProportion { get; init; }

    /// <summary>
    /// How much provenance is captured onto <see cref="ExternalBenchmarkResult.Provenance"/>.
    /// Default: <see cref="Models.RunProvenanceMode.None"/>.
    /// </summary>
    public RunProvenanceMode RunProvenanceMode { get; init; } = RunProvenanceMode.None;

    /// <summary>
    /// Prefix applied to every turn AgentEval synthesises when injecting history, making them
    /// removable by exact prefix match. Null (default) emits the historical text verbatim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies to structured history injection only — see
    /// <see cref="LongMemEval.LongMemEvalHistoryFormatter.Format"/>. The text-blob format is the
    /// official LongMemEval prompt and is left byte-for-byte alone.
    /// </para>
    /// <para>
    /// Covers strictly more than <see cref="PreserveSessionBoundaries"/>: that flag removes the
    /// session-boundary turn pair, but the filler reply synthesised for a user turn with no assistant
    /// response is emitted either way, and is indistinguishable from real content once retrieved.
    /// </para>
    /// </remarks>
    public string? SyntheticTurnMarker { get; init; }

    /// <summary>
    /// Dataset mode identifier. Meaning is benchmark-specific
    /// (e.g., "Oracle", "S", "M" for LongMemEval). Default: null.
    /// </summary>
    public string? DatasetMode { get; init; }

    /// <summary>
    /// Optional path to the dataset file. Null = use embedded subset.
    /// </summary>
    public string? DatasetPath { get; init; }

    /// <summary>
    /// Controls how conversation history is injected into the agent.
    /// Default: <see cref="HistoryInjectionMode.TextBlob"/> — matches the original LongMemEval paper's
    /// prompt format and works with any agent.
    /// </summary>
    /// <remarks>
    /// TextBlob is the default because it matches the LongMemEval paper methodology and ensures
    /// history is visible to all middleware, context providers, and memory pipelines.
    /// Set to <see cref="HistoryInjectionMode.StructuredChatHistory"/> to force fast structured injection
    /// (requires IHistoryInjectableAgent). Set to <see cref="HistoryInjectionMode.Auto"/> to let the
    /// runner choose based on agent capabilities.
    /// </remarks>
    public HistoryInjectionMode HistoryInjectionMode { get; init; } = HistoryInjectionMode.TextBlob;

    /// <summary>
    /// Controls how judge infrastructure failures affect the run.
    /// Default: retry within the configured bound, then retain an inconclusive result.
    /// </summary>
    public JudgeFailurePolicy JudgeFailurePolicy { get; init; } = JudgeFailurePolicy.RetryThenInconclusive;

    /// <summary>Maximum retry count after the initial judge attempt. Valid range: 0-3.</summary>
    public int MaxJudgeRetries { get; init; } = 1;

    /// <summary>
    /// Optional judge sampling temperature. Null uses the provider/model default and is
    /// compatible with deployments that reject explicit temperature values.
    /// </summary>
    public double? JudgeTemperature { get; init; }

    /// <summary>
    /// Maximum judge output-token budget. Includes reasoning tokens on reasoning models.
    /// Valid range: 1-4096. Default: 256.
    /// </summary>
    public int JudgeMaxOutputTokens { get; init; } = 256;

    /// <summary>Controls diagnostic evidence retained from judge responses.</summary>
    public JudgeEvidenceMode JudgeEvidenceMode { get; init; } = JudgeEvidenceMode.Outcome;

    /// <summary>
    /// How the judge verdict is requested and recovered. Default:
    /// <see cref="JudgeVerdictProtocol.FreeText"/>, which reproduces historical scoring exactly.
    /// </summary>
    /// <remarks>
    /// <see cref="JudgeVerdictProtocol.StructuredJson"/> is opt-in because it changes verdicts on the
    /// questions the free-text parser mis-scored — that is the point of it, and it is also why enabling
    /// it makes results non-comparable with a base recorded under the free-text protocol.
    /// </remarks>
    public JudgeVerdictProtocol JudgeVerdictProtocol { get; init; } = JudgeVerdictProtocol.FreeText;

    /// <summary>
    /// Retains the bounded raw judge response regardless of <see cref="JudgeEvidenceMode"/>.
    /// Default: false, which leaves existing behaviour unchanged.
    /// </summary>
    /// <remarks>
    /// Separates "how much do we render" from "what do we keep": without it, diagnosing whether a failed
    /// verdict was the judge being wrong or the wrapper being unparseable requires raising the evidence
    /// mode, which also changes the rendered explanation.
    /// </remarks>
    public bool RetainRawJudgeResponse { get; init; }

    /// <summary>
    /// Whether one judge call decides the whole question or each gold-answer predicate is judged
    /// separately. Default: <see cref="JudgeDecompositionMode.None"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="JudgeDecompositionMode.PerPredicate"/> costs one provider call per extracted predicate
    /// (bounded by <c>LongMemEvalPredicateExtractor.MaximumPredicates</c>), so a run's judge spend rises
    /// by the mean predicate count.
    /// </remarks>
    public JudgeDecompositionMode JudgeDecompositionMode { get; init; } = JudgeDecompositionMode.None;

    /// <summary>
    /// How per-predicate outcomes combine when <see cref="JudgeDecompositionMode.PerPredicate"/> is
    /// active. Default: <see cref="Models.PredicateCombinationRule.AllMustHold"/>, matching official
    /// LongMemEval scoring, where a partial answer is incorrect.
    /// </summary>
    public PredicateCombinationRule PredicateCombinationRule { get; init; }
        = PredicateCombinationRule.AllMustHold;

    /// <summary>Controls normalized retrieval-evidence capture. Default: None.</summary>
    public EvidenceCaptureMode EvidenceCaptureMode { get; init; } = EvidenceCaptureMode.None;

    /// <summary>Top-K retrieval depth used for evaluator-side gold diagnostics.</summary>
    public int EvidenceTopK { get; init; } = 10;

    /// <summary>Whether Full evidence mode can persist bounded user content.</summary>
    public bool PersistsEvidenceContent => EvidenceCaptureMode == EvidenceCaptureMode.Full;

    /// <summary>Validates bounded judge and evidence configuration before provider calls begin.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(JudgeFailurePolicy))
            throw new ArgumentOutOfRangeException(nameof(JudgeFailurePolicy));
        if (!Enum.IsDefined(JudgeEvidenceMode))
            throw new ArgumentOutOfRangeException(nameof(JudgeEvidenceMode));
        if (!Enum.IsDefined(JudgeVerdictProtocol))
            throw new ArgumentOutOfRangeException(nameof(JudgeVerdictProtocol));
        if (!Enum.IsDefined(JudgeDecompositionMode))
            throw new ArgumentOutOfRangeException(nameof(JudgeDecompositionMode));
        if (!Enum.IsDefined(PredicateCombinationRule))
            throw new ArgumentOutOfRangeException(nameof(PredicateCombinationRule));
        if (!Enum.IsDefined(EvidenceCaptureMode))
            throw new ArgumentOutOfRangeException(nameof(EvidenceCaptureMode));
        if (!Enum.IsDefined(AbstentionPolicy))
            throw new ArgumentOutOfRangeException(nameof(AbstentionPolicy));
        if (!Enum.IsDefined(RunProvenanceMode))
            throw new ArgumentOutOfRangeException(nameof(RunProvenanceMode));

        // A proportion is meaningful under exactly one policy. Accepting it under the others would
        // let a run look configured for an abstention share it never applied.
        if (AbstentionPolicy == AbstentionSamplingPolicy.TargetProportion)
        {
            if (AbstentionTargetProportion is not { } proportion)
            {
                throw new ArgumentException(
                    "AbstentionTargetProportion is required when AbstentionPolicy is TargetProportion.",
                    nameof(AbstentionTargetProportion));
            }
            if (!double.IsFinite(proportion) || proportion is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(AbstentionTargetProportion), AbstentionTargetProportion,
                    "AbstentionTargetProportion must be a finite value between 0 and 1.");
            }
        }
        else if (AbstentionTargetProportion.HasValue)
        {
            throw new ArgumentException(
                $"AbstentionTargetProportion is only valid when AbstentionPolicy is " +
                $"{nameof(AbstentionSamplingPolicy.TargetProportion)}; it is set while the policy is " +
                $"{AbstentionPolicy}, where it would be silently ignored.",
                nameof(AbstentionTargetProportion));
        }

        if (IncludeQuestionTypes is { Count: > 0 } types &&
            types.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "IncludeQuestionTypes must not contain null or whitespace entries.",
                nameof(IncludeQuestionTypes));
        }

        if (SyntheticTurnMarker is { Length: > MaximumSyntheticTurnMarkerLength })
        {
            throw new ArgumentOutOfRangeException(
                nameof(SyntheticTurnMarker), SyntheticTurnMarker.Length,
                $"SyntheticTurnMarker must be at most {MaximumSyntheticTurnMarkerLength} characters.");
        }
        if (MaxJudgeRetries is < 0 or > MaximumJudgeRetries)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxJudgeRetries), MaxJudgeRetries,
                $"MaxJudgeRetries must be between 0 and {MaximumJudgeRetries}.");
        }
        if (JudgeTemperature is { } temperature &&
            (!double.IsFinite(temperature) || temperature is < 0 or > 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(JudgeTemperature), JudgeTemperature,
                "JudgeTemperature must be null or a finite value between 0 and 2.");
        }
        if (JudgeMaxOutputTokens is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(JudgeMaxOutputTokens), JudgeMaxOutputTokens,
                "JudgeMaxOutputTokens must be between 1 and 4096.");
        }
        if (EvidenceTopK is < 1 or > QuestionEvidenceEnvelope.MaximumReferences)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EvidenceTopK), EvidenceTopK,
                $"EvidenceTopK must be between 1 and {QuestionEvidenceEnvelope.MaximumReferences}.");
        }
    }
}
