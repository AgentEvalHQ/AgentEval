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

    /// <summary>Number of provider calls attempted, including retries.</summary>
    public int AttemptCount => LlmCallCount;

    /// <summary>Bounded AgentEval-owned failure code; never provider exception text.</summary>
    public string? SafeFailureCode { get; init; }

    /// <summary>Bounded raw response, emitted only when JudgeEvidenceMode.Raw is enabled.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RawResponse { get; init; }
}
