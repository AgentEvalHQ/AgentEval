// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.PartnerDeskDemo.Evals;

/// <summary>
/// The Wilson score interval for a binomial proportion.
/// </summary>
/// <remarks>
/// Preferred over the normal (Wald) approximation because a live susceptibility eval routinely lands at extreme
/// rates (0% or 100%) and small sample sizes, where Wald produces nonsense intervals — a width of zero at the
/// boundary, or bounds outside [0, 1]. Wilson stays inside [0, 1] and gives a sensible non-zero width even at
/// 0/n or n/n, which is exactly the regime the gate-controlled arms live in.
/// </remarks>
public static class WilsonInterval
{
    /// <summary>The standard-normal quantile for a 95% two-sided interval.</summary>
    public const double Z95 = 1.959963984540054d;

    /// <summary>Computes the Wilson interval for <paramref name="successes"/> of <paramref name="total"/>.</summary>
    public static (double Low, double High) Compute(int successes, int total, double z)
    {
        if (total <= 0)
        {
            return (0d, 0d);
        }

        var p = (double)successes / total;
        var z2 = z * z;
        var denominator = 1d + (z2 / total);
        var center = (p + (z2 / (2d * total))) / denominator;
        var margin = (z / denominator)
            * Math.Sqrt((p * (1d - p) / total) + (z2 / (4d * total * total)));
        return (Math.Max(0d, center - margin), Math.Min(1d, center + margin));
    }
}
