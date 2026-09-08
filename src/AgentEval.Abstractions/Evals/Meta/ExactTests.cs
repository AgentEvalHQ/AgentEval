// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;

namespace AgentEval.Evals.Meta;

/// <summary>
/// The one-sided upper bound a run of <paramref name="Trials"/> supports, and whether the rule
/// that produces it even applies.
/// </summary>
/// <param name="IsApplicable">False when the rule was asked for outside the only place it holds.</param>
/// <param name="UpperBound">The bound, when applicable. Null otherwise — never a number that is not one.</param>
/// <param name="ObservedRateInterval">The exact interval around the observed rate, supplied instead of a bound.</param>
/// <param name="Reason">Why. Never empty, and it prints where the bound would have.</param>
/// <param name="Events">The event count the caller passed.</param>
/// <param name="Trials">The trial count the caller passed.</param>
public sealed record ZeroEventBound(
    bool IsApplicable,
    double? UpperBound,
    (double Low, double High)? ObservedRateInterval,
    string Reason,
    int Events,
    int Trials);

/// <summary>
/// Exact tests. Five pure static functions, deterministic across machines, BCL-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-030 §4.4 and Slice 2.3/2.4.</b> Everything here is exact — no normal approximation, no
/// bootstrap, no seed. At the n an eval suite actually works at, the exact form is both cheaper and
/// correct, and it has no state to record.
/// </para>
/// <para>
/// <b>Everything is accumulated in LOG space, deliberately.</b> The naive form divides a sum of
/// binomial coefficients by <c>Math.Pow(2, n)</c>, which is <c>+Infinity</c> past n ≈ 1030 and
/// returns <c>NaN</c> — silently, and in the direction of "no result". This repository shipped three
/// independent binomial-coefficient implementations before this type existed.
/// </para>
/// </remarks>
public static class ExactTests
{
    /// <summary>The significance level this namespace uses when a caller does not name one.</summary>
    public const double DefaultAlpha = 0.05;

    /// <summary>
    /// Exact two-sided binomial p at p = 0.5: <c>2 × P(X ≥ max(wins, n − wins))</c>, clamped to 1.
    /// </summary>
    /// <param name="wins">Wins among the non-tied pairs.</param>
    /// <param name="nonTied">The non-tied pair count — the n the test actually runs on.</param>
    /// <returns>The two-sided p-value. <c>1.0</c> when <paramref name="nonTied"/> is 0.</returns>
    /// <remarks>
    /// Returns <c>1.0</c> when every case tied. <b>Every case tied is "no detectable difference",
    /// never a win</b> — and a comparison that refused every pair is the same fact, which is why
    /// <see cref="PairedComparison.UnderpoweredByConstruction"/> travels beside this number
    /// everywhere it is rendered.
    /// </remarks>
    public static double TwoSidedSignP(int wins, int nonTied)
    {
        if (nonTied <= 0) return 1.0;

        int w = Math.Clamp(wins, 0, nonTied);
        int extreme = Math.Max(w, nonTied - w);

        double logHalfN = -nonTied * Math.Log(2.0);
        double tail = 0.0;
        for (int k = extreme; k <= nonTied; k++)
        {
            tail += Math.Exp(LogChoose(nonTied, k) + logHalfN);
        }

        return Math.Clamp(2.0 * tail, 0.0, 1.0);
    }

    /// <summary>
    /// The smallest two-sided p this n could EVER produce: <c>min(1, 2 × 0.5^n)</c>.
    /// </summary>
    /// <param name="nonTied">The NON-TIED count — the n the exact test runs on.</param>
    /// <returns>The minimum attainable two-sided p.</returns>
    /// <remarks>
    /// Computed from the non-tied count, never from the full paired count: discarding ties costs
    /// power, so the full count understates the floor and makes an unreachable α look reachable.
    /// A comparison whose minimum attainable p exceeds α could not have reached significance <i>even
    /// if the challenger had won every informative pair</i> — that is a property of the DESIGN, and
    /// it must be printed beside the p-value rather than discovered afterwards.
    /// </remarks>
    public static double MinimumAttainableP(int nonTied) =>
        nonTied <= 0 ? 1.0 : Math.Min(1.0, 2.0 * Math.Exp(-nonTied * Math.Log(2.0)));

    /// <summary>
    /// One-sided binomial upper tail against a FLOOR: <c>P(X ≥ successes)</c> for
    /// <c>X ~ Binomial(trials, floor)</c>.
    /// </summary>
    /// <param name="successes">Observed successes. Clamped into <c>0..trials</c>.</param>
    /// <param name="trials">Trials. Zero gives <see cref="double.NaN"/> — an empty denominator is not a result.</param>
    /// <param name="floor">The null rate. Outside (0, 1) gives <see cref="double.NaN"/>.</param>
    /// <returns>The exact upper-tail probability, or NaN when the question is not askable.</returns>
    /// <remarks>
    /// <b>This — not <c>rate &gt; floor</c> — is what "beats chance" means.</b> A point estimate
    /// above a constant says yes to observations a fair coin produces routinely; measured against a
    /// 1/12 floor over 12 trials, <c>rate &gt; floor</c> marks 2 of 12 (p = 0.264) and 3 of 12
    /// (p = 0.072) as ABOVE.
    /// <para>
    /// <b>Pass an ESTIMATED floor's upper bound, not its point value</b> — see
    /// <see cref="ChanceFloor.ComparisonBar"/>. Comparing an observed rate to a point estimate
    /// computed from the same corpus is the co-moving-operands failure.
    /// </para>
    /// </remarks>
    public static double BinomialTailP(int successes, int trials, double floor)
    {
        if (trials <= 0) return double.NaN;
        if (double.IsNaN(floor) || floor <= 0.0 || floor >= 1.0) return double.NaN;

        int s = Math.Clamp(successes, 0, trials);
        if (s == 0) return 1.0;

        double logP = Math.Log(floor);
        double logQ = Math.Log(1.0 - floor);

        double tail = 0.0;
        for (int k = s; k <= trials; k++)
        {
            tail += Math.Exp(LogChoose(trials, k) + (k * logP) + ((trials - k) * logQ));
        }

        return Math.Clamp(tail, 0.0, 1.0);
    }

    /// <summary>
    /// Exact (Clopper-Pearson) interval, by bisecting the exact tail sums.
    /// </summary>
    /// <param name="successes">Observed successes. Clamped into <c>0..trials</c>.</param>
    /// <param name="trials">Trials. Zero gives <c>(NaN, NaN)</c>.</param>
    /// <param name="alpha">Two-sided significance level; 0.05 gives a 95% interval.</param>
    /// <returns>The interval. <c>Low</c> is exactly 0 at zero successes and <c>High</c> exactly 1 at n of n.</returns>
    /// <remarks>
    /// Returns a named tuple rather than a new interval type: the shipped
    /// <c>AgentEval.Comparison.ConfidenceInterval</c> stays the library's one interval type, and a
    /// second positional record with a different shape would have been a binary and source break on
    /// it. Renderers wrap the tuple.
    /// </remarks>
    public static (double Low, double High) ClopperPearson(int successes, int trials, double alpha = DefaultAlpha)
    {
        if (trials <= 0 || double.IsNaN(alpha) || alpha <= 0.0 || alpha >= 1.0) return (double.NaN, double.NaN);

        int s = Math.Clamp(successes, 0, trials);
        double half = alpha / 2.0;

        // Both functions are INCREASING in p: the upper tail rises with p, and (half − lower tail)
        // rises with it too. One bisector serves both.
        double low = s == 0 ? 0.0 : Bisect(p => BinomialTailP(s, trials, p) - half);
        double high = s == trials ? 1.0 : Bisect(p => half - LowerTail(s, trials, p));

        return (low, high);
    }

    /// <summary>
    /// The one-sided upper bound given <paramref name="events"/> events in
    /// <paramref name="trials"/> trials: <c>1 − alpha^(1/n)</c>, the exact form of the "rule of three".
    /// </summary>
    /// <param name="events">The number of events OBSERVED. Mandatory, and that is the point.</param>
    /// <param name="trials">Trials.</param>
    /// <param name="alpha">Significance level.</param>
    /// <returns>
    /// The bound when <paramref name="events"/> is 0; otherwise <see cref="ZeroEventBound.IsApplicable"/>
    /// is false and the exact interval around the OBSERVED rate is supplied in its place.
    /// </returns>
    /// <remarks>
    /// <b>There is deliberately no overload taking only <paramref name="trials"/>.</b> The rule holds
    /// ONLY at zero events. The recorded defect was calling it with a CLEAN-CASE count and printing
    /// a <i>"95% upper bound of 34.8%"</i> beside an OBSERVED defect rate of 50% (7 of 14) — a bound
    /// below its own observation is not a bound, and it failed in the flattering direction.
    /// Requiring the event count makes the misuse unspellable rather than discouraged.
    /// </remarks>
    public static ZeroEventBound ZeroEventUpperBound(int events, int trials, double alpha = DefaultAlpha)
    {
        if (trials <= 0)
        {
            return new(false, null, null,
                "No trials. An empty denominator supports no bound at all — not a wide one, none.",
                Math.Max(events, 0), trials);
        }

        if (double.IsNaN(alpha) || alpha <= 0.0 || alpha >= 1.0)
        {
            return new(false, null, null,
                string.Create(CultureInfo.InvariantCulture, $"alpha must be in (0, 1); got {alpha}."),
                Math.Max(events, 0), trials);
        }

        if (events < 0)
        {
            return new(false, null, null,
                string.Create(CultureInfo.InvariantCulture, $"A negative event count ({events}) is a caller bug, not an observation."),
                events, trials);
        }

        if (events > 0)
        {
            var interval = ClopperPearson(events, trials, alpha);
            return new(false, null, interval,
                string.Create(CultureInfo.InvariantCulture,
                    $"The rule of three holds ONLY at zero events; {events} of {trials} were observed. An exact interval around the observed rate ({events}/{trials}) is supplied instead: [{interval.Low:F4}, {interval.High:F4}].")
                + " A bound computed here would sit BELOW its own observation and would read as reassurance.",
                events, trials);
        }

        double bound = 1.0 - Math.Exp(Math.Log(alpha) / trials);
        return new(true, bound, null,
            string.Create(CultureInfo.InvariantCulture,
                $"Zero events in {trials} trials: the true rate is below {bound:P2} with {1 - alpha:P0} confidence.")
            + " This is an upper bound on an UNOBSERVED event, never evidence the event cannot happen.",
            0, trials);
    }

    /// <summary>P(X ≤ successes) for X ~ Binomial(trials, p).</summary>
    private static double LowerTail(int successes, int trials, double p)
    {
        if (p <= 0.0) return 1.0;
        if (p >= 1.0) return successes >= trials ? 1.0 : 0.0;

        double logP = Math.Log(p);
        double logQ = Math.Log(1.0 - p);

        double tail = 0.0;
        for (int k = 0; k <= successes; k++)
        {
            tail += Math.Exp(LogChoose(trials, k) + (k * logP) + ((trials - k) * logQ));
        }

        return Math.Clamp(tail, 0.0, 1.0);
    }

    /// <summary>Bisects an INCREASING function on [0, 1] for its root, to double precision.</summary>
    private static double Bisect(Func<double, double> f)
    {
        double lo = 0.0, hi = 1.0;
        for (int i = 0; i < 200; i++)
        {
            double mid = (lo + hi) / 2.0;
            if (f(mid) < 0.0) lo = mid; else hi = mid;
        }

        return (lo + hi) / 2.0;
    }

    /// <summary>log C(n, k) via log-factorials — no factorial is ever formed.</summary>
    private static double LogChoose(int n, int k) =>
        LogFactorial(n) - LogFactorial(k) - LogFactorial(n - k);

    /// <summary>log(n!) — exact summation to n = 1000, Stirling above it.</summary>
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
