// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AgentEval.Results.Checkpoints;

/// <summary>
/// The exact one-sided sign test of a comparison lane (contracts/aef/1/spec/05-checkpoints.md, [LANE-8], [LANE-11]): with
/// <c>r</c> regressions among <c>m</c> untied pairs, <c>p = Σ_{k=r}^{m} C(m, k) / 2^m</c>, and the lane fails when
/// <c>p ≤ significance</c>. Nothing is computed in floating point: <c>p</c> is a rational with a power of two below it,
/// <c>significance</c> is the exact rational value of a binary64 number (<c>M · 2^E</c>), and the two are compared as
/// integers.
/// </summary>
/// <remarks>
/// <para>
/// The tail is summed on whichever side of <c>r</c> is shorter ([LANE-11]): by symmetry (<c>C(m, k) = C(m, m − k)</c>) the
/// upper tail <c>Σ_{k=r}^{m}</c> is the prefix <c>Σ_{j=0}^{m−r}</c>, and otherwise it is <c>2^m</c> minus the prefix
/// <c>Σ_{j=0}^{r−1}</c>. Each term follows from the previous one, <c>C(m, j + 1) = C(m, j) · (m − j) / (j + 1)</c>.
/// </para>
/// <para>
/// First, bounds computed exactly ([LANE-11] allows deciding sooner on them): each term and the running sum are carried
/// as a 96-bit integer and a power of two, rounded down for the lower bound and up for the upper one, so the true prefix
/// always lies between them (relatively within about 2^-60 at a million pairs). When both bounds fall on one side of
/// <c>significance</c>, that is the answer; this takes milliseconds at <c>m</c> = 1,000,000. Only when <c>p</c> lies
/// within that sliver of <c>significance</c> (in practice: when <c>p</c> is exactly a binary64 value and equals it) is the
/// prefix summed exactly, on mutable 64-bit words: three terms per pass, each word multiplied by a 64-bit factor and
/// divided exactly (by the odd part's inverse modulo 2^64, then a shift). A <c>p</c> that close to a significance between
/// 2^-1074 and 1 has <c>r</c> within a few tens of thousands of <c>m/2</c>, so the exact prefix is summed down from the
/// middle, where it is known, starting from a binomial computed from its prime factors: about half a second at a million
/// pairs (summing up from <c>C(m, 0)</c> would take half a million steps, about twenty seconds). Both paths give the same
/// answer; the bounds only decide when they settle it.
/// </para>
/// </remarks>
public static class SignTest
{
    // Mantissas of the bounds are kept below 2^96 (and at least 2^95 when they are not exact), so a mantissa times a
    // factor below 2^31 fits in 128 bits.
    private const int MantissaBits = 96;

    /// <summary>
    /// Whether <c>p ≤ significance</c> for <paramref name="regressions"/> regressions and <paramref name="improvements"/>
    /// improvements ([LANE-8]): the lane's result is then <c>failed</c>. Ties are dropped before this is asked.
    /// </summary>
    /// <param name="regressions">The pairs that regressed, <c>r</c>.</param>
    /// <param name="improvements">The pairs that improved; <c>m</c> = <c>r</c> + this.</param>
    /// <param name="significance">The rule's significance as read (binary64, [ENC-4]); compared at its exact value.</param>
    /// <exception cref="ArgumentOutOfRangeException">A negative count, or more than <see cref="int.MaxValue"/> pairs in all.</exception>
    /// <exception cref="ArgumentException"><paramref name="significance"/> is not a finite number.</exception>
    public static bool PAtMost(int regressions, int improvements, double significance)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(regressions);
        ArgumentOutOfRangeException.ThrowIfNegative(improvements);
        if (!double.IsFinite(significance))
        {
            throw new ArgumentException("The significance is a finite binary64 number ([ENC-3]).", nameof(significance));
        }

        var pairs = (long)regressions + improvements;
        if (pairs > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(improvements), "More pairs than the test is defined for here.");
        }

        if (significance <= 0)
        {
            return false;   // p ≥ 2^-m > 0
        }

        var m = (int)pairs;
        var (mantissa, exponent) = Exact(significance);

        // p ≤ M·2^E  ⇔  tail ≤ M·2^(E+m).
        var threshold = new Dyadic(mantissa, exponent + (long)m);
        var (lower, upper) = TailBounds(m, regressions);
        if (upper.CompareTo(threshold) <= 0)
        {
            return true;
        }

        if (lower.CompareTo(threshold) > 0)
        {
            return false;
        }

        return new Dyadic(UpperTail(m, regressions), 0).CompareTo(threshold) <= 0;
    }

    /// <summary>The numerator of <c>p</c>: <c>Σ_{k=r}^{m} C(m, k)</c>, exactly (<c>p</c> is it divided by <c>2^m</c>).</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pairs"/> is negative, or <paramref name="regressions"/> is not between 0 and it.</exception>
    public static BigInteger UpperTail(int pairs, int regressions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pairs);
        ArgumentOutOfRangeException.ThrowIfNegative(regressions);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(regressions, pairs);
        return Symmetric(pairs, regressions)
            ? ExactPrefix(pairs, pairs - regressions)
            : (BigInteger.One << pairs) - ExactPrefix(pairs, regressions - 1);
    }

    /// <summary>
    /// Bounds on <see cref="UpperTail"/> computed exactly (integers and powers of two, rounded outward): the lower bound
    /// is never above the tail, the upper never below it.
    /// </summary>
    internal static (Dyadic Lower, Dyadic Upper) TailBounds(int pairs, int regressions)
    {
        if (Symmetric(pairs, regressions))
        {
            return PrefixBounds(pairs, pairs - regressions);
        }

        // 2^m minus the prefix below r: its upper bound gives the tail's lower bound, and the reverse.
        var (lower, upper) = PrefixBounds(pairs, regressions - 1);
        return (Dyadic.PowerOfTwoMinus(pairs, upper), Dyadic.PowerOfTwoMinus(pairs, lower));
    }

    /// <summary>
    /// The exact prefix <c>Σ_{j=0}^{n} C(m, j)</c>; 0 for a negative <paramref name="last"/>. Summed from <c>C(m, 0)</c>
    /// up, or, when <c>n</c> is nearer the middle than the start, from the middle down: up to the middle <c>H</c> the
    /// prefix is known (<c>2^(m−1)</c> for an odd <c>m</c>, <c>(2^m + C(m, m/2)) / 2</c> for an even one), and the terms
    /// between <c>n</c> and <c>H</c> are subtracted, starting from <c>C(m, n + 1)</c> computed from its prime factors.
    /// When the bounds cannot decide, <c>p</c> is within about 2^-60 of a significance between 2^-1074 and 1, so
    /// <c>n</c> is within a few tens of thousands of the middle even at a million pairs: the exact sum then costs
    /// those terms, not half a million.
    /// </summary>
    internal static BigInteger ExactPrefix(int m, int last)
    {
        if (last < 0)
        {
            return BigInteger.Zero;
        }

        var half = m % 2 == 1 ? (m - 1) / 2 : m / 2;
        if (m <= MiddleLimit && last <= half && half - last < last / 2)
        {
            var atHalf = m % 2 == 1
                ? BigInteger.One << (m - 1)
                : ((BigInteger.One << m) + Binomial(m, half)) >> 1;
            return last == half ? atHalf : atHalf - Sum(m, last + 1, half, Binomial(m, last + 1));
        }

        return Sum(m, 0, last, BigInteger.One);
    }

    // The middle method needs a sieve of the primes up to m.
    private const int MiddleLimit = 1 << 23;

    /// <summary>
    /// <c>C(m, k)</c> from its prime factorization (Legendre: the exponent of a prime <c>p</c> is
    /// <c>Σ_i ⌊m/p^i⌋ − ⌊k/p^i⌋ − ⌊(m−k)/p^i⌋</c>), multiplied out as a balanced product.
    /// </summary>
    internal static BigInteger Binomial(int m, int k)
    {
        if (k < 0 || k > m)
        {
            return BigInteger.Zero;
        }

        k = Math.Min(k, m - k);
        var factors = new List<BigInteger>();
        var composite = new bool[m + 1];
        ulong chunk = 1;
        for (var p = 2; p <= m && k > 0; p++)
        {
            if (composite[p])
            {
                continue;
            }

            for (var multiple = (long)p * p; multiple <= m; multiple += p)
            {
                composite[multiple] = true;
            }

            var exponent = 0L;
            for (var power = (long)p; power <= m; power *= p)
            {
                exponent += (m / power) - (k / power) - ((m - k) / power);
            }

            for (var i = 0L; i < exponent; i++)
            {
                if (chunk > ulong.MaxValue / (ulong)p)
                {
                    factors.Add(chunk);
                    chunk = 1;
                }

                chunk *= (ulong)p;
            }
        }

        factors.Add(chunk);
        return Product(factors, 0, factors.Count);
    }

    private static BigInteger Product(List<BigInteger> factors, int from, int to) =>
        to - from == 1 ? factors[from] : Product(factors, from, (from + to) / 2) * Product(factors, (from + to) / 2, to);

    /// <summary><c>Σ_{k=first}^{last} C(m, k)</c>, exactly, given <paramref name="start"/> = <c>C(m, first)</c>.</summary>
    private static BigInteger Sum(int m, int first, int last, BigInteger start)
    {
        // The sum is below 2^m, and below (n + 1) · m^n for the last term n: words for the smaller, plus room for a
        // product's carry.
        var bits = Math.Min(m, ((long)last * (64 - BitOperations.LeadingZeroCount((ulong)m))) + 64);
        var words = (int)(bits / 64) + 3;
        var term = new ulong[words];
        var sum = new ulong[words + 1];
        var startBytes = start.ToByteArray(isUnsigned: true, isBigEndian: false);
        var termLength = (startBytes.Length + 7) / 8;
        for (var i = 0; i < startBytes.Length; i++)
        {
            term[i / 8] |= (ulong)startBytes[i] << (8 * (i % 8));
        }

        // Three factors below 2^21 multiply to below 2^63; two below 2^31 to below 2^62.
        var block = m < (1 << 21) ? 3 : 2;
        var k = first;
        while (k <= last)
        {
            var count = Math.Min(block, last - k + 1);   // terms k .. k + count - 1

            // Their sum is C(m, k) · N / D, with a_t = m − k − t, d_t = k + t + 1, D = Π_{t<count−1} d_t and
            // N = Σ_{j<count} (Π_{t<j} a_t) · (Π_{j≤t<count−1} d_t); the next block's first term is
            // C(m, k + count) = C(m, k) · Π_{t<count} a_t / Π_{t<count} d_t.
            ulong numerator = 0, denominator = 1, up = 1, down = 1;
            for (var j = 0; j < count; j++)
            {
                ulong product = 1;
                for (var t = 0; t < j; t++)
                {
                    product *= (ulong)(m - k - t);
                }

                for (var t = j; t < count - 1; t++)
                {
                    product *= (ulong)(k + t + 1);
                }

                numerator += product;
                up *= (ulong)(m - k - j);
                down *= (ulong)(k + j + 1);
                if (j < count - 1)
                {
                    denominator *= (ulong)(k + j + 1);
                }
            }

            k += count;
            termLength = Pass(term, termLength, sum, numerator, denominator, up, down, advance: k <= last);
        }

        var sumLength = sum.Length;
        while (sumLength > 0 && sum[sumLength - 1] == 0)
        {
            sumLength--;
        }

        var bytes = new byte[Math.Max(sumLength, 1) * 8];
        for (var i = 0; i < sumLength; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), sum[i]);
        }

        return new BigInteger(bytes, isUnsigned: true, isBigEndian: false);
    }

    /// <summary>
    /// Lower and upper bounds on <c>Σ_{j=0}^{n} C(m, j)</c>, each a mantissa below 2^96 and a power of two, rounded
    /// down and up at every step.
    /// </summary>
    internal static (Dyadic Lower, Dyadic Upper) PrefixBounds(int m, int last)
    {
        if (last < 0)
        {
            return (Dyadic.Zero, Dyadic.Zero);
        }

        // C(m, 0) = 1, as 2^95 · 2^-95: every mantissa starts and stays normalized, so aligning two values only drops
        // bits below the larger one's last.
        UInt128 termLow = UInt128.One << (MantissaBits - 1), termHigh = termLow, sumLow = termLow, sumHigh = termLow;
        int termLowExp = 1 - MantissaBits, termHighExp = termLowExp, sumLowExp = termLowExp, sumHighExp = termLowExp;
        for (var j = 0; j < last; j++)
        {
            var a = (ulong)(m - j);
            var d = (ulong)(j + 1);
            termLow = termLow * a / d;
            termHigh = ((termHigh * a) + d - 1) / d;
            Normalize(ref termLow, ref termLowExp, up: false);
            Normalize(ref termHigh, ref termHighExp, up: true);
            AddInto(ref sumLow, ref sumLowExp, termLow, termLowExp, up: false);
            AddInto(ref sumHigh, ref sumHighExp, termHigh, termHighExp, up: true);
        }

        return (new Dyadic(Big(sumLow), sumLowExp), new Dyadic(Big(sumHigh), sumHighExp));
    }

    // The upper tail from r has m − r + 1 terms, the prefix below r has r: sum the shorter (ties go to the tail itself).
    private static bool Symmetric(int m, int r) => m - r <= r - 1;

    // A binary64 value > 0 as M · 2^E exactly.
    private static (BigInteger Mantissa, long Exponent) Exact(double value)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var biased = (int)((bits >> 52) & 0x7FF);
        var fraction = bits & ((1L << 52) - 1);
        return biased == 0 ? (fraction, -1074) : (fraction | (1L << 52), biased - 1075);
    }

    // Keeps a bound's mantissa below 2^96: shifting right rounds down (lower bound) or up (upper bound); a mantissa
    // below 2^95 is shifted left, which is exact, so the bound keeps its precision as terms shrink.
    private static void Normalize(ref UInt128 mantissa, ref int exponent, bool up)
    {
        var bits = 128 - (int)UInt128.LeadingZeroCount(mantissa);
        if (bits > MantissaBits)
        {
            var shift = bits - MantissaBits;
            var dropped = mantissa & ((UInt128.One << shift) - 1);
            mantissa >>= shift;
            if (up && dropped != 0)
            {
                mantissa++;
            }

            exponent += shift;
        }
        else if (bits < MantissaBits - 1 && mantissa != 0)
        {
            var shift = MantissaBits - 1 - bits;
            mantissa <<= shift;
            exponent -= shift;
        }
    }

    // sum += value, both as mantissa · 2^exponent, the operand with the smaller exponent shifted to the larger one and
    // rounded in the bound's direction.
    private static void AddInto(ref UInt128 sum, ref int sumExp, UInt128 value, int valueExp, bool up)
    {
        if (valueExp > sumExp)
        {
            sum = Shift(sum, valueExp - sumExp, up);
            sumExp = valueExp;
        }
        else if (valueExp < sumExp)
        {
            value = Shift(value, sumExp - valueExp, up);
        }

        sum += value;   // both below 2^97: no overflow
        Normalize(ref sum, ref sumExp, up);
    }

    private static UInt128 Shift(UInt128 value, int shift, bool up)
    {
        if (shift >= 128)
        {
            return up && value != 0 ? UInt128.One : UInt128.Zero;
        }

        var dropped = value & ((UInt128.One << shift) - 1);
        value >>= shift;
        return up && dropped != 0 ? value + 1 : value;
    }

    private static BigInteger Big(UInt128 value) =>
        ((BigInteger)(ulong)(value >> 64) << 64) | (ulong)value;

    /// <summary>
    /// One pass over the term <c>x</c> = C(m, k) (<paramref name="length"/> words), from the low word up: adds
    /// <c>x · sumMultiplier / sumDivisor</c> into <paramref name="sum"/>, and, when <paramref name="advance"/>, replaces
    /// the term in place with <c>x · nextMultiplier / nextDivisor</c>. Both quotients are integers (sums and terms of
    /// binomials), so each is an exact division: every word of the product is divided by the divisor's odd part through
    /// its inverse modulo 2^64, and the quotient is shifted right by the divisor's power of two. Returns the new term's
    /// length in words.
    /// </summary>
    private static int Pass(ulong[] term, int length, ulong[] sum, ulong sumMultiplier, ulong sumDivisor, ulong nextMultiplier, ulong nextDivisor, bool advance)
    {
        var sumTwos = BitOperations.TrailingZeroCount(sumDivisor);
        var sumOdd = sumDivisor >> sumTwos;
        var sumInverse = Inverse(sumOdd);
        var nextTwos = BitOperations.TrailingZeroCount(nextDivisor);
        var nextOdd = nextDivisor >> nextTwos;
        var nextInverse = Inverse(nextOdd);

        ref var x = ref MemoryMarshal.GetArrayDataReference(term);
        ref var s = ref MemoryMarshal.GetArrayDataReference(sum);
        ulong sumCarry = 0, sumBorrow = 0, sumPrevious = 0, addCarry = 0;
        ulong nextCarry = 0, nextBorrow = 0, nextPrevious = 0;
        for (var i = 0; i <= length; i++)
        {
            var word = i < length ? Unsafe.Add(ref x, i) : 0;

            // The block's sum: a word of the quotient, shifted, added into the sum.
            var quotient = DivideStep(word, sumMultiplier, sumOdd, sumInverse, ref sumCarry, ref sumBorrow);
            if (sumTwos == 0)
            {
                AddWord(ref s, i, quotient, ref addCarry);
            }
            else
            {
                if (i > 0)
                {
                    AddWord(ref s, i - 1, (sumPrevious >> sumTwos) | (quotient << (64 - sumTwos)), ref addCarry);
                }

                sumPrevious = quotient;
            }

            // The next term, written over the word just read (or the one before it).
            if (advance)
            {
                var next = DivideStep(word, nextMultiplier, nextOdd, nextInverse, ref nextCarry, ref nextBorrow);
                if (nextTwos == 0)
                {
                    Unsafe.Add(ref x, i) = next;
                }
                else
                {
                    if (i > 0)
                    {
                        Unsafe.Add(ref x, i - 1) = (nextPrevious >> nextTwos) | (next << (64 - nextTwos));
                    }

                    nextPrevious = next;
                }
            }
        }

        var end = length + 1;
        if (sumTwos > 0)
        {
            AddWord(ref s, length, sumPrevious >> sumTwos, ref addCarry);
        }

        for (var i = end; addCarry != 0; i++)
        {
            AddWord(ref s, i, 0, ref addCarry);
        }

        Debug.Assert(sumBorrow == 0 && nextBorrow == 0, "A division was not exact.");
        if (!advance)
        {
            return length;
        }

        if (nextTwos > 0)
        {
            Unsafe.Add(ref x, length) = nextPrevious >> nextTwos;
        }

        while (end > 0 && term[end - 1] == 0)
        {
            end--;
        }

        return end;
    }

    // One word of (product = word · multiplier + carry) / odd, exactly, from the low word up: the low word of the
    // remaining product, less the borrow, times the inverse of odd modulo 2^64.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong DivideStep(ulong word, ulong multiplier, ulong odd, ulong inverse, ref ulong carry, ref ulong borrow)
    {
        var high = Math.BigMul(word, multiplier, out var low);
        low += carry;
        high += low < carry ? 1UL : 0UL;
        carry = high;
        var under = low < borrow ? 1UL : 0UL;
        var quotient = (low - borrow) * inverse;
        borrow = Math.BigMul(quotient, odd, out _) + under;
        return quotient;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddWord(ref ulong sum, int index, ulong value, ref ulong carry)
    {
        ref var target = ref Unsafe.Add(ref sum, index);
        var total = target + value;
        var overflow = total < value ? 1UL : 0UL;
        var withCarry = total + carry;
        overflow += withCarry < carry ? 1UL : 0UL;
        target = withCarry;
        carry = overflow;
    }

    // The inverse of an odd number modulo 2^64, by Newton's iteration (each step doubles the correct low bits).
    private static ulong Inverse(ulong odd)
    {
        var inverse = odd;   // correct to 3 bits: odd · odd ≡ 1 (mod 8)
        for (var i = 0; i < 5; i++)
        {
            inverse *= 2 - (odd * inverse);
        }

        return inverse;
    }

    /// <summary>A non-negative dyadic rational, <c>Mantissa · 2^Exponent</c>, compared exactly.</summary>
    internal readonly record struct Dyadic(BigInteger Mantissa, long Exponent) : IComparable<Dyadic>
    {
        public static Dyadic Zero => new(BigInteger.Zero, 0);

        /// <summary><c>2^power − value</c>, for a value not above <c>2^power</c>.</summary>
        public static Dyadic PowerOfTwoMinus(int power, Dyadic value)
        {
            if (value.Exponent >= 0)
            {
                return new Dyadic((BigInteger.One << power) - (value.Mantissa << (int)value.Exponent), 0);
            }

            return new Dyadic((BigInteger.One << (int)(power - value.Exponent)) - value.Mantissa, value.Exponent);
        }

        public int CompareTo(Dyadic other)
        {
            if (Mantissa.IsZero || other.Mantissa.IsZero)
            {
                return Mantissa.Sign.CompareTo(other.Mantissa.Sign);
            }

            // Different bit lengths of the values decide at once; otherwise the exponents differ by little.
            var length = (long)Mantissa.GetBitLength() + Exponent;
            var otherLength = (long)other.Mantissa.GetBitLength() + other.Exponent;
            if (length != otherLength)
            {
                return length.CompareTo(otherLength);
            }

            return Exponent >= other.Exponent
                ? (Mantissa << (int)(Exponent - other.Exponent)).CompareTo(other.Mantissa)
                : Mantissa.CompareTo(other.Mantissa << (int)(other.Exponent - Exponent));
        }
    }
}
