// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Captures how and at what cost an eval result was produced.</summary>
/// <param name="Type">The kind of producer, e.g. <c>"atomic-llm"</c>, <c>"atomic-code"</c> or <c>"composite"</c>.</param>
/// <param name="JudgeModel">The judge model, when a model graded this result.</param>
/// <param name="PromptId">The judge prompt's id, when known.</param>
/// <param name="PromptHash">A short fingerprint of the instrument that was sent.</param>
/// <param name="TokensUsed">Judge tokens spent, when reported.</param>
/// <param name="EstimatedCost">
/// Estimated cost in USD. <see langword="null"/> means not priced: a model call whose usage was not reported, a model
/// with no known rate, or a total with an unpriced part. Not priced is never written as 0, so 0 is a real cost of
/// nothing (a deterministic check, a skipped leaf). Always written to JSON, as <c>null</c> when not priced, whatever
/// the serializer's null handling: the eval-result schema requires the key.
/// </param>
/// <param name="CacheHit">Whether the result came from a cache.</param>
public sealed record EvalProvenance(
    string Type,
    string? JudgeModel,
    string? PromptId,
    string? PromptHash,
    int? TokensUsed,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    double? EstimatedCost,
    bool CacheHit)
{
    /// <summary>Full SHA-256 of the rubric/instrument, as sha256: plus 64 hex characters, when known.</summary>
    /// <remarks>The legacy <see cref="PromptHash"/> remains a short compatibility fingerprint.</remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? RubricDigest { get; init; }

    /// <summary>The configured judge provider, when known; never inferred from a model name.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? JudgeProvider { get; init; }

    /// <summary>Number of configured panel members, when this result represents a judge panel.</summary>
    /// <remarks>The first N sub-results are panel members in definition order. Any subsequent sub-results
    /// are separately invoked adjudicators, not additional panel votes. Absent for ordinary composites.</remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? JudgePanelSize { get; init; }
}
