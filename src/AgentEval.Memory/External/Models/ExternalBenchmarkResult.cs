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

    /// <summary>Estimated cost in USD.</summary>
    public double? EstimatedCostUsd { get; init; }

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

    /// <summary>Agent provider calls attempted for this question.</summary>
    public int AgentLlmCallCount { get; init; }

    /// <summary>Judge provider calls attempted for this question, including retries.</summary>
    public int JudgeLlmCallCount { get; init; }

    /// <summary>Judge tokens consumed across attempts.</summary>
    public int JudgeTokensUsed { get; init; }

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
