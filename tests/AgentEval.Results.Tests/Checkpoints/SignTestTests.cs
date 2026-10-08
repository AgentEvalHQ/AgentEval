using System.Diagnostics;
using System.Numerics;
using AgentEval.Results.Checkpoints;

namespace AgentEval.Results.Tests.Checkpoints;

/// <summary>
/// [LANE-8], [LANE-11]: the exact one-sided sign test. Expected values are computed here independently, as sums of
/// binomials with BigInteger and an exact comparison of rationals.
/// </summary>
public class SignTestTests
{
    [Fact]
    public void TheTail_IsTheSumOfBinomials_ForEveryRegressionCount()
    {
        for (var m = 0; m <= 64; m++)
        {
            for (var r = 0; r <= m; r++)
            {
                Assert.Equal(NaiveTail(m, r), SignTest.UpperTail(m, r));
            }
        }
    }

    [Theory]
    [InlineData(2_097_160, 0)]      // a million pairs and more: two factors per step instead of three
    [InlineData(2_097_160, 7)]
    [InlineData(2_097_160, 12)]
    [InlineData(int.MaxValue - 1, 3)]
    public void ThePrefix_IsExact_WhenOnlyTwoFactorsFitInAWord(int m, int last)
    {
        Assert.Equal(Enumerable.Range(0, last + 1).Aggregate(BigInteger.Zero, (sum, k) => sum + Binomial(m, k)), SignTest.ExactPrefix(m, last));
    }

    [Fact]
    public void TheBounds_AlwaysHoldTheExactTail()
    {
        foreach (var m in new[] { 1, 2, 3, 10, 53, 64, 65, 200, 333, 1200 })
        {
            for (var r = 0; r <= m; r += Math.Max(1, m / 40))
            {
                var exact = new SignTest.Dyadic(SignTest.UpperTail(m, r), 0);
                var (lower, upper) = SignTest.TailBounds(m, r);
                Assert.True(lower.CompareTo(exact) <= 0, $"m={m} r={r}: lower bound above the tail");
                Assert.True(upper.CompareTo(exact) >= 0, $"m={m} r={r}: upper bound below the tail");
            }
        }
    }

    [Fact]
    public void TheTest_AgreesWithTheExactComparison_OnBothSidesOfP()
    {
        for (var m = 1; m <= 90; m++)
        {
            for (var r = 0; r <= m; r++)
            {
                var tail = NaiveTail(m, r);
                var near = Math.ScaleB((double)tail, -m);
                foreach (var s in new[] { near, Math.BitDecrement(near), Math.BitIncrement(near), near / 2, Math.Min(near * 2, 0.999) })
                {
                    if (s is > 0 and < 1)
                    {
                        Assert.Equal(AtMost(tail, m, s), SignTest.PAtMost(r, m - r, s));
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(0.0107421875, true)]    // p = 11/1024 exactly: p ≤ significance
    [InlineData(0.0107421874, false)]
    public void ABoundaryAtExactlyP_Fails(double significance, bool atMost)
    {
        // 9 regressions of 10 pairs: p = (C(10,9) + C(10,10)) / 2^10 = 11/1024 (lane-vectors/comparison, boundary-*).
        Assert.Equal(atMost, SignTest.PAtMost(9, 1, significance));
    }

    [Theory]
    [InlineData(0.04991859577302666, false)]   // one binary64 step below the exact p of 629 regressions in 1,200 pairs
    [InlineData(0.04991859577302667, true)]    // one step above
    public void OneBinary64StepEitherSideOfP_IsDecidedExactly(double significance, bool atMost)
    {
        Assert.Equal(atMost, SignTest.PAtMost(629, 571, significance));
        Assert.Equal(atMost, AtMost(NaiveTail(1200, 629), 1200, significance));
    }

    [Fact]
    public void NoRegression_GivesPOne_AndAllRegressions_GivePTwoToTheMinusM()
    {
        Assert.False(SignTest.PAtMost(0, 50, 0.999999));
        Assert.True(SignTest.PAtMost(30, 0, Math.ScaleB(1, -30)));
        Assert.False(SignTest.PAtMost(30, 0, Math.BitDecrement(Math.ScaleB(1, -30))));
    }

    [Fact]
    public void ASubnormalSignificance_IsComparedAtItsExactValue()
    {
        Assert.True(SignTest.PAtMost(1074, 0, double.Epsilon));    // p = 2^-1074 = the smallest subnormal
        Assert.True(SignTest.PAtMost(1075, 0, double.Epsilon));    // p = 2^-1075 < it
        Assert.False(SignTest.PAtMost(1073, 1, double.Epsilon));   // p = 1075 · 2^-1074 > it
    }

    [Fact]
    public void AMillionPairs_AreDecidedInMilliseconds_ByExactBounds()
    {
        // m = 1,000,000 with r near m/2: about half a million terms of up to a million bits each, decided on bounds.
        var m = 1_000_000;
        var watch = Stopwatch.StartNew();
        Assert.True(SignTest.PAtMost(501_000, m - 501_000, 0.05));    // 1,000 regressions above half: p ≈ 0.023
        Assert.False(SignTest.PAtMost(500_500, m - 500_500, 0.05));   // 500 above: p ≈ 0.16
        Assert.False(SignTest.PAtMost(500_000, m - 500_000, 0.4));    // exactly half: p just above 0.5
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void AnExactTieAtAMillionPairs_IsDecidedByTheExactSum()
    {
        // For an odd m, Σ_{k ≥ (m+1)/2} C(m, k) = 2^(m−1): p is exactly 1/2, which no bound can separate from 0.5.
        var m = 1_000_001;
        var r = (m + 1) / 2;
        var watch = Stopwatch.StartNew();
        Assert.True(SignTest.PAtMost(r, m - r, 0.5));
        Assert.False(SignTest.PAtMost(r, m - r, Math.BitDecrement(0.5)));
        Assert.Equal(BigInteger.One << (m - 1), SignTest.UpperTail(m, r));
        watch.Stop();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(2001)]
    [InlineData(2000)]
    [InlineData(64)]
    public void ThePrefix_SummedDownFromTheMiddle_IsThePrefixSummedUpFromTheStart(int m)
    {
        BigInteger term = 1, prefix = 0;
        var half = m % 2 == 1 ? (m - 1) / 2 : m / 2;
        for (var k = 0; k <= half; k++)
        {
            prefix += term;
            if (k >= half - 40 || k % 37 == 0)
            {
                Assert.Equal(prefix, SignTest.ExactPrefix(m, k));
            }

            term = term * (m - k) / (k + 1);
        }
    }

    [Fact]
    public void AMiddleSumAtAMillionPairs_LiesWithinTheBoundsComputedFromTheStart()
    {
        // Two independent computations: the exact tail from the middle (a binomial from its prime factors, then a
        // thousand terms), and bounds carried up from C(m, 0) with 96-bit mantissas.
        var m = 1_000_000;
        var r = (m / 2) + 1_000;
        var watch = Stopwatch.StartNew();
        var exact = new SignTest.Dyadic(SignTest.UpperTail(m, r), 0);
        watch.Stop();
        var (lower, upper) = SignTest.TailBounds(m, r);

        Assert.True(lower.CompareTo(exact) <= 0 && exact.CompareTo(upper) <= 0);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Theory]
    [InlineData(10, 3)]
    [InlineData(1000, 500)]
    [InlineData(999, 1)]
    [InlineData(4096, 4095)]
    [InlineData(7, 0)]
    public void ABinomialFromItsPrimeFactors_IsTheBinomial(int m, int k) =>
        Assert.Equal(Binomial(m, k), SignTest.Binomial(m, k));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void ANegativeCount_IsRefused(int regressions, int improvements) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SignTest.PAtMost(regressions, improvements, 0.05));

    [Fact]
    public void ASignificanceThatIsNotFinite_IsRefused_AndOneAtOrBelowZeroNeverFails()
    {
        Assert.Throws<ArgumentException>(() => SignTest.PAtMost(5, 5, double.NaN));
        Assert.False(SignTest.PAtMost(10, 0, 0));
        Assert.True(SignTest.PAtMost(0, 10, 1));
    }

    // Σ_{k=r}^{m} C(m, k), term by term.
    private static BigInteger NaiveTail(int m, int r) =>
        Enumerable.Range(r, m - r + 1).Aggregate(BigInteger.Zero, (sum, k) => sum + Binomial(m, k));

    private static BigInteger Binomial(int m, int k)
    {
        BigInteger c = 1;
        for (var i = 0; i < k; i++)
        {
            c = c * (m - i) / (i + 1);
        }

        return c;
    }

    // tail / 2^m ≤ s, exactly: s = M · 2^E.
    private static bool AtMost(BigInteger tail, int m, double s)
    {
        var bits = BitConverter.DoubleToInt64Bits(s);
        var biased = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & ((1L << 52) - 1);
        var (mantissa, exponent) = biased == 0 ? (new BigInteger(fraction), -1074) : (new BigInteger(fraction | (1L << 52)), biased - 1075);
        var shift = exponent + m;
        return shift >= 0 ? tail <= mantissa << shift : tail << -shift <= mantissa;
    }
}
