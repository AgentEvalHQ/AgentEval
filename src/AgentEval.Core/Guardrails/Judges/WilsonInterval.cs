// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails.Judges;

/// <summary>
/// A Wilson score interval for a binomial proportion. Used by <see cref="CalibrationReport"/> to give
/// honest uncertainty around <see cref="CalibrationReport.DecisiveAccuracy"/> and
/// <see cref="CalibrationReport.FalsePositiveRate"/>: a point estimate destroys sample-size information
/// exactly where it matters most ("0 missed attacks on 8 probes" is very different from
/// "0 missed attacks on 800 probes"). Wilson keeps that visible.
/// </summary>
/// <param name="Successes">Number of successes observed.</param>
/// <param name="Total">Number of trials.</param>
/// <param name="Estimate">The point estimate (<c>Successes / Total</c>), or 0 when <paramref name="Total"/> is 0.</param>
/// <param name="Lower">Lower bound of the 95% interval, or 0 when not measured.</param>
/// <param name="Upper">Upper bound of the 95% interval, or 0 when not measured.</param>
public readonly record struct WilsonInterval(int Successes, int Total, double Estimate, double Lower, double Upper)
{
    /// <summary>
    /// Computes the Wilson score interval at the given confidence level (default 95 %).
    /// </summary>
    /// <remarks>
    /// Wilson is preferred over the normal (Wald) approximation because Wald degenerates to a
    /// zero-width interval at 0 and 1 — precisely the values a per-axis recall table is full of.
    /// </remarks>
    /// <param name="successes">Number of successes. Must be ≥ 0 and ≤ <paramref name="total"/>.</param>
    /// <param name="total">Number of trials. Must be ≥ 0.</param>
    /// <param name="z">Standard-normal critical value. Must be finite and positive. Default 1.959963984540054 (95 %).</param>
    public static WilsonInterval Compute(int successes, int total, double z = 1.959963984540054)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(successes);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        if (successes > total)
        {
            throw new ArgumentOutOfRangeException(
                nameof(successes), successes, $"successes ({successes}) cannot exceed total ({total}).");
        }

        // A negative z swaps the bounds; NaN or an infinity makes them NaN.
        if (!double.IsFinite(z) || z <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(z), z, "z must be finite and positive.");
        }

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

        return new WilsonInterval(
            successes,
            total,
            p,
            Math.Clamp((centre - margin) / denominator, 0d, 1d),
            Math.Clamp((centre + margin) / denominator, 0d, 1d));
    }

    /// <summary>
    /// Whether this interval rests on any observations at all.
    /// <see langword="false"/> means the class was never exercised — a coverage gap, not a 0 % result.
    /// </summary>
    public bool IsMeasured => Total > 0;

    /// <summary>Renders as <c>estimate [lower, upper] (k/n)</c>, or an explicit not-measured marker.</summary>
    public override string ToString() =>
        IsMeasured
            ? $"{Estimate:P1} [{Lower:P1}, {Upper:P1}] ({Successes}/{Total})"
            : "not measured (0 cases)";
}
