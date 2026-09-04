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
/// <para>
/// The same guard applies to a <c>with</c> copy: <c>score with { Value = double.NaN }</c>
/// throws exactly as the constructor would. <see cref="Threshold"/> and
/// <see cref="Confidence"/> carry the same guard on both paths when non-null.
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
    // AE-08. Value / Threshold / Confidence are declared explicitly (backing field + validating init
    // accessor) rather than left as auto-properties with a validating INITIALIZER. An initializer runs
    // on the constructor path only; a `with { Value = double.NaN }` copy invokes the init accessor
    // directly, so the auto-property let a non-finite score be manufactured by copying — every
    // consumer that trusted the ctor guard then carried a NaN it could neither compare nor serialise.
    // The record's clone copies these fields before the accessor runs, and both the field initialiser
    // (ctor path) and the accessor (copy path) now go through the same guard.
    private readonly double _value = EnsureFinite(Value, nameof(Value));
    private readonly double? _threshold = EnsureFiniteOrNull(Threshold, nameof(Threshold));
    private readonly double? _confidence = EnsureFiniteOrNull(Confidence, nameof(Confidence));

    /// <inheritdoc cref="EvalScore"/>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the value is <c>NaN</c>, <c>+Infinity</c>, or <c>-Infinity</c> — on construction
    /// and on a <c>with</c> copy alike.
    /// </exception>
    public double Value
    {
        get => _value;
        init => _value = EnsureFinite(value, nameof(Value));
    }

    /// <inheritdoc cref="EvalScore"/>
    /// <remarks>Like <see cref="Value"/>, a non-null Threshold must be finite — NaN/Infinity would
    /// serialise as invalid JSON and fail the very schema validation Value's guard prevents (GAP-11).</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a non-null value is not finite — on construction and on a <c>with</c> copy alike.
    /// </exception>
    public double? Threshold
    {
        get => _threshold;
        init => _threshold = EnsureFiniteOrNull(value, nameof(Threshold));
    }

    /// <inheritdoc cref="EvalScore"/>
    /// <remarks>A non-null Confidence must be finite for the same reason as <see cref="Threshold"/> (GAP-11).</remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when a non-null value is not finite — on construction and on a <c>with</c> copy alike.
    /// </exception>
    public double? Confidence
    {
        get => _confidence;
        init => _confidence = EnsureFiniteOrNull(value, nameof(Confidence));
    }

    private static double EnsureFinite(double value, string member)
        => double.IsFinite(value)
            ? value
            : throw new ArgumentOutOfRangeException(member, value,
                $"EvalScore.{member} must be a finite number (NaN / Infinity are rejected; downstream JSON + schema validation cannot represent them).");

    private static double? EnsureFiniteOrNull(double? value, string member)
        => value is null || double.IsFinite(value.Value)
            ? value
            : throw new ArgumentOutOfRangeException(member, value,
                $"EvalScore.{member} must be a finite number when set (NaN / Infinity are rejected).");
}
