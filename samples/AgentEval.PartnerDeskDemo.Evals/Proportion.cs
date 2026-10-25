// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.PartnerDeskDemo.Evals;

/// <summary>A measured proportion — <paramref name="Successes"/> out of <paramref name="Total"/> observations.</summary>
public readonly record struct Proportion(int Successes, int Total)
{
    /// <summary>The point estimate, or null when no observations were measured.</summary>
    public double? Rate => Total == 0 ? null : (double)Successes / Total;

    /// <summary>The 95% Wilson interval, or null when no observations were measured.</summary>
    public (double Low, double High)? Wilson95 =>
        Total == 0 ? null : WilsonInterval.Compute(Successes, Total, WilsonInterval.Z95);

    /// <summary>Renders a measured rate and its denominator, or n/a when unmeasured.</summary>
    public string Format()
    {
        if (Wilson95 is not { } interval)
            return "   n/a";
        return $"{Rate * 100,5:0.#}% [{interval.Low * 100,4:0}, {interval.High * 100,3:0}]  ({Successes}/{Total})";
    }
}
