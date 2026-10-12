// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Captures how and at what cost an eval result was produced.</summary>
public sealed record EvalProvenance(
    string Type,
    string? JudgeModel,
    string? PromptId,
    string? PromptHash,
    int? TokensUsed,
    double EstimatedCost,
    bool CacheHit)
{
    /// <summary>Full SHA-256 of the rubric/instrument, as sha256: plus 64 hex characters, when known.</summary>
    /// <remarks>The legacy <see cref="PromptHash"/> remains a short compatibility fingerprint.</remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RubricDigest { get; init; }

    /// <summary>Number of configured panel members, when this result represents a judge panel.</summary>
    /// <remarks>The first N sub-results are panel members in definition order. Any subsequent sub-results
    /// are separately invoked adjudicators, not additional panel votes. Absent for ordinary composites.</remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? JudgePanelSize { get; init; }
}
