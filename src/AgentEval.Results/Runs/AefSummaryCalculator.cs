// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Results.Runs;

/// <summary>
/// What one summary entry's figures are (contracts/aef/1/spec/03-run.md, [SUM-3]–[SUM-5], [SUM-8]): <see cref="N"/>
/// (the lines not left out), <see cref="Measured"/> (<c>n</c>), <see cref="NotMeasured"/>, <see cref="Sum"/> and
/// <see cref="SumOfSquares"/> of the measured values, the measured <see cref="Values"/> themselves in line order, and
/// <see cref="Value"/>.
/// </summary>
/// <param name="N">The number of lines that are not left out.</param>
/// <param name="Measured">The number of measured lines, <c>n</c>.</param>
/// <param name="Sum">
/// The sum of the measured values, computed exactly and rounded once to binary64; an infinity when that sum is beyond
/// binary64 ([SUM-5]: the entry then omits <c>sum</c> and <c>sumSq</c>, and without an aggregate its value is null).
/// </param>
/// <param name="SumOfSquares">The sum of their squares (each square rounded to binary64, then summed exactly).</param>
/// <param name="Value">
/// The entry's value: null when <c>n</c> is 0, or, without an aggregate, when the sum is beyond binary64; otherwise
/// <c>sum</c> for a metric of kind <c>count</c> and <c>sum / n</c> for any other, or for an <c>aggregate</c> its method's
/// value when AEF defines the method
/// (<c>median</c>, <c>min</c>, <c>max</c>). Null also when the method is the producer's (<see cref="ValueDefined"/> false).
/// </param>
/// <param name="ValueDefined">
/// Whether <see cref="Value"/> is the value AEF defines and a verifier recomputes: false only for an aggregate whose
/// method is the producer's (pass@k, F1, …) and <c>n</c> is not 0 ([SUM-8]: shown as written, never recomputed).
/// </param>
/// <param name="Values">The measured values, in results.ndjson order.</param>
public sealed record AefSummaryFigures(long N, long Measured, double Sum, double SumOfSquares, double? Value, bool ValueDefined, IReadOnlyList<double> Values)
{
    /// <summary><c>notMeasured</c> = <c>N</c> − <c>n</c>.</summary>
    public long NotMeasured => N - Measured;

    /// <summary>
    /// Whether the entry reads as not measured ([SUM-5], [SUM-6]): <c>n</c> is 0, or, without an aggregate, the sum is
    /// beyond binary64 (no mean): its verdict is <c>not_measured</c>, and the producer's verdict is not asked for.
    /// </summary>
    public bool ReadsNotMeasured => Measured == 0 || (ValueDefined && Value is null);
}

/// <summary>
/// The summary recomputation of [SUM-3]–[SUM-9], shared by the writer (which computes a summary.json) and the run
/// verifier (which recomputes each entry of one, §3.9 <c>summary</c>). It reads result lines as written
/// (<see cref="JsonObject"/>s valid against the result schema).
/// </summary>
public static class AefSummaryCalculator
{
    /// <summary>The aggregate methods AEF defines, whose value a verifier recomputes ([SUM-8]).</summary>
    public static IReadOnlyList<string> DefinedMethods { get; } = ["median", "min", "max"];

    /// <summary>
    /// [SUM-3]: whether a result line belongs to <paramref name="lane"/>: its <c>lane</c> names it, or it has none and
    /// the summary has a single lane (<paramref name="summaryLanes"/>: the summary's lane names) and it is this one.
    /// </summary>
    public static bool Belongs(JsonObject line, string lane, IEnumerable<string> summaryLanes)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(summaryLanes);
        if (line.ContainsKey("lane"))
        {
            return string.Equals(AefNode.String(line["lane"]), lane, StringComparison.Ordinal);
        }

        var distinct = summaryLanes.Distinct(StringComparer.Ordinal).Take(2).ToList();
        return distinct.Count == 1 && string.Equals(distinct[0], lane, StringComparison.Ordinal);
    }

    /// <summary>
    /// [SUM-4]: what one line gives an entry for <paramref name="metricId"/> of kind <paramref name="metricKind"/>:
    /// left out (<c>not_applicable</c>: the result's <c>LeftOut</c> is true), not measured (null), or measured with a
    /// value. <c>not_measured</c>, <c>skipped</c>, <c>error</c> and <c>pending</c> are not measured. Otherwise a metric
    /// of kind <c>rate</c> or <c>verdict</c> takes 1 for <c>passed</c> and 0 for <c>failed</c>, <c>warn</c> and
    /// <c>inconclusive</c>, and a <c>scored</c> line (no verdict) is not measured; any other kind takes the line's score
    /// for the metric, and a line with none is not measured. A line that scores the metric twice is not measured for it
    /// (§3.9 <c>metric</c>).
    /// </summary>
    public static (bool LeftOut, double? Value) ValueOf(JsonObject line, string metricId, string metricKind)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(metricId);
        ArgumentNullException.ThrowIfNull(metricKind);
        var state = AefNode.String(line["state"]);
        if (state == "not_applicable")
        {
            return (true, null);
        }

        if (state is "not_measured" or "skipped" or "error" or "pending")
        {
            return (false, null);
        }

        var scores = AefNode.Objects(line["scores"]).Where(s => AefNode.String(s["metric"]) == metricId).ToList();
        if (scores.Count > 1)
        {
            return (false, null);
        }

        if (metricKind is "rate" or "verdict")
        {
            return state == "scored" ? (false, null) : (false, state == "passed" ? 1 : 0);
        }

        return (false, scores.Count == 1 ? AefNode.Number(scores[0]["value"]) : null);
    }

    /// <summary>
    /// [SUM-3]–[SUM-5] and [SUM-8]: the figures of the entry for <paramref name="lane"/>, <paramref name="metricId"/> (of
    /// kind <paramref name="metricKind"/>) and <paramref name="path"/>, over the result lines of a run. Trial lines are
    /// not counted (their rollup is); a line belongs to the lane as <see cref="Belongs"/> says, at exactly this path.
    /// </summary>
    /// <param name="results">The run's result lines, in file order.</param>
    /// <param name="summaryLanes">The summary's lane names (a line without <c>lane</c> belongs to the only one).</param>
    /// <param name="lane">The entry's lane.</param>
    /// <param name="metricId">The entry's metric.</param>
    /// <param name="metricKind">The metric's kind, as metrics.json declares it.</param>
    /// <param name="path">The entry's path.</param>
    /// <param name="aggregateMethod">The entry's <c>aggregate.method</c>, or null for the mean (or the sum for a count).</param>
    public static AefSummaryFigures Compute(
        IEnumerable<JsonObject> results,
        IReadOnlyCollection<string> summaryLanes,
        string lane,
        string metricId,
        string metricKind,
        string path,
        string? aggregateMethod = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(summaryLanes);
        ArgumentNullException.ThrowIfNull(path);
        long n = 0, total = 0;
        var values = new List<double>();
        foreach (var line in results)
        {
            if (line.ContainsKey("trial") || !string.Equals(AefNode.String(line["path"]), path, StringComparison.Ordinal)
                || !Belongs(line, lane, summaryLanes))
            {
                continue;
            }

            var (leftOut, value) = ValueOf(line, metricId, metricKind);
            if (leftOut)
            {
                continue;
            }

            total++;
            if (value is { } measured)
            {
                n++;
                values.Add(measured);
            }
        }

        var sum = ExactSum(values);
        var sumOfSquares = ExactSum(values.Select(v => v * v));
        if (n == 0)
        {
            return new AefSummaryFigures(total, 0, sum, sumOfSquares, null, true, values);
        }

        return aggregateMethod switch
        {
            // [SUM-5]: a sum beyond binary64 has no mean (nor is it a count's value): the value is null, as when n is 0.
            null when !double.IsFinite(sum) => new AefSummaryFigures(total, n, sum, sumOfSquares, null, true, values),
            null => new AefSummaryFigures(total, n, sum, sumOfSquares, metricKind == "count" ? sum : sum / n, true, values),
            "median" => new AefSummaryFigures(total, n, sum, sumOfSquares, Median(values), true, values),
            "min" => new AefSummaryFigures(total, n, sum, sumOfSquares, values.Min(), true, values),
            "max" => new AefSummaryFigures(total, n, sum, sumOfSquares, values.Max(), true, values),
            _ => new AefSummaryFigures(total, n, sum, sumOfSquares, null, false, values),
        };
    }

    /// <summary>
    /// §3.6, [SUM-5], [SUM-8]: a written <c>sum</c> or <c>value</c> (the mean, the sum of a count, or a median, minimum
    /// or maximum) is the recomputed binary64 value exactly: each is defined to one value (the sum computed exactly and
    /// rounded once; the mean that sum divided by <c>n</c> in one binary64 division; an even count's median the exact
    /// mean of the two middle values, rounded once). A recomputed value that is not finite matches no written number
    /// ([ENC-3]).
    /// </summary>
    public static bool Same(double written, double recomputed) => double.IsFinite(recomputed) && written == recomputed;

    /// <summary>
    /// §3.6: a written <c>sumSq</c>, the one figure not defined to one value, matches the recomputed one when they differ
    /// by at most 1e-9 × max(1, |recomputed|). A recomputed value that is not finite (a sum that overflows binary64)
    /// matches no written number: JSON has none for it ([ENC-3]).
    /// </summary>
    public static bool Matches(double written, double recomputed) =>
        double.IsFinite(recomputed) && Math.Abs(written - recomputed) <= 1e-9 * Math.Max(1, Math.Abs(recomputed));

    /// <summary>
    /// [SUM-8]: the median of the values; the median of an even count is the exact mean of the two middle values, rounded
    /// once: their sum rounded once, halved (exact, or the one rounding where the sum is too small to halve exactly); and
    /// where the sum overflows, the exact halves added and rounded once.
    /// </summary>
    /// <exception cref="ArgumentException">No values.</exception>
    public static double Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            throw new ArgumentException("The median of no values is not defined.", nameof(values));
        }

        var middle = sorted.Length / 2;
        if (sorted.Length % 2 == 1)
        {
            return sorted[middle];
        }

        var (a, b) = (sorted[middle - 1], sorted[middle]);
        var mean = (a + b) / 2;
        return double.IsFinite(mean) ? mean : (a / 2) + (b / 2);   // a + b can overflow where the mean does not
    }

    /// <summary>
    /// The sum of binary64 values computed exactly and rounded once, to nearest with ties to even (Shewchuk's
    /// algorithm: a list of non-overlapping partial sums is kept exact; they are added from the largest at the end).
    /// Summing in order would make the result depend on the order and lose small values next to large ones
    /// (1e20 + 1 − 1e20 is 0 in order, 1 exactly). A sum whose partial sums leave binary64 (1.5e308 + 1.5e308) is
    /// summed again in integers, so an exact sum within binary64 (1e308 + 1e308 − 1e308) is still found; one beyond it
    /// is an infinity of its sign ([SUM-5]: no binary64 value). A value that is not finite makes the sum what binary64
    /// addition makes it.
    /// </summary>
    public static double ExactSum(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values as IReadOnlyList<double> ?? values.ToList();
        var partials = new List<double>();
        foreach (var value in list)
        {
            if (!double.IsFinite(value))
            {
                return list.Sum();
            }

            var x = value;
            var kept = 0;
            for (var j = 0; j < partials.Count; j++)
            {
                var y = partials[j];
                if (Math.Abs(x) < Math.Abs(y))
                {
                    (x, y) = (y, x);
                }

                var high = x + y;
                if (!double.IsFinite(high))
                {
                    return ExactSumInIntegers(list);
                }

                var low = y - (high - x);   // the rounding error of x + y, exactly
                if (low != 0)
                {
                    partials[kept++] = low;
                }

                x = high;
            }

            partials.RemoveRange(kept, partials.Count - kept);
            partials.Add(x);
        }

        if (partials.Count == 0)
        {
            return 0;
        }

        // Add the partials from the largest, until a rounding error appears; then correct a tie to even.
        var i = partials.Count - 1;
        var sum = partials[i];
        double error = 0;
        while (i > 0)
        {
            var next = partials[--i];
            var high = sum + next;
            if (!double.IsFinite(high))
            {
                return ExactSumInIntegers(list);
            }

            error = next - (high - sum);
            sum = high;
            if (error != 0)
            {
                break;
            }
        }

        if (i > 0 && ((error < 0 && partials[i - 1] < 0) || (error > 0 && partials[i - 1] > 0)))
        {
            // The rest pushes past the halfway point: round away from the tie.
            var twice = error * 2;
            var adjusted = sum + twice;
            if (!double.IsFinite(adjusted))
            {
                return ExactSumInIntegers(list);
            }

            if (twice == adjusted - sum)
            {
                sum = adjusted;
            }
        }

        return sum;
    }

    // The exact sum of finite binary64 values, each an integer number of 2^-1074 (the least subnormal), added as
    // integers and rounded once to nearest, ties to even: an infinity of its sign when it is beyond binary64.
    private static double ExactSumInIntegers(IReadOnlyList<double> values)
    {
        var total = System.Numerics.BigInteger.Zero;
        foreach (var value in values)
        {
            var bits = BitConverter.DoubleToInt64Bits(value);
            var exponent = (int)((bits >> 52) & 0x7FF);
            var fraction = bits & ((1L << 52) - 1);
            var (mantissa, scale) = exponent == 0 ? (fraction, 0) : (fraction | (1L << 52), exponent - 1);
            var units = new System.Numerics.BigInteger(mantissa) << scale;   // value / 2^-1074
            total += value < 0 ? -units : units;
        }

        if (total.IsZero)
        {
            return 0;
        }

        var negative = total.Sign < 0;
        var magnitude = System.Numerics.BigInteger.Abs(total);
        var length = (int)magnitude.GetBitLength();
        if (length <= 53)
        {
            var exact = Math.ScaleB((double)(long)magnitude, -1074);   // at most 53 bits: exactly a binary64 value
            return negative ? -exact : exact;
        }

        var shift = length - 53;
        var kept = magnitude >> shift;
        var rest = magnitude - (kept << shift);
        var half = System.Numerics.BigInteger.One << (shift - 1);
        if (rest > half || (rest == half && !kept.IsEven))
        {
            kept += 1;
        }

        var rounded = Math.ScaleB((double)(long)kept, shift - 1074);   // beyond binary64: an infinity
        return negative ? -rounded : rounded;
    }
}
