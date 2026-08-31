// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// Configuration for a TypedMemEval run.
/// </summary>
/// <remarks>
/// <para>
/// A thin façade rather than <see cref="ExternalBenchmarkOptions"/> re-exposed. It carries only
/// knobs that mean something for an embedded-corpus family run, and it applies the family's
/// invariants on the way down (ADR-026 §3). Three of those invariants are deliberate omissions:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>No dataset path.</b> The family is embedded-only. A path knob would reintroduce the
/// "which corpus produced this number" ambiguity the family exists to end.
/// </description></item>
/// <item><description>
/// <b>No provenance knob.</b> The mapper always sets <see cref="RunProvenanceMode.Full"/> —
/// Full rather than merely non-None, because the shipped capture path populates the dataset
/// identifier and hash only under Full, and a result that cannot say which corpus produced it
/// is exactly the artefact the identity rule exists to prevent.
/// </description></item>
/// <item><description>
/// <b>No verdict-protocol knob.</b> Always structured JSON. A free-text protocol cannot carry a
/// five-way outcome, and the family has no historical free-text scores to stay comparable with.
/// </description></item>
/// </list>
/// </remarks>
public sealed class TypedMemEvalOptions
{
    /// <summary>Maximum questions to run; null runs the whole corpus.</summary>
    public int? MaxQuestions { get; init; }

    /// <summary>Seed for reproducible question selection. Null selects non-deterministically.</summary>
    /// <remarks>
    /// A fixed seed re-draws the <i>same</i> questions, so repeated runs under one seed measure
    /// that one sample many times rather than widening coverage.
    /// </remarks>
    public int? RandomSeed { get; init; }

    /// <summary>Sampling temperature for the answer call; null leaves the provider default.</summary>
    /// <remarks>
    /// Reaching the answer call requires the agent to implement
    /// <see cref="AgentEval.Core.IAnswerSamplingConfigurableAgent"/>. What actually happened is
    /// recorded on <see cref="ExternalBenchmarkResult.AnswerSampling"/>, including the case where
    /// the agent could not carry the value at all — a benchmark that quietly did nothing here
    /// would be worse than one that never offered the option.
    /// </remarks>
    public double? AnswerTemperature { get; init; }

    /// <summary>Sampling seed for the answer call; null sends none.</summary>
    public int? AnswerSeed { get; init; }

    /// <summary>
    /// How history reaches the agent. Forced to
    /// <see cref="Models.HistoryInjectionMode.StructuredChatHistory"/> for grounding-required
    /// verticals, because the shipped validation correctly rejects temporal grounding combined
    /// with the <c>TextBlob</c> default.
    /// </summary>
    public HistoryInjectionMode HistoryInjectionMode { get; init; } = HistoryInjectionMode.StructuredChatHistory;

    /// <summary>
    /// Grounding override. Null uses the vertical's required mode, which is what almost every
    /// caller wants; the Prospective control arm is the reason an override exists at all.
    /// </summary>
    public TemporalGroundingMode? TemporalGrounding { get; init; }

    /// <summary>
    /// Whether session dates reach the agent at all. Default true.
    /// </summary>
    /// <remarks>
    /// Setting this false on a vertical whose answers are derived from timestamps — Arithmetic's
    /// duration questions — makes those questions unanswerable by construction. They are then
    /// reported as <see cref="TypedMemEvalOutcome.Unrun"/> with a reason rather than counted as
    /// failures, because a harness artefact scored as a memory result is a lie about the system
    /// under test.
    /// </remarks>
    public bool IncludeTimestamps { get; init; } = true;

    /// <summary>
    /// Labels this run as the control arm, which stamps a <c>-control</c> dataset mode.
    /// </summary>
    /// <remarks>
    /// Same corpus, same hash, different options — never a second corpus file. The pair only
    /// means something when both arms run over identical questions.
    /// </remarks>
    public bool ControlArm { get; init; }

    /// <summary>Judge sampling temperature; null uses the provider default.</summary>
    public double? JudgeTemperature { get; init; }

    /// <summary>Judge output-token budget.</summary>
    /// <remarks>
    /// 1500 because that is the value the SHIPPED CALIBRATION WAS MEASURED AT — agreement 0.987
    /// over 230 cases on a reasoning deployment. A default that differs from the configuration the
    /// published number came from means the out-of-box judge is not the judge the claim describes.
    ///
    /// It was 512, which is ample for a non-reasoning model and DANGEROUS on a reasoning one: the
    /// budget covers reasoning tokens too, so the model can spend the whole allowance thinking and
    /// return an empty completion. This family has already paid for that once — empty completions
    /// scored as answers until the silence accounting was built — and a consumer pointing the judge
    /// at a gpt-5.x deployment on defaults would have hit it silently.
    ///
    /// BEHAVIOUR-AFFECTING. Raising a cap can flip a formerly-truncated verdict, so the effective
    /// value is stamped per run in <c>BenchmarkRunProvenance.JudgeMaxOutputTokens</c>: two runs
    /// differing only in this number are not comparable, and that has to be visible rather than
    /// inferred.
    /// </remarks>
    public int JudgeMaxOutputTokens { get; init; } = 1500;

    /// <summary>Retries after the first judge attempt, 0–3.</summary>
    public int MaxJudgeRetries { get; init; } = 1;

    /// <summary>What a judge infrastructure failure does to the run.</summary>
    public JudgeFailurePolicy JudgeFailurePolicy { get; init; } = JudgeFailurePolicy.RetryThenInconclusive;

    /// <summary>Diagnostic evidence retained from judge responses.</summary>
    public JudgeEvidenceMode JudgeEvidenceMode { get; init; } = JudgeEvidenceMode.Outcome;

    /// <summary>Retains the bounded raw judge response regardless of evidence mode.</summary>
    public bool RetainRawJudgeResponse { get; init; }

    /// <summary>
    /// Retrieval-evidence capture. <see cref="EvidenceCaptureMode.References"/> or higher is what
    /// makes realised coverage and evidence attribution observable at all; without it the report
    /// says <c>Unobserved</c> rather than guessing.
    /// </summary>
    public EvidenceCaptureMode EvidenceCaptureMode { get; init; } = EvidenceCaptureMode.References;

    /// <summary>Top-K retrieval depth used for evaluator-side gold diagnostics.</summary>
    public int EvidenceTopK { get; init; } = 10;

    /// <summary>Validates the façade's own bounds before anything downstream runs.</summary>
    public void Validate()
    {
        if (MaxQuestions is < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxQuestions), MaxQuestions, "MaxQuestions must be non-negative.");
        if (!Enum.IsDefined(HistoryInjectionMode))
            throw new ArgumentOutOfRangeException(nameof(HistoryInjectionMode));
        if (TemporalGrounding is { } grounding && !Enum.IsDefined(grounding))
            throw new ArgumentOutOfRangeException(nameof(TemporalGrounding));
        if (!Enum.IsDefined(JudgeFailurePolicy))
            throw new ArgumentOutOfRangeException(nameof(JudgeFailurePolicy));
        if (!Enum.IsDefined(JudgeEvidenceMode))
            throw new ArgumentOutOfRangeException(nameof(JudgeEvidenceMode));
        if (!Enum.IsDefined(EvidenceCaptureMode))
            throw new ArgumentOutOfRangeException(nameof(EvidenceCaptureMode));
    }

    /// <summary>
    /// Maps the façade onto the pipeline's own options with the family's invariants applied.
    /// </summary>
    internal ExternalBenchmarkOptions ToExternalOptions(TypedMemEvalVerticalDescriptor descriptor)
    {
        var grounding = TemporalGrounding ?? descriptor.RequiredGrounding;

        // Forced rather than validated-and-rejected: the caller asked for a vertical whose whole
        // premise is that dates arrive as timestamps, and the default TextBlob mode would make the
        // shipped validation throw on options the caller never wrote.
        var injection = grounding != TemporalGroundingMode.None &&
                        HistoryInjectionMode == HistoryInjectionMode.TextBlob
            ? HistoryInjectionMode.StructuredChatHistory
            : HistoryInjectionMode;

        return new ExternalBenchmarkOptions
        {
            MaxQuestions = MaxQuestions,
            RandomSeed = RandomSeed,
            StratifiedSampling = true,
            PreserveSessionBoundaries = true,
            IncludeTimestamps = IncludeTimestamps,
            AnswerTemperature = AnswerTemperature,
            AnswerSeed = AnswerSeed,
            TemporalGrounding = grounding,
            HistoryInjectionMode = injection,
            DatasetMode = ControlArm ? $"{descriptor.CorpusId}-control" : descriptor.CorpusId,
            // Never exposed on the façade; stated here so the intent is visible at the seam.
            DatasetPath = null,
            RunProvenanceMode = RunProvenanceMode.Full,
            JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson,
            JudgeTemperature = JudgeTemperature,
            JudgeMaxOutputTokens = JudgeMaxOutputTokens,
            MaxJudgeRetries = MaxJudgeRetries,
            JudgeFailurePolicy = JudgeFailurePolicy,
            JudgeEvidenceMode = JudgeEvidenceMode,
            RetainRawJudgeResponse = RetainRawJudgeResponse,
            EvidenceCaptureMode = EvidenceCaptureMode,
            EvidenceTopK = EvidenceTopK
        };
    }
}
