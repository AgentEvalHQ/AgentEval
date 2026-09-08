// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals.Meta;

/// <summary>
/// ADR-030 Slice 2.3 and 2.4.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every reference value here was computed OUTSIDE this codebase</b>, in exact rational
/// arithmetic, and pasted in. That matters: the recorded failure mode when this repository last
/// added an exact test was <i>two wrong hand-computed references</i>, caught only because a control
/// row compared the implementation against an independent one. A test that asserts an
/// implementation against a number the same author derived in their head is a test of neither.
/// </para>
/// </remarks>
public class ExactTestsTests
{
    // ── 2.3 · TwoSidedSignP against R's binom.test ────────────────────────────────────────────

    [Fact]
    public void SignP_MatchesReference()
    {
        // R: binom.test(8, 18)$p.value == 0.8145294189453125.
        // Derived independently as an exact rational: 2 * sum_{k=10..18} C(18,k) / 2^18, which at
        // p = 0.5 is the same set of outcomes R keeps (every outcome except k = 9, the only one
        // whose density exceeds the observed).
        //
        // ⚠ Pinned to 12 decimals, not 15, and that is a real cost being declared rather than hidden:
        // log-space accumulation returns 0.81452941894531006 where exact rational arithmetic gives
        // 0.8145294189453125 — about 2.4e-15 out, roughly two ULPs. That is the price of surviving
        // n = 4000 (see SignP_SurvivesLargeN), and it is four orders of magnitude below any alpha
        // anybody compares against.
        Assert.Equal(0.8145294189453125, ExactTests.TwoSidedSignP(8, 18), 12);

        // A clean sweep in each direction: 2 * 2^-12.
        Assert.Equal(0.00048828125, ExactTests.TwoSidedSignP(0, 12), 15);
        Assert.Equal(0.00048828125, ExactTests.TwoSidedSignP(12, 12), 15);

        // 9 of 10 — 2 * (C(10,9) + C(10,10)) / 2^10 = 22/1024.
        Assert.Equal(0.021484375, ExactTests.TwoSidedSignP(9, 10), 15);

        // 4 of 4 is exactly the minimum attainable at n = 4, and it is NOT significant at 0.05.
        Assert.Equal(0.125, ExactTests.TwoSidedSignP(4, 4), 15);
    }

    [Fact]
    public void SignP_EveryPairTied_IsOne_NeverAWin()
    {
        // "Every case tied" is no detectable difference. Rendering it as anything but 1.0 is how a
        // comparison nobody could make becomes a comparison somebody won.
        Assert.Equal(1.0, ExactTests.TwoSidedSignP(0, 0));
        Assert.Equal(1.0, ExactTests.TwoSidedSignP(5, 0));
    }

    [Fact]
    public void SignP_SurvivesLargeN()
    {
        // The naive form divides a sum of binomial coefficients by Math.Pow(2, n). At n = 4000 that
        // denominator is +Infinity, the quotient is NaN, and NaN renders as "no result" — silently,
        // and in the flattering direction for whoever wanted no difference found.
        double naiveDenominator = Math.Pow(2, 4000);
        Assert.True(double.IsPositiveInfinity(naiveDenominator), "the naive form must actually be broken at this n");

        double p = ExactTests.TwoSidedSignP(2100, 4000);

        Assert.True(double.IsFinite(p), "log-space accumulation must return a finite p where the naive form cannot");
        Assert.InRange(p, 0.0, 1.0);
        Assert.Equal(0.001649266375139406, p, 12);   // computed in log space outside this codebase
    }

    [Fact]
    public void SignP_IsSymmetric_AndClampedToOne()
    {
        Assert.Equal(ExactTests.TwoSidedSignP(3, 10), ExactTests.TwoSidedSignP(7, 10), 15);

        // 1 of 2 is 2 * P(X >= 1) = 2 * 0.75 = 1.5 before clamping. A p above 1 is not a p.
        Assert.Equal(1.0, ExactTests.TwoSidedSignP(1, 2));
    }

    // ── MinimumAttainableP ────────────────────────────────────────────────────────────────────

    [Fact]
    public void MinimumAttainableP_ComesFromTheNonTiedCount()
    {
        // The recorded design: 13 pairs, 9 tied, 4 informative. The smallest two-sided p the design
        // could produce is 0.125, so it could not have reached 0.05 even on a 4-0 sweep. Using the
        // full paired count (13) would have said 0.000244 and made an unreachable alpha look
        // reachable.
        Assert.Equal(0.125, ExactTests.MinimumAttainableP(4), 15);
        Assert.True(ExactTests.MinimumAttainableP(4) > ExactTests.DefaultAlpha);
        Assert.True(ExactTests.MinimumAttainableP(13) < ExactTests.DefaultAlpha);

        // And it is exactly what a clean sweep at that n produces — the floor is attainable, not a
        // bound nothing reaches.
        Assert.Equal(ExactTests.TwoSidedSignP(4, 4), ExactTests.MinimumAttainableP(4), 15);

        // n = 1 cannot distinguish anything: 2 * 0.5 = 1.
        Assert.Equal(1.0, ExactTests.MinimumAttainableP(1), 15);
        Assert.Equal(1.0, ExactTests.MinimumAttainableP(0), 15);
    }

    // ── BinomialTailP ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BinomialTail_MatchesReference_AndRefusesTheUnaskable()
    {
        const double OneInTwelve = 1.0 / 12.0;

        // Exact rational references against a 1/12 floor over 12 trials.
        Assert.Equal(0.26400914142498605, ExactTests.BinomialTailP(2, 12, OneInTwelve), 12);
        Assert.Equal(0.072011526144547658, ExactTests.BinomialTailP(3, 12, OneInTwelve), 12);
        Assert.Equal(0.013830430605020861, ExactTests.BinomialTailP(4, 12, OneInTwelve), 12);
        Assert.Equal(1.5152434271467392e-05, ExactTests.BinomialTailP(7, 12, OneInTwelve), 15);

        // The point of the type: `rate > floor` says ABOVE at 2 of 12 and 3 of 12. The exact test
        // does not, and 4 of 12 is where it starts to.
        Assert.True(ExactTests.BinomialTailP(2, 12, OneInTwelve) > ExactTests.DefaultAlpha);
        Assert.True(ExactTests.BinomialTailP(3, 12, OneInTwelve) > ExactTests.DefaultAlpha);
        Assert.True(ExactTests.BinomialTailP(4, 12, OneInTwelve) <= ExactTests.DefaultAlpha);

        // An empty denominator is not a result, and neither is a degenerate floor.
        Assert.True(double.IsNaN(ExactTests.BinomialTailP(0, 0, 0.5)));
        Assert.True(double.IsNaN(ExactTests.BinomialTailP(3, 12, 0.0)));
        Assert.True(double.IsNaN(ExactTests.BinomialTailP(3, 12, 1.0)));
        Assert.True(double.IsNaN(ExactTests.BinomialTailP(3, 12, double.NaN)));

        // Zero successes is P(X >= 0) = 1 by definition, never 0.
        Assert.Equal(1.0, ExactTests.BinomialTailP(0, 12, OneInTwelve));
    }

    // ── ClopperPearson ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClopperPearson_MatchesReference()
    {
        // The textbook 95% exact interval for 7 of 14.
        var (low, high) = ExactTests.ClopperPearson(7, 14);
        Assert.Equal(0.23036054144806234, low, 10);
        Assert.Equal(0.76963945855193772, high, 10);

        // The boundary cases are exact, not bisected: 0 successes has a lower bound of exactly 0,
        // and n of n an upper bound of exactly 1.
        var (zeroLow, zeroHigh) = ExactTests.ClopperPearson(0, 14);
        Assert.Equal(0.0, zeroLow);
        Assert.Equal(0.23163576165011651, zeroHigh, 10);

        var (allLow, allHigh) = ExactTests.ClopperPearson(14, 14);
        Assert.Equal(0.76836423834988343, allLow, 10);
        Assert.Equal(1.0, allHigh);

        // An interval on nothing is not a wide interval.
        var (nanLow, nanHigh) = ExactTests.ClopperPearson(3, 0);
        Assert.True(double.IsNaN(nanLow) && double.IsNaN(nanHigh));
    }

    [Fact]
    public void ClopperPearson_ContainsItsOwnObservation()
    {
        // An interval that excludes the point it was computed from is the shape of the recorded
        // 34.8%-vs-50% defect. Checked across the whole range rather than at one point.
        for (int s = 0; s <= 14; s++)
        {
            var (low, high) = ExactTests.ClopperPearson(s, 14);
            double rate = s / 14.0;
            Assert.True(low <= rate + 1e-12 && rate <= high + 1e-12,
                $"the exact interval for {s} of 14 is [{low}, {high}] and excludes its own observation {rate}");
        }
    }

    // ── 2.4 · ZeroEventUpperBound misuse is unspellable ───────────────────────────────────────

    [Fact]
    public void RuleOfThree_RefusesNonZeroEvents()
    {
        // THE recorded defect, reproduced as a test: the rule was called with a CLEAN-CASE count of
        // 7 and printed "a 95% upper bound of 34.8%" beside an OBSERVED defect rate of 7 of 14 =
        // 50%. A bound below its own observation is not a bound, and it failed in the flattering
        // direction.
        var wrong = ExactTests.ZeroEventUpperBound(events: 7, trials: 14);

        Assert.False(wrong.IsApplicable);
        Assert.Null(wrong.UpperBound);
        Assert.NotNull(wrong.ObservedRateInterval);
        Assert.False(string.IsNullOrWhiteSpace(wrong.Reason));

        // What it gives back instead is an interval that BRACKETS the observation it was handed.
        var interval = wrong.ObservedRateInterval!.Value;
        Assert.True(interval.Low <= 0.5 && 0.5 <= interval.High);

        // And the number the misuse used to produce is demonstrably below that observation, so the
        // test pins the direction of the old error, not just its absence.
        double oldWrongBound = 1.0 - Math.Pow(ExactTests.DefaultAlpha, 1.0 / 7.0);
        Assert.True(oldWrongBound < 0.5);
    }

    [Fact]
    public void RuleOfThree_AtZeroEvents_IsTheExactForm()
    {
        var bound = ExactTests.ZeroEventUpperBound(events: 0, trials: 14);

        Assert.True(bound.IsApplicable);
        Assert.Null(bound.ObservedRateInterval);
        Assert.Equal(0.19263617565013536, bound.UpperBound!.Value, 12);   // 1 - 0.05^(1/14)

        // Small n, wide bound. 3 clean trials support almost nothing, and the number says so.
        Assert.Equal(0.63159685013596123, ExactTests.ZeroEventUpperBound(0, 3).UpperBound!.Value, 12);
    }

    [Fact]
    public void RuleOfThree_OnNoTrials_IsNotAWideBound()
    {
        var none = ExactTests.ZeroEventUpperBound(events: 0, trials: 0);

        Assert.False(none.IsApplicable);
        Assert.Null(none.UpperBound);
        Assert.Null(none.ObservedRateInterval);
    }
}
