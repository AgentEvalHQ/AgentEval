// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.RedTeam.Reporting;

/// <summary>
/// A Wilson score interval for a binomial proportion, and the proportion itself.
/// </summary>
/// <param name="Successes">Number of successes observed.</param>
/// <param name="Total">Number of trials.</param>
/// <param name="Estimate">The point estimate (<c>Successes / Total</c>), or 0 when <paramref name="Total"/> is 0.</param>
/// <param name="Lower">Lower bound of the interval.</param>
/// <param name="Upper">Upper bound of the interval.</param>
public readonly record struct WilsonInterval(int Successes, int Total, double Estimate, double Lower, double Upper)
{
    /// <summary>
    /// Computes the Wilson score interval at the given confidence level (default 95%).
    /// </summary>
    /// <remarks>
    /// <para><b>Why Wilson and not a bare percentage.</b> A point estimate destroys sample-size information
    /// exactly where it matters most: "0% of homoglyph attacks got through" reads as a clean result whether it
    /// came from 8 probes or 800. Wilson keeps the honesty visible — 0/8 is <c>0.0 [0.0, 0.324]</c>, which
    /// says plainly that the true rate could be a third. It is the interval used by the reference
    /// prompt-injection benchmark we validate against, so per-class numbers are directly comparable.</para>
    ///
    /// <para>Wilson is preferred over the normal (Wald) approximation because Wald degenerates to a
    /// zero-width interval at 0 and 1 — precisely the values a per-class recall table is full of.</para>
    /// </remarks>
    /// <param name="successes">Number of successes. Must be ≥ 0 and ≤ <paramref name="total"/>.</param>
    /// <param name="total">Number of trials. Must be ≥ 0.</param>
    /// <param name="z">
    /// The standard-normal critical value. Defaults to 1.959963984540054 (95%), the value the reference
    /// implementation uses.
    /// </param>
    public static WilsonInterval Compute(int successes, int total, double z = 1.959963984540054)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (successes > total)
        {
            throw new ArgumentOutOfRangeException(
                nameof(successes), successes, $"successes ({successes}) cannot exceed total ({total}).");
        }

        // An empty sample has no interval to report. Returning a zero-width interval at 0 would claim
        // certainty we do not have, so the caller must treat Total == 0 as "not measured", not as "0%".
        if (total == 0)
        {
            return new WilsonInterval(0, 0, 0d, 0d, 0d);
        }

        var n = (double)total;
        var p = successes / n;
        var z2 = z * z;

        var denominator = 1d + (z2 / n);
        var centre = p + (z2 / (2d * n));
        var margin = z * Math.Sqrt((p * (1d - p) / n) + (z2 / (4d * n * n)));

        var lower = (centre - margin) / denominator;
        var upper = (centre + margin) / denominator;

        return new WilsonInterval(
            successes,
            total,
            p,
            Math.Clamp(lower, 0d, 1d),
            Math.Clamp(upper, 0d, 1d));
    }

    /// <summary>
    /// Whether this interval rests on any observations at all. A <see langword="false"/> value means the class
    /// was never exercised — a coverage gap, which must not be rendered as a 0% result.
    /// </summary>
    public bool IsMeasured => Total > 0;

    /// <summary>Renders as <c>estimate [lower, upper] (k/n)</c>, or an explicit not-measured marker.</summary>
    public override string ToString() =>
        IsMeasured
            ? $"{Estimate:P1} [{Lower:P1}, {Upper:P1}] ({Successes}/{Total})"
            : "not measured (0 probes)";
}
