// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Serialization;

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Result of running an external benchmark. Preserves benchmark-native metrics
/// (binary accuracy, per-type breakdown) before conversion to MemoryBaseline.
/// </summary>
public class ExternalBenchmarkResult
{
    /// <summary>Benchmark identifier (e.g., "longmemeval").</summary>
    public required string BenchmarkId { get; init; }

    /// <summary>Display name (e.g., "LongMemEval-S 30q").</summary>
    public required string BenchmarkName { get; init; }

    /// <summary>
    /// Overall accuracy: correct / scored * 100 (micro-average), or null when
    /// no question received an explicit binary judgment.
    /// </summary>
    public required double? OverallAccuracy { get; init; }

    /// <summary>
    /// Task-averaged accuracy across types with at least one scored question,
    /// or null when no type has a score.
    /// </summary>
    public required double? TaskAveragedAccuracy { get; init; }

    /// <summary>Questions selected for execution.</summary>
    public int SelectedQuestions { get; init; }

    /// <summary>Questions for which the agent completed and reached the judge boundary.</summary>
    public int AgentCompletedQuestions { get; init; }

    /// <summary>Questions included in the configured accuracy denominator.</summary>
    public int ScoredQuestions { get; init; }

    /// <summary>Questions judged explicitly correct.</summary>
    public int CorrectQuestions { get; init; }

    /// <summary>Questions judged explicitly incorrect.</summary>
    public int IncorrectQuestions { get; init; }

    /// <summary>Completed questions without an explicit binary judge outcome.</summary>
    public int InconclusiveQuestions { get; init; }

    /// <summary>Questions that failed before reaching a judge outcome.</summary>
    public int AgentFailureQuestions { get; init; }

    /// <summary>Fraction of agent-completed questions with a non-binary judge outcome.</summary>
    public double? JudgeFailureRate { get; init; }

    /// <summary>Question types contributing to task-averaged accuracy.</summary>
    public int ScoredTypeCount { get; init; }

    /// <summary>Per question-type results.</summary>
    public required Dictionary<string, TypeResult> PerTypeResults { get; init; }

    /// <summary>Per-question detail results.</summary>
    public required IReadOnlyList<QuestionResult> QuestionResults { get; init; }

    /// <summary>Total execution time.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>Total LLM calls made (query + judge per question).</summary>
    public int TotalLlmCalls { get; init; }

    /// <summary>
    /// Judge provider calls across the run that were retries rather than first attempts. Subtract
    /// from <see cref="TotalLlmCalls"/> to reconcile against an expected two-calls-per-question
    /// budget without having to guess how often AgentEval retried.
    /// </summary>
    public int TotalJudgeRetryLlmCalls { get; init; }

    /// <summary>Estimated cost in USD.</summary>
    public double? EstimatedCostUsd { get; init; }

    /// <summary>
    /// What the run actually contained, by question type and abstention flag, counted from
    /// <see cref="QuestionResults"/>.
    /// </summary>
    public SampleComposition? Composition { get; init; }

    /// <summary>
    /// What the run did with <see cref="ExternalBenchmarkOptions.AnswerTemperature"/> and
    /// <see cref="ExternalBenchmarkOptions.AnswerSeed"/>; null when neither was requested.
    /// </summary>
    /// <remarks>
    /// Requesting a value and reaching the provider with it are different things, and the difference
    /// decides whether two runs are comparable. Both are recorded, per parameter, counted from
    /// <see cref="QuestionResults"/>.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnswerSamplingReport? AnswerSampling { get; init; }

    /// <summary>
    /// What time-grounding did to the corpus, or null when the run was not time-grounded.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TemporalGroundingReport? TemporalGrounding { get; init; }

    /// <summary>
    /// What the oracle projection realised — evidence kept of evidence available, distractors added
    /// of distractors requested — or null when the run was not an oracle arm.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OracleProjectionReport? OracleProjection { get; init; }

    /// <summary>
    /// The typed outcome vector for a TypedMemEval run; null for every other benchmark.
    /// </summary>
    /// <remarks>
    /// This is the citable form of a TypedMemEval result. <see cref="OverallAccuracy"/> stays
    /// populated on family runs so generic tooling keeps working, but it is compatibility
    /// surface, not a score — see <see cref="TypedMemEvalReport"/> for the citation rule.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TypedMemEvalReport? TypedOutcomes { get; init; }

    /// <summary>
    /// Dataset and judge-prompt fingerprints; null unless
    /// <see cref="ExternalBenchmarkOptions.RunProvenanceMode"/> requested them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkRunProvenance? Provenance { get; init; }

    /// <summary>
    /// Distinct judge backend build identifiers observed across the run, sorted; null when no
    /// provider returned one.
    /// </summary>
    /// <remarks>
    /// More than one entry means the run itself spanned backend builds, so its own questions were
    /// not all answered under the same conditions.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? JudgeSystemFingerprints { get; init; }

    /// <summary>Options used for this run.</summary>
    public required ExternalBenchmarkOptions Options { get; init; }
}

/// <summary>
/// Aggregated result for a single question type within an external benchmark.
/// </summary>
public class TypeResult
{
    private int? _scoredQuestions;

    /// <summary>Question type name (e.g., "temporal-reasoning").</summary>
    public required string TypeName { get; init; }

    /// <summary>Total questions of this type.</summary>
    public required int TotalQuestions { get; init; }

    /// <summary>Number answered correctly.</summary>
    public required int CorrectQuestions { get; init; }

    /// <summary>Number with an explicit yes/no judgment.</summary>
    public int ScoredQuestions
    {
        get => _scoredQuestions ?? TotalQuestions;
        init => _scoredQuestions = value;
    }

    /// <summary>Completed questions without an explicit binary judge outcome.</summary>
    public int InconclusiveQuestions { get; init; }

    /// <summary>Questions that failed before reaching a judge outcome.</summary>
    public int AgentFailureQuestions { get; init; }

    /// <summary>Accuracy as percentage (0-100), or null when no result is scored.</summary>
    public double? Accuracy => ScoredQuestions > 0
        ? (double)CorrectQuestions / ScoredQuestions * 100
        : null;

    /// <summary>Execution time for this type's questions.</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Result for a single question within an external benchmark.
/// </summary>
public class QuestionResult
{
    private JudgeOutcomeStatus? _judgeStatus;
    private bool? _isAbstention;

    /// <summary>Question identifier from the dataset.</summary>
    public required string QuestionId { get; init; }

    /// <summary>Question type (e.g., "multi-session").</summary>
    public required string QuestionType { get; init; }

    /// <summary>The question text.</summary>
    public required string Question { get; init; }

    /// <summary>Gold/expected answer.</summary>
    public required string GoldAnswer { get; init; }

    /// <summary>Agent's response.</summary>
    public required string AgentResponse { get; init; }

    /// <summary>Binary judgment; null when agent execution or judging was inconclusive.</summary>
    public required bool? Correct { get; init; }

    /// <summary>Raw score from judge (0-100); null when inconclusive.</summary>
    public required double? RawScore { get; init; }

    /// <summary>Whether the agent completed before judging.</summary>
    public QuestionExecutionStatus ExecutionStatus { get; init; } = QuestionExecutionStatus.Completed;

    /// <summary>
    /// Typed judge status; null when the agent failed before judging. Legacy successful
    /// JSON without this field infers Yes or No from Correct.
    /// </summary>
    public JudgeOutcomeStatus? JudgeStatus
    {
        get => _judgeStatus ?? Correct switch
        {
            true => JudgeOutcomeStatus.Yes,
            false => JudgeOutcomeStatus.No,
            null => null
        };
        init => _judgeStatus = value;
    }

    /// <summary>
    /// Whether this was an abstention question. Falls back to the LongMemEval convention — a
    /// <c>_abs</c> suffix on the question id — so results stored before this field existed still
    /// report it correctly rather than reporting every question as non-abstention.
    /// </summary>
    public bool IsAbstention
    {
        get => _isAbstention ?? QuestionId.EndsWith("_abs", StringComparison.Ordinal);
        init => _isAbstention = value;
    }

    /// <summary>Agent provider calls attempted for this question.</summary>
    public int AgentLlmCallCount { get; init; }

    /// <summary>Judge provider calls attempted for this question, including retries.</summary>
    public int JudgeLlmCallCount { get; init; }

    /// <summary>Judge provider calls made by the first attempt.</summary>
    public int JudgePrimaryLlmCallCount { get; init; }

    /// <summary>
    /// Judge provider calls made by retries. <see cref="JudgeLlmCallCount"/> always equals
    /// <see cref="JudgePrimaryLlmCallCount"/> plus this.
    /// </summary>
    public int JudgeRetryLlmCallCount { get; init; }

    /// <summary>Logical judge attempts used; 1 when the first attempt produced a verdict.</summary>
    public int JudgeAttemptsUsed { get; init; }

    /// <summary>
    /// Provider backend build identifier for the judge call, or null when none was returned.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JudgeSystemFingerprint { get; init; }

    /// <summary>
    /// Provider backend build identifier reported by the agent under test, or null when the agent
    /// did not surface one. AgentEval sees only what the agent adapter puts in
    /// <c>AgentResponse.AdditionalProperties</c>, so null here means "not reported by the agent".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentSystemFingerprint { get; init; }

    /// <summary>Judge tokens consumed across attempts.</summary>
    public int JudgeTokensUsed { get; init; }

    /// <summary>
    /// What happened to the requested answer-sampling parameters on this question; null when none
    /// were requested.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnswerSamplingOutcome? AnswerSampling { get; init; }

    /// <summary>
    /// TypedMemEval's per-question typed outcome and evidence attribution; null for every
    /// other benchmark.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TypedMemEvalQuestionDetail? TypedOutcome { get; init; }

    /// <summary>Validated, copy-owned normalized evidence when capture is enabled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuestionEvidenceEnvelope? Evidence { get; init; }

    /// <summary>Evaluator-side evidence diagnostics when capture is enabled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuestionEvidenceDiagnostics? EvidenceDiagnostics { get; init; }

    /// <summary>Bounded AgentEval-owned failure code.</summary>
    public string? SafeFailureCode { get; init; }

    /// <summary>Judge's explanation.</summary>
    public string? JudgeExplanation { get; init; }

    /// <summary>
    /// Bounded raw judge response, carried through from the judgment when
    /// <see cref="ExternalBenchmarkOptions.JudgeEvidenceMode"/> is <see cref="JudgeEvidenceMode.Raw"/> or
    /// <see cref="ExternalBenchmarkOptions.RetainRawJudgeResponse"/> is set.
    /// </summary>
    /// <remarks>
    /// Present on the question result, not only on the judgment, because diagnosis happens against a
    /// stored run: without it, telling a judge that was WRONG from a wrapper that could not PARSE
    /// requires re-running the question.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JudgeRawResponse { get; init; }

    /// <summary>
    /// Judge reasoning recovered from its own field under
    /// <see cref="JudgeVerdictProtocol.StructuredJson"/>; null under the free-text protocol.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JudgeReasoning { get; init; }

    /// <summary>
    /// Per-predicate outcomes under <see cref="JudgeDecompositionMode.PerPredicate"/>; null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<JudgePredicateResult>? JudgePredicateResults { get; init; }

    /// <summary>
    /// The rule that combined <see cref="JudgePredicateResults"/> into <see cref="JudgeStatus"/>; null
    /// when the verdict came from a single judge call.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PredicateCombinationRule? JudgePredicateCombinationRule { get; init; }

    /// <summary>Execution time for this question.</summary>
    public TimeSpan Duration { get; init; }
}
