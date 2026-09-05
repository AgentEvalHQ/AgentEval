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
