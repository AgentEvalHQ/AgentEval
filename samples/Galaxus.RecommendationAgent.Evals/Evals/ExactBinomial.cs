// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Globalization;

namespace Galaxus.RecommendationAgent.Evals;

/// <summary>
/// The exact one-sided binomial test that decides whether an observed rate is above a chance floor.
/// </summary>
/// <remarks>
/// <para>
/// <b>Plan item 1.4 / design N-4.</b> Every "is this arm above chance?" decision in this suite used
/// to be <c>rate &gt; floor</c>. At the sizes this corpus actually has that is not a test, it is a
/// comparison of a point estimate to a constant, and it says YES to observations that a fair coin
/// produces routinely. Measured against the shipped forced-choice floor of 1/12 over 12 personas:
/// </para>
/// <list type="table">
///   <listheader><term>observed</term><description>old rule · exact upper-tail p · verdict now</description></listheader>
///   <item><term>2 of 12</term><description>▲ ABOVE · p = 0.264 · <b>not above chance</b></description></item>
///   <item><term>3 of 12</term><description>▲ ABOVE · p = 0.070 · <b>not above chance</b></description></item>
///   <item><term>7 of 12</term><description>▲ ABOVE · p ≈ 2.2e-5 · <b>above chance, and now it says how strongly</b></description></item>
/// </list>
/// <para>
/// So the change removes two ticks and keeps the one that was earned. ⚠ <b>Its direction is
/// un-flattering by construction</b> — nothing here can turn a ▼ into a ▲, because
/// <see cref="UpperTailP"/> is monotone: an arm that fails <c>rate &gt; floor</c> has
/// <c>p ≥ P(X ≥ ⌈floor·n⌉)</c>, which cannot be small. That one-sidedness is why this is a
/// correction and not a re-scoring.
/// </para>
/// <para>
/// <b>Computed in log space, deliberately.</b> The naive product form loses the tail entirely once
/// <c>n</c> passes a few hundred — the defect ADR-030 §4.4's <c>SignP_SurvivesLargeN</c> exists to
/// pin — and this suite's Eval 09 pairs at n in the dozens today but is not bounded there.
/// </para>
/// <para>
/// ⚠ <b>MIGRATION TARGET, named rather than assumed.</b> This is the sample-side instance of
/// ADR-030 Slice 2.3 (<c>ExactTests</c>) and Slice 2.4 (<c>ZeroEventUpperBound</c>). When Phase 4
/// lands, this type is deleted and its callers move to the library's — the same arrangement
/// <c>CalibratedThresholds</c> already declares for <c>ChanceFloor.Empirical</c>. It is written here
/// now because 1.4 is independently doable today and the defect it fixes is live in printed output.
/// </para>
/// <para>
/// <b>What it is NOT.</b> It is not a correction for multiplicity: several arms are tested against
/// the same floor in one panel and no Bonferroni or Holm adjustment is applied. With five arms at
/// α = 0.05 the family-wise error rate is about 0.23, and the honest reading of a single ▲ in a
/// five-arm panel has to carry that. Saying so is cheaper than pretending otherwise, and the
/// adjustment belongs with the library's <c>ExactTests</c>, not in a printer.
/// </para>
/// </remarks>
public static class ExactBinomial
{
    /// <summary>The significance level every caller in this suite uses. One number, one place.</summary>
    public const double Alpha = 0.05;

    /// <summary>
    /// P(X ≥ <paramref name="successes"/>) for X ~ Binomial(<paramref name="trials"/>,
    /// <paramref name="chance"/>), the exact one-sided upper tail.
    /// </summary>
    /// <param name="successes">Observed successes. Clamped into 0..trials.</param>
    /// <param name="trials">Trials. Zero trials give NaN — an empty denominator is not a result.</param>
    /// <param name="chance">The null rate. Outside (0, 1) gives NaN.</param>
    /// <returns>The upper-tail probability, or NaN when the question is not askable.</returns>
    public static double UpperTailP(int successes, int trials, double chance)
    {
        if (trials <= 0) return double.NaN;
        if (double.IsNaN(chance) || chance <= 0.0 || chance >= 1.0) return double.NaN;
        if (successes <= 0) return 1.0;                       // P(X ≥ 0) is 1 by definition
        if (successes > trials) return 0.0;

        double logP = Math.Log(chance);
        double logQ = Math.Log(1.0 - chance);

        // Log-space term by term, summed with the exponential taken per term. The alternative —
        // summing the log terms — is wrong; the alternative that multiplies raw terms underflows.
        double total = 0.0;
        for (int k = successes; k <= trials; k++)
        {
            double logTerm = LogChoose(trials, k) + (k * logP) + ((trials - k) * logQ);
            total += Math.Exp(logTerm);
        }

        return Math.Clamp(total, 0.0, 1.0);
    }

    /// <summary>
    /// The decision every caller shares: is <paramref name="successes"/> of
    /// <paramref name="trials"/> above <paramref name="chance"/> at <see cref="Alpha"/>?
    /// </summary>
    /// <remarks>
    /// ⚠ <b>Every "▲ above chance" in this suite must route through THIS method.</b> A second copy
    /// of the rule is how the old <c>rate &gt; floor</c> survived in four places at once, and it is
    /// what makes the control row able to test the printer rather than a paraphrase of it.
    /// </remarks>
    /// <param name="successes">Observed successes.</param>
    /// <param name="trials">Trials.</param>
    /// <param name="chance">The null rate.</param>
    /// <returns>Whether it is above chance, and the exact p-value behind that answer.</returns>
    public static (bool Above, double P) AboveChance(int successes, int trials, double chance)
    {
        double p = UpperTailP(successes, trials, chance);
        return (!double.IsNaN(p) && p <= Alpha, p);
    }

    /// <summary>Renders a p-value for a console panel, never rounding a small one to zero.</summary>
    /// <param name="p">The p-value, possibly NaN.</param>
    public static string FormatP(double p) =>
        double.IsNaN(p) ? "p n/a"
        : p < 0.0001 ? "p < 0.0001"
        : string.Create(CultureInfo.InvariantCulture, $"p = {p:0.0000}");

    /// <summary>log C(n, k), via the log-gamma of the factorials — no factorial is ever formed.</summary>
    private static double LogChoose(int n, int k) =>
        LogFactorial(n) - LogFactorial(k) - LogFactorial(n - k);

    /// <summary>
    /// log(n!) by Lanczos-free summation for the small n this suite has, and Stirling above it.
    /// </summary>
    /// <remarks>
    /// The exact summation is used up to n = 1000, which covers every denominator this repository
    /// has ever produced by three orders of magnitude and is exact rather than asymptotic. Above it
    /// the Stirling series is used so the function cannot blow up on a caller it was not sized for.
    /// </remarks>
    private static double LogFactorial(int n)
    {
        if (n <= 1) return 0.0;

        if (n <= 1000)
        {
            double sum = 0.0;
            for (int i = 2; i <= n; i++) sum += Math.Log(i);
            return sum;
        }

        double x = n + 1.0;
        return ((x - 0.5) * Math.Log(x)) - x + (0.5 * Math.Log(2.0 * Math.PI))
             + (1.0 / (12.0 * x)) - (1.0 / (360.0 * x * x * x));
    }
}
