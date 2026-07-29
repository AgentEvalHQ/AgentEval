// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Models;

namespace AgentEval.Memory.External.Models;

/// <summary>Sanitized answer-model configuration recorded for a paired LongMemEval path.</summary>
public sealed class LongMemEvalModelIdentity
{
    /// <summary>Caller-declared model or deployment identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>Optional caller-declared model version.</summary>
    public string? ModelVersion { get; init; }

    /// <summary>Optional generation temperature.</summary>
    public double? Temperature { get; init; }

    /// <summary>Optional maximum output-token setting.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Client-reported default model/deployment identifier, when observable.</summary>
    public string? ClientReportedModelId { get; init; }

    internal static LongMemEvalModelIdentity From(
        AgentBenchmarkConfig config,
        string? clientReportedModelId = null) => new()
    {
        ModelId = config.ModelId!,
        ModelVersion = config.ModelVersion,
        Temperature = config.Temperature,
        MaxTokens = config.MaxTokens,
        ClientReportedModelId = clientReportedModelId
    };
}

/// <summary>
/// Separate normal and oracle LongMemEval results over one frozen selected-question set.
/// The oracle gap is diagnostic and never participates in normal scoring.
/// </summary>
public sealed class LongMemEvalPairedResult
{
    /// <summary>Normal memory-agent result.</summary>
    public required ExternalBenchmarkResult Normal { get; init; }

    /// <summary>Retrieval-bypassing oracle-reader result.</summary>
    public required ExternalBenchmarkResult Oracle { get; init; }

    /// <summary>Question IDs selected once and used by both paths in the same order.</summary>
    public required IReadOnlyList<string> SelectedQuestionIds { get; init; }

    /// <summary>Caller-declared normal answer-model configuration.</summary>
    public required LongMemEvalModelIdentity NormalAnswerModel { get; init; }

    /// <summary>Caller-declared oracle answer-model configuration.</summary>
    public required LongMemEvalModelIdentity OracleAnswerModel { get; init; }

    /// <summary>Judge client's reported default model/deployment identifier, when available.</summary>
    public string? JudgeModelId { get; init; }

    /// <summary>
    /// Oracle accuracy minus normal accuracy in percentage points, or null when either
    /// path has no scored questions.
    /// </summary>
    public double? OracleGapPercentagePoints { get; init; }
}
