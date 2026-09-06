// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.Evals.Meta;

/// <summary>
/// What went into a number. A mean over 3 of 12 and a mean over 12 of 12 are different facts and
/// must not render identically (ADR-030 §4.2, Slice 1.3).
/// </summary>
/// <param name="Measured">Real measurements — the denominator of the mean.</param>
/// <param name="NotApplicable">Cases that could not test the thing. A CORPUS finding.</param>
/// <param name="NotMeasured">Runs where the instrument did not run. An OPERATIONAL finding.</param>
/// <remarks>
/// BCL-only, like everything in this namespace. It never sees an <c>EvalScore</c>, an
/// <c>EvalResult</c> or any AgentEval type.
/// </remarks>
public sealed record ObservationCensus(int Measured, int NotApplicable, int NotMeasured)
{
    /// <summary>Every observation, of whatever kind.</summary>
    public int Total => Measured + NotApplicable + NotMeasured;

    /// <summary>
    /// Nothing was measurable. The aggregate is VOID — <b>not perfect, not zero.</b> A mean over an
    /// empty denominator is the silent-<c>{}</c> shape: a green (or red) verdict from an instrument
    /// that measured nothing.
    /// </summary>
    public bool Void => Measured == 0 && Total > 0;

    /// <summary>
    /// Extreme values are wiring faults until proven otherwise, in BOTH directions.
    /// <c>NotApplicable == 0</c> across a suite is as suspicious as <c>== Total</c>: an
    /// inapplicability ledger that reads clean is usually a ledger nothing writes to.
    /// </summary>
    public bool ExtremeAndUnexamined => Total > 0 && (NotApplicable == 0 || NotApplicable == Total);

    /// <summary>
    /// The fraction of observations that are REAL MEASUREMENTS — <c>Measured / Total</c>.
    /// <see cref="double.NaN"/> when there are no observations at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>THE DENOMINATOR IS <see cref="Total"/>, AND THE ALTERNATIVE IS THE DEFECT THIS WHOLE
    /// TYPE EXISTS TO PREVENT.</b> The tempting form is
    /// <c>(Total − NotApplicable) / Total</c> — "the fraction we could have measured" — and it
    /// POOLS <see cref="NotApplicable"/> with <see cref="NotMeasured"/>. Those are different
    /// findings with different owners: a case that could not test the thing is a CORPUS finding,
    /// and a run where the instrument did not run is an OPERATIONAL one. A number that cannot tell
    /// them apart reports a broken harness as a well-scoped corpus, which is the flattering
    /// direction (ADR-030 §4.2; ADR-031 §0.1 states the rule for <c>stats.applicableFraction</c>).
    /// </para>
    /// <para>
    /// The two forms coincide exactly when <see cref="NotMeasured"/> is zero, which is why a
    /// consumer that has never had an instrument fail cannot tell that it picked the wrong one.
    /// </para>
    /// </remarks>
    public double MeasuredFraction => Total == 0 ? double.NaN : Measured / (double)Total;

    /// <summary>
    /// The form this type <b>refuses</b>: <c>(Total − NotApplicable) / Total</c>, exposed only so a
    /// caller can demonstrate that it differs from <see cref="MeasuredFraction"/>.
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Never report this.</b> It exists for one purpose: a control that asserts the shipped
    /// denominator is not this one needs both numbers, and a control that recomputes the forbidden
    /// form itself would drift from the definition it is checking against. It is documented as
    /// forbidden at the point where it is defined, so nobody can adopt it by accident.
    /// </remarks>
    public double PooledFractionDoNotReport =>
        Total == 0 ? double.NaN : (Total - NotApplicable) / (double)Total;

    /// <summary>
    /// Whether this census carries at least <paramref name="minimumApplicable"/> real measurements
    /// — ADR-031 S2b's <c>minApplicable</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ Counted against <see cref="Measured"/>, never against <c>Total − NotApplicable</c>. A run
    /// whose instrument silently failed on four cases must not satisfy a minimum by counting those
    /// four as "applicable".
    /// </remarks>
    /// <param name="minimumApplicable">The floor, in real measurements. Negative values are rejected.</param>
    /// <returns>True when <see cref="Measured"/> reaches the floor.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumApplicable"/> is negative.</exception>
    public bool MeetsMinimumApplicable(int minimumApplicable)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumApplicable);
        return Measured >= minimumApplicable;
    }

    /// <summary>
    /// The denominator, rendered. <c>8 of 12 measured, 3 n/a, 1 not measured</c> — the zero buckets
    /// are omitted, but "measured" and its total never are.
    /// </summary>
    /// <returns>The denominator phrase, without surrounding parentheses.</returns>
    public string Describe()
    {
        var parts = new List<string>(3)
        {
            string.Create(CultureInfo.InvariantCulture, $"{Measured} of {Total} measured"),
        };
        if (NotApplicable > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{NotApplicable} n/a"));
        if (NotMeasured > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{NotMeasured} not measured"));
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Renders a mean <b>with</b> its denominator — <c>0.62 (8 of 12 measured, 3 n/a, 1 not
    /// measured)</c> — or <c>VOID — nothing measurable</c> when <see cref="Void"/>.
    /// </summary>
    /// <param name="mean">
    /// The mean over the <see cref="Measured"/> observations. Ignored when <see cref="Void"/>, so a
    /// caller cannot smuggle a placeholder <c>0.0</c> past the void case.
    /// </param>
    /// <param name="format">Numeric format for the mean; two decimals by default.</param>
    /// <returns>The renderable string. Never a bare number.</returns>
    /// <remarks>
    /// <b>A bare <c>0.62</c> is not renderable</b> and neither is a <c>0.00</c> that stands for
    /// "nothing ran". This method is the library's answer to both; ADR-030 §6.3 records that the
    /// library ships no console renderer, so every consumer would otherwise re-implement the rule
    /// and half would forget.
    /// </remarks>
    public string RenderMean(double mean, string format = "F2")
    {
        if (Total == 0) return "VOID — no observations";
        if (Void) return "VOID — nothing measurable";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{mean.ToString(format, CultureInfo.InvariantCulture)} ({Describe()})");
    }
}
