// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Meta;

/// <summary>
/// Whether a number is a real measurement, and if it is not, whose problem that is.
/// </summary>
/// <remarks>
/// <para>
/// ADR-030 §4.2. The draft of that design proposed <c>bool? Applicable</c> and it was rejected:
/// a tri-state <c>bool?</c> is the silent-<c>{}</c> shape — <see langword="null"/> reads as
/// "nobody set it" and the first consumer writes <c>score.Applicable ?? true</c>. The default has
/// to be a real, named value, and it has to be the one every existing call site already means.
/// </para>
/// <para>
/// <b>The namespace is the contract, not the assembly.</b> Everything in
/// <c>AgentEval.Evals.Meta</c> is BCL-only and references nothing from AgentEval, so the whole
/// meta layer stays testable without an eval tree and portable to any framework that can produce
/// a tuple (ADR-030 §4.1). It currently lives inside <c>AgentEval.Abstractions</c> — itself
/// BCL-only, zero <c>PackageReference</c> — because ADR-030 §9 Q2 (a separate <c>AgentEval.Meta</c>
/// project at the bottom of the dependency graph) is still open. Moving these files to that project
/// later changes no namespace and therefore no consumer source.
/// </para>
/// </remarks>
public enum MeasurementState
{
    /// <summary>
    /// A real measurement. <b>Default</b> — <c>default(MeasurementState)</c> is this value, so
    /// every existing call site keeps the meaning it already had and nothing needs migrating.
    /// </summary>
    Measured = 0,

    /// <summary>
    /// The CASE could not test the thing: empty gold, no distractor, no tool definitions supplied,
    /// a chance floor of 1.0. A CORPUS/DESIGN finding — fix the cases.
    /// <para>
    /// Never a pass and never a zero. Excluded from means, counted in its own column.
    /// <b>"The agent answered nothing" is NOT this state — that is a FAIL.</b>
    /// Applicability is a property of the CASE, never of the ANSWER.
    /// </para>
    /// </summary>
    NotApplicable = 1,

    /// <summary>
    /// The INSTRUMENT did not run: skipped, timed out, errored, budget-filtered. An OPERATIONAL
    /// finding — fix the run. Distinct from <see cref="NotApplicable"/> because they have different
    /// owners and different fixes; pooling them hides which one you have.
    /// </summary>
    NotMeasured = 2,
}
