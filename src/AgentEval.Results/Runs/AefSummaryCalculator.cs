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
/// <param name="Sum">The sum of the measured values, computed exactly and rounded once to binary64.</param>
/// <param name="SumOfSquares">The sum of their squares (each square rounded to binary64, then summed exactly).</param>
/// <param name="Value">
/// The entry's value: null when <c>n</c> is 0; otherwise <c>sum</c> for a metric of kind <c>count</c> and
/// <c>sum / n</c> for any other, or for an <c>aggregate</c> its method's value when AEF defines the method
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
            null => new AefSummaryFigures(total, n, sum, sumOfSquares, metricKind == "count" ? sum : sum / n, true, values),
            "median" => new AefSummaryFigures(total, n, sum, sumOfSquares, Median(values), true, values),
            "min" => new AefSummaryFigures(total, n, sum, sumOfSquares, values.Min(), true, values),
            "max" => new AefSummaryFigures(total, n, sum, sumOfSquares, values.Max(), true, values),
            _ => new AefSummaryFigures(total, n, sum, sumOfSquares, null, false, values),
        };
    }

    /// <summary>
    /// §3.6: a written <c>value</c> or <c>sum</c> matches the recomputed one when they differ by at most
    /// 1e-9 × max(1, |recomputed|).
    /// </summary>
    public static bool Matches(double written, double recomputed) =>
        Math.Abs(written - recomputed) <= 1e-9 * Math.Max(1, Math.Abs(recomputed));

    /// <summary>
    /// [SUM-8]: the median of the values; the median of an even count is the mean of the two middle values.
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
    /// (1e20 + 1 − 1e20 is 0 in order, 1 exactly).
    /// </summary>
    public static double ExactSum(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var partials = new List<double>();
        foreach (var value in values)
        {
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
            if (twice == adjusted - sum)
            {
                sum = adjusted;
            }
        }

        return sum;
    }
}
