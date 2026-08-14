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
    /// <remarks>
    /// <para>
    /// Setting this to false removes the synthesised session-boundary turn pair — the
    /// <c>"--- Session N ---"</c> user turn and its
    /// <see cref="LongMemEval.LongMemEvalHistoryFormatter.SessionBoundaryAcknowledgement"/> reply.
    /// </para>
    /// <para>
    /// <b>Applies to structured history injection only.</b> It is read by
    /// <see cref="LongMemEval.LongMemEvalHistoryFormatter.Format"/> and <i>not</i> by
    /// <see cref="LongMemEval.LongMemEvalHistoryFormatter.FormatAsTextBlob"/>, which always emits its
    /// own <c>"### Session N:"</c> headers because that is the official LongMemEval prompt format.
    /// Since <see cref="HistoryInjectionMode"/> defaults to
    /// <see cref="Models.HistoryInjectionMode.TextBlob"/>, setting this to false on otherwise-default
    /// options has no effect — set <see cref="HistoryInjectionMode"/> to
    /// <see cref="Models.HistoryInjectionMode.StructuredChatHistory"/> for it to apply.
    /// </para>
    /// <para>
    /// It also does not suppress
    /// <see cref="LongMemEval.LongMemEvalHistoryFormatter.UnpairedUserAcknowledgement"/>, the filler
    /// reply synthesised for a user turn the dataset leaves unanswered. Use
    /// <see cref="SyntheticTurnMarker"/> to make every synthesised turn identifiable.
    /// </para>
    /// </remarks>
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
    /// Optional sampling temperature for the <i>answer</i> call. Null (default) leaves the answer
    /// model at whatever the provider defaults to — on most deployments, 1.0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the noise floor of every number the benchmark produces. Repeats of one configuration
    /// over an identical corpus can flip verdicts with byte-identical retrieval — same sessions, same
    /// config, same retrieved items — which is the answer model disagreeing with itself, not the
    /// memory system behaving differently. Below that floor no memory change is detectable, and
    /// nothing in a default result says the floor is there.
    /// </para>
    /// <para>
    /// The value is passed through as given. AgentEval does not assume 0 works: reasoning-model
    /// deployments reject an explicit temperature, and some reject 0 specifically. What the provider
    /// did with it is recorded on <see cref="ExternalBenchmarkResult.AnswerSampling"/> — including
    /// the case where the agent under test could not carry it at all.
    /// </para>
    /// <para>
    /// Reaching the answer call requires the agent to implement
    /// <see cref="AgentEval.Core.IAnswerSamplingConfigurableAgent"/>. AgentEval owns the judge client
    /// and can pin the judge directly (<see cref="JudgeTemperature"/>); the agent under test is the
    /// caller's, and a benchmark that quietly did nothing here would be worse than one that never
    /// offered the option.
    /// </para>
    /// </remarks>
    public double? AnswerTemperature { get; init; }

    /// <summary>
    /// Optional sampling seed for the <i>answer</i> call. Null (default) sends no seed.
    /// </summary>
    /// <remarks>
    /// A seed that a provider silently ignores is worse than no seed at all, because the run looks
    /// reproducible while it is not. So the seed's fate is reported per parameter on
    /// <see cref="ExternalBenchmarkResult.AnswerSampling"/>, and the strongest claim AgentEval will
    /// make from a successful call is <see cref="AnswerSamplingDisposition.SentUnverified"/> —
    /// <see cref="AnswerSamplingDisposition.SentAndEchoed"/> requires the provider to echo the value
    /// back. See <see cref="AnswerTemperature"/> for how the value reaches the agent.
    /// </remarks>
    public int? AnswerSeed { get; init; }

    /// <summary>
    /// Whether session dates reach the agent as real timestamps, and whether the harness keeps its
    /// own in-text date scaffolding. Default: <see cref="TemporalGroundingMode.None"/> — historical
    /// behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Any mode other than <see cref="TemporalGroundingMode.None"/> requires the agent to implement
    /// <see cref="AgentEval.Core.ITimestampedHistoryInjectableAgent"/>, and the run fails before its
    /// first provider call if it does not. Falling back to text injection would answer temporal
    /// questions from the very scaffolding this option exists to take away, and would report a score
    /// for a measurement that never happened.
    /// </para>
    /// <para>
    /// It applies to any LongMemEval-shaped dataset, so the same flag turns the real corpus into a
    /// time-grounded corpus. <c>LongMemEvalTimeGroundedCorpus</c> adds questions whose answers depend
    /// on the clock, which the original corpus does not contain.
    /// </para>
    /// </remarks>
    public TemporalGroundingMode TemporalGrounding { get; init; } = TemporalGroundingMode.None;

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
        if (!Enum.IsDefined(TemporalGrounding))
            throw new ArgumentOutOfRangeException(nameof(TemporalGrounding));

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

        // Time-grounding replaces history injection entirely — the turns go through the timestamped
        // channel — so a TextBlob setting alongside it describes an injection that never happens.
        // TextBlob is also the default, so this is usually a forgotten line rather than a wrong one,
        // and saying so beats silently overriding it.
        if (TemporalGrounding != TemporalGroundingMode.None &&
            HistoryInjectionMode == HistoryInjectionMode.TextBlob)
        {
            throw new ArgumentException(
                $"TemporalGrounding is {TemporalGrounding}, which injects history through " +
                $"ITimestampedHistoryInjectableAgent, but HistoryInjectionMode is TextBlob — the " +
                $"blob would never be built. Set HistoryInjectionMode to StructuredChatHistory or " +
                $"Auto for a time-grounded run.",
                nameof(HistoryInjectionMode));
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
        if (AnswerTemperature is { } answerTemperature &&
            (!double.IsFinite(answerTemperature) || answerTemperature is < 0 or > 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(AnswerTemperature), AnswerTemperature,
                "AnswerTemperature must be null or a finite value between 0 and 2.");
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
