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

    /// <summary>
    /// Random seed for reproducible sampling. Null = non-deterministic.
    /// </summary>
    public int? RandomSeed { get; init; }

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
