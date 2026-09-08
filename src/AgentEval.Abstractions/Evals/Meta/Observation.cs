// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.Evals.Meta;

/// <summary>
/// One collapsed observation: a case, an arm, a number, and whether that number is real.
/// </summary>
/// <param name="CaseId">Stable per-case identity. A display string is not one (ADR-030 §4.7).</param>
/// <param name="ArmId">Which arm produced it — the live agent, a control, a baseline, an oracle.</param>
/// <param name="Value">The number. Meaningless unless <paramref name="State"/> is <see cref="MeasurementState.Measured"/>.</param>
/// <param name="State">Whether this is a real measurement, and if not, whose problem that is.</param>
/// <remarks>
/// <para>
/// <b>ADR-030 §4.1, the single most important design ruling in that document.</b> Chance floors,
/// controls, exact tests and rep collapse operate on this five-field tuple and nothing more. Binding
/// them to <c>EvalResult</c> was the draft's mistake: defined over a neutral tuple the whole meta
/// layer is a module that AgentEval, <c>Microsoft.Extensions.AI.Evaluation</c> and a future Python
/// port can all consume; defined over <c>EvalResult</c> it is an AgentEval internal nobody else can
/// adopt.
/// </para>
/// <para>
/// <b>The unit of analysis is the CASE, not the rep.</b> Reps of the same
/// (<paramref name="CaseId"/>, <paramref name="ArmId"/>) collapse into ONE observation before
/// anything is compared — see <see cref="RepCollapse"/>. Nothing in this namespace pairs raw reps.
/// </para>
/// <para>
/// <b>Adapters are one-way.</b> <c>EvalResult → Observation</c>, <c>MetricResult → Observation</c>
/// and <c>M.E.AI EvaluationResult → Observation</c> live in <c>AgentEval.Core</c>. There is no
/// <c>Observation → EvalResult</c>, and there must never be one: the moment a meta type can return
/// a result model, AgentEval has a seventh result model and it is the one holding pass/fail
/// authority (§4.6).
/// </para>
/// </remarks>
public readonly record struct Observation(string CaseId, string ArmId, double Value, MeasurementState State)
{
    // The AE-01 / AE-08 pattern, copied rather than reinvented: a validating INITIALIZER runs on the
    // constructor path only, and a `with` copy invokes the init ACCESSOR directly. Declaring the
    // backing field plus the accessor is what makes both paths go through the same guard. This
    // repository has now written that guard wrongly twice and correctly three times; this is the
    // third correct one.
    private readonly string _caseId = Require(CaseId, nameof(CaseId));
    private readonly string _armId = Require(ArmId, nameof(ArmId));
    private readonly double _value = RequireFiniteWhenMeasured(Value, State);

    /// <summary>The case this observation belongs to. Never empty.</summary>
    /// <exception cref="ArgumentException">Set to null, empty or whitespace.</exception>
    public string CaseId
    {
        get => _caseId;
        init => _caseId = Require(value, nameof(CaseId));
    }

    /// <summary>The arm that produced it. Never empty.</summary>
    /// <exception cref="ArgumentException">Set to null, empty or whitespace.</exception>
    public string ArmId
    {
        get => _armId;
        init => _armId = Require(value, nameof(ArmId));
    }

    /// <summary>
    /// The number. Finite whenever <see cref="State"/> is <see cref="MeasurementState.Measured"/>;
    /// otherwise it is a placeholder and is never read.
    /// </summary>
    /// <exception cref="ArgumentException">Set to a non-finite value on a MEASURED observation.</exception>
    /// <remarks>
    /// A non-finite <i>measured</i> value is refused, for the reason <c>EvalScore.Value</c> refuses
    /// one: a NaN that survives construction is averaged, compared and serialised by everything
    /// downstream, and the producer bug surfaces three layers away from where it happened.
    /// </remarks>
    public double Value
    {
        get => _value;
        init => _value = RequireFiniteWhenMeasured(value, State);
    }

    /// <summary>Whether this observation contributes to an aggregate — the one authority in this namespace.</summary>
    public bool CountsTowardAggregate => State == MeasurementState.Measured;

    /// <summary>A real measurement.</summary>
    /// <param name="caseId">Stable case identity.</param>
    /// <param name="armId">The arm.</param>
    /// <param name="value">The measured value. Must be finite.</param>
    /// <returns>A measured observation.</returns>
    public static Observation Measured(string caseId, string armId, double value) =>
        new(caseId, armId, value, MeasurementState.Measured);

    /// <summary>
    /// The CASE could not test the thing — empty gold, no distractor, a chance floor of 1.0. A
    /// CORPUS finding. <b>"The arm answered nothing" is NOT this state; that is a fail.</b>
    /// </summary>
    /// <param name="caseId">Stable case identity.</param>
    /// <param name="armId">The arm.</param>
    /// <returns>An inapplicable observation, whose value is never read.</returns>
    public static Observation NotApplicable(string caseId, string armId) =>
        new(caseId, armId, 0.0, MeasurementState.NotApplicable);

    /// <summary>
    /// The INSTRUMENT did not run — skipped, timed out, errored, budget-filtered. An OPERATIONAL
    /// finding, with a different owner and a different fix from <see cref="NotApplicable"/>.
    /// </summary>
    /// <param name="caseId">Stable case identity.</param>
    /// <param name="armId">The arm.</param>
    /// <returns>An unmeasured observation, whose value is never read.</returns>
    public static Observation NotMeasured(string caseId, string armId) =>
        new(caseId, armId, 0.0, MeasurementState.NotMeasured);

    private static string Require(string value, string member) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException(
                $"An Observation needs a non-empty {member}. Every comparison in this namespace joins on it, "
                + "and a blank key silently pools unrelated rows into one — which looks exactly like a comparison "
                + "that ran.", member)
            : value;

    private static double RequireFiniteWhenMeasured(double value, MeasurementState state) =>
        state == MeasurementState.Measured && !double.IsFinite(value)
            ? throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"A measured Observation cannot carry {value}.")
                + $" If the number is not real, say so with {nameof(MeasurementState)}.{nameof(MeasurementState.NotMeasured)}"
                + $" or {nameof(MeasurementState)}.{nameof(MeasurementState.NotApplicable)} — undecidable is not a value.",
                nameof(Value))
            : value;
}
