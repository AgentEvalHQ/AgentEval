// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Normalised score produced by an eval, including pass/fail disposition and severity.</summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> rejects <c>NaN</c>, <c>+Infinity</c>, and <c>-Infinity</c> at
/// construction. The v1 schema (<c>eval-result.schema.json</c>) constrains the score
/// to <c>type: number, minimum: 0, maximum: 1</c>; non-finite floats serialise as
/// the JSON literal <c>"NaN"</c> / <c>"Infinity"</c> which would fail validation
/// downstream. Rejecting at the ctor surfaces the producer bug at the source rather
/// than at the persistence layer.
/// </para>
/// </remarks>
public sealed record EvalScore(
    double Value,
    int? Ordinal,
    string Label,
    bool Passed,
    double? Threshold,
    string Severity,
    double? Confidence)
{
    /// <inheritdoc cref="EvalScore"/>
    public double Value { get; init; } = double.IsFinite(Value)
        ? Value
        : throw new ArgumentOutOfRangeException(nameof(Value), Value,
            "EvalScore.Value must be a finite number (NaN / Infinity are rejected; downstream JSON + schema validation cannot represent them).");

    /// <inheritdoc cref="EvalScore"/>
    /// <remarks>Like <see cref="Value"/>, a non-null Threshold must be finite — NaN/Infinity would
    /// serialise as invalid JSON and fail the very schema validation Value's guard prevents (GAP-11).</remarks>
    public double? Threshold { get; init; } = Threshold is null || double.IsFinite(Threshold.Value)
        ? Threshold
        : throw new ArgumentOutOfRangeException(nameof(Threshold), Threshold,
            "EvalScore.Threshold must be a finite number when set (NaN / Infinity are rejected).");

    /// <inheritdoc cref="EvalScore"/>
    /// <remarks>A non-null Confidence must be finite for the same reason as <see cref="Threshold"/> (GAP-11).</remarks>
    public double? Confidence { get; init; } = Confidence is null || double.IsFinite(Confidence.Value)
        ? Confidence
        : throw new ArgumentOutOfRangeException(nameof(Confidence), Confidence,
            "EvalScore.Confidence must be a finite number when set (NaN / Infinity are rejected).");
}
