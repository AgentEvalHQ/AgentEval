// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.PartnerDeskDemo.Evals;

/// <summary>A measured proportion — <paramref name="Successes"/> out of <paramref name="Total"/> runs.</summary>
public readonly record struct Proportion(int Successes, int Total)
{
    /// <summary>The point estimate, or 0 when nothing was measured.</summary>
    public double Rate => Total == 0 ? 0d : (double)Successes / Total;

    /// <summary>The 95% Wilson score interval for this proportion.</summary>
    public (double Low, double High) Wilson95 => WilsonInterval.Compute(Successes, Total, WilsonInterval.Z95);

    /// <summary>Renders as <c>rate% [low, high]  (k/n)</c>.</summary>
    public string Format()
    {
        var (low, high) = Wilson95;
        return Total == 0
            ? "   n/a"
            : $"{Rate * 100,5:0.#}% [{low * 100,4:0}, {high * 100,3:0}]  ({Successes}/{Total})";
    }
}

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
