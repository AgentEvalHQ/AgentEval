// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// What a run actually contained, counted from the questions that were run rather than from the
/// options that were requested.
/// </summary>
/// <remarks>
/// <para>
/// Every count here is derived from <see cref="ExternalBenchmarkResult.QuestionResults"/>, the same
/// list the accuracy denominators are derived from. That is deliberate: a composition computed from
/// the requested options could claim a sample the run never drew, and a downstream denominator
/// computed independently could disagree with AgentEval's. Both are computed from one list, so
/// neither can drift from the other.
/// </para>
/// <para>
/// The requested configuration is echoed alongside the realised counts so the two can be compared.
/// They will differ whenever the pool could not satisfy the request — asking for a 50% abstention
/// share of 100 questions when only 30 abstention questions exist yields 30, and that shortfall is
/// visible here rather than silently topped up with ordinary questions.
/// </para>
/// </remarks>
public sealed class SampleComposition
{
    /// <summary>Questions actually run.</summary>
    public required int TotalQuestions { get; init; }

    /// <summary>Abstention questions actually run.</summary>
    public required int AbstentionQuestions { get; init; }

    /// <summary>Non-abstention questions actually run.</summary>
    public required int NonAbstentionQuestions { get; init; }

    /// <summary>Realised counts per question type, including the abstention split within each type.</summary>
    public required IReadOnlyDictionary<string, QuestionTypeComposition> ByQuestionType { get; init; }

    /// <summary>
    /// The question types the caller asked for, or null when no filter was applied (all types).
    /// Echoed from the options so a stored result records the request as well as the outcome.
    /// </summary>
    public IReadOnlyList<string>? RequestedQuestionTypes { get; init; }

    /// <summary>The abstention policy the caller asked for.</summary>
    public required AbstentionSamplingPolicy RequestedAbstentionPolicy { get; init; }

    /// <summary>The abstention share the caller asked for; null unless the policy was TargetProportion.</summary>
    public double? RequestedAbstentionProportion { get; init; }

    /// <summary>
    /// Realised abstention share of the run, or null when nothing ran — an empty run has no
    /// proportion, and reporting 0.0 would state a measurement that was never made.
    /// </summary>
    public double? RealisedAbstentionProportion => TotalQuestions == 0
        ? null
        : (double)AbstentionQuestions / TotalQuestions;
}

/// <summary>Realised counts for one question type.</summary>
public sealed class QuestionTypeComposition
{
    /// <summary>Questions of this type actually run.</summary>
    public required int TotalQuestions { get; init; }

    /// <summary>Of those, how many were abstention questions.</summary>
    public required int AbstentionQuestions { get; init; }

    /// <summary>Of those, how many were not abstention questions.</summary>
    public int NonAbstentionQuestions => TotalQuestions - AbstentionQuestions;
}
