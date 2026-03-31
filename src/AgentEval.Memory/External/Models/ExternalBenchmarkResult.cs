// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

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

    /// <summary>Overall accuracy: correct / total * 100 (micro-average).</summary>
    public required double OverallAccuracy { get; init; }

    /// <summary>Task-averaged accuracy: mean of per-type accuracies (macro-average).</summary>
    public required double TaskAveragedAccuracy { get; init; }

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
    /// <summary>Question type name (e.g., "temporal-reasoning").</summary>
    public required string TypeName { get; init; }

    /// <summary>Total questions of this type.</summary>
    public required int TotalQuestions { get; init; }

    /// <summary>Number answered correctly.</summary>
    public required int CorrectQuestions { get; init; }

    /// <summary>Accuracy as percentage (0-100).</summary>
    public double Accuracy => TotalQuestions > 0 ? (double)CorrectQuestions / TotalQuestions * 100 : 0;

    /// <summary>Execution time for this type's questions.</summary>
    public required TimeSpan Duration { get; init; }
}

/// <summary>
/// Result for a single question within an external benchmark.
/// </summary>
public class QuestionResult
{
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

    /// <summary>Binary judgment: correct or not.</summary>
    public required bool Correct { get; init; }

    /// <summary>Raw score from judge (0-100), kept for granular analysis.</summary>
    public required double RawScore { get; init; }

    /// <summary>Judge's explanation.</summary>
    public string? JudgeExplanation { get; init; }

    /// <summary>Execution time for this question.</summary>
    public TimeSpan Duration { get; init; }
}
