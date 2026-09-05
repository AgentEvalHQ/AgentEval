// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using AgentEval.Evals.Meta;

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
/// <para>
/// <b>ADR-030 Slice 1.1.</b> <see cref="Measurement"/> and <see cref="Passed"/> are bound by one
/// invariant — <i>a score that is not a measurement can never be <see cref="Passed"/></i> — and it
/// is guarded on the PAIR, so neither side can reach the bad state from its own direction. Use
/// <see cref="NotApplicable"/> to build an inapplicable score.
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

    // ADR-030 Slice 1.1. Declared BEFORE _passed so the constructor path reads a settled Measurement.
    // MeasurementState.Measured is default(MeasurementState) = 0, so on the constructor path the pair
    // is always (Measured, Passed) and there is nothing to validate; Measurement can only become
    // non-Measured through an object initializer or a `with`, both of which run AFTER the field
    // initialisers. That is why the constructor path assigns directly and only the copy path validates.
    //
    // The guard was placed wrongly twice before landing here, and both wrong placements are on record
    // in ADR-030 §4.2: a throwing property INITIALIZER on Measurement (does not compile — CS0236 — and
    // could never fire, because the initializer observes `default` before any object-initializer runs),
    // and a guard on EvalResult's primary constructor (bypassed by `result with { Score = ... }`, and on
    // the wrong type — both operands live here, and all five aggregation strategies read EvalScore
    // directly). This is the AE-01 / AE-08 pattern the repository has now shipped three times.
    private readonly MeasurementState _measurement = MeasurementState.Measured;
    private readonly bool _passed = Passed;

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

    /// <summary>
    /// Whether the eval decided and the thing held. Positional, so every existing call site, every
    /// deconstruction and every persisted artifact is unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when set to <see langword="true"/> on a copy whose <see cref="Measurement"/> is not
    /// <see cref="MeasurementState.Measured"/>. The primary constructor cannot reach that state —
    /// <see cref="Measurement"/> is still <c>Measured</c> while the field initialisers run — so the
    /// constructor path assigns directly and only the copy path validates.
    /// </exception>
    public bool Passed
    {
        get => _passed;
        init
        {
            EnsureDecidable(_measurement, value, nameof(Passed));
            _passed = value;
        }
    }

    /// <summary>
    /// Whether this score is a real measurement (ADR-030 §4.2). Non-positional and init-only, so
    /// positional construction and deconstruction are unaffected and the default is
    /// <see cref="MeasurementState.Measured"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not written to JSON while it is <see cref="MeasurementState.Measured"/></b>
    /// (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>). Schema v1 declares
    /// <c>additionalProperties: false</c> on <c>score</c>, so emitting this field unconditionally
    /// would invalidate every document the library writes and change every historical
    /// <c>ScenarioResult</c> content hash. Persisting it is ADR-030 Slice 1.4 — a schema v1.1 bump
    /// that ADR-030 §9 Q4 still gates. The READ path is deliberately live even so: an artifact that
    /// claims <c>passed:true</c> alongside a non-measured state is refused rather than loaded, which
    /// is the ADR-025 direction.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown when set to a non-measured state on a score whose <see cref="Passed"/> is
    /// <see langword="true"/> — on an object initializer, a <c>with</c> copy, and deserialisation
    /// alike, since all three route through this accessor.
    /// </exception>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public MeasurementState Measurement
    {
        get => _measurement;
        init
        {
            EnsureDecidable(value, _passed, nameof(Measurement));
            _measurement = value;
        }
    }

    /// <summary>
    /// The only sanctioned way to build a not-applicable score: the CASE could not test the thing.
    /// </summary>
    /// <param name="severity">Severity to carry; <c>none</c> by default.</param>
    /// <remarks>
    /// The REASON does not live here — it goes to <c>EvalDetails.Summary</c> (and
    /// <c>Recommendations</c>). A bare <c>n/a</c> with no reason is the blank cell ADR-030 §4.2's
    /// rendering rule exists to forbid.
    /// </remarks>
    public static EvalScore NotApplicable(string severity = "none") =>
        new(0.0, null, "inapplicable", false, null, severity, null)
        { Measurement = MeasurementState.NotApplicable };

    // One predicate, both accessors. The guard is on the PAIR, so it cannot be satisfied by arriving
    // at the bad state from the other side: the record's clone copies every field first and then runs
    // the `with` block's init accessors in source order, so each accessor sees the other operand's
    // already-copied value and BOTH orderings throw.
    private static void EnsureDecidable(MeasurementState measurement, bool passed, string member)
    {
        if (measurement is not MeasurementState.Measured && passed)
        {
            throw new ArgumentException(
                $"A score whose Measurement is '{measurement}' cannot be Passed. The case had " +
                "nothing to fire against, or the instrument did not run; undecidable is not a pass. " +
                $"Use {nameof(EvalScore)}.{nameof(NotApplicable)}(...) or EvalResult.Skipped(...).",
                member);   // the member the caller actually assigned, so the ParamName is testable
        }
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
