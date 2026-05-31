// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Pure factory methods for creating <see cref="DistributionStatistics"/> from value collections.
/// Self-contained — all math helpers are inlined so this class has no external dependencies
/// beyond BCL, making it safe to live in <c>AgentEval.Abstractions</c>.
/// </summary>
/// <remarks>
/// Extracted from <c>StatisticsCalculator</c> during Phase 0.6. Mean, Median,
/// and Percentile were inlined during Phase 1 to remove the back-reference to
/// <c>StatisticsCalculator</c>.
/// </remarks>
public static class DistributionStatisticsFactory
{
    /// <summary>
    /// Creates distribution statistics from a list of double values.
    /// </summary>
    public static DistributionStatistics Create(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return new DistributionStatistics(0, 0, 0, 0, 0, 0, 0, 0);
        }

        // PERF-06: sort ONCE and reuse the ordered array for Min/Max/Median/percentiles. Previously each
        // of Median + Percentile(25/75/95) called values.OrderBy().ToList() independently — four full sorts
        // per Create, plus separate Min()/Max() passes.
        var sorted = new double[values.Count];
        for (int i = 0; i < values.Count; i++) sorted[i] = values[i];
        Array.Sort(sorted);

        return new DistributionStatistics(
            Min: sorted[0],
            Max: sorted[^1],
            Mean: Mean(values),
            Median: MedianSorted(sorted),
            Percentile25: PercentileSorted(sorted, 25),
            Percentile75: PercentileSorted(sorted, 75),
            Percentile95: PercentileSorted(sorted, 95),
            SampleSize: values.Count);
    }

    /// <summary>
    /// Creates distribution statistics from a list of time spans (converted to milliseconds).
    /// </summary>
    public static DistributionStatistics Create(IReadOnlyList<TimeSpan> values)
    {
        return Create(values.Select(v => v.TotalMilliseconds).ToList());
    }

    /// <summary>
    /// Creates distribution statistics from integer values.
    /// </summary>
    public static DistributionStatistics Create(IReadOnlyList<int> values)
    {
        return Create(values.Select(v => (double)v).ToList());
    }

    /// <summary>
    /// Creates distribution statistics from decimal values.
    /// </summary>
    public static DistributionStatistics Create(IReadOnlyList<decimal> values)
    {
        return Create(values.Select(v => (double)v).ToList());
    }

    // --- Inlined math helpers (copied from StatisticsCalculator) ---

    private static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        return values.Sum() / values.Count;
    }

    // The Median/Percentile helpers now take an ALREADY-SORTED span so Create sorts only once (PERF-06).
    private static double MedianSorted(ReadOnlySpan<double> sorted)
    {
        if (sorted.Length == 0) return 0;
        int mid = sorted.Length / 2;

        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private static double PercentileSorted(ReadOnlySpan<double> sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;

        if (percentile == 0) return sorted[0];
        if (percentile == 100) return sorted[^1];

        double index = (percentile / 100.0) * (sorted.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);

        if (lower == upper) return sorted[lower];

        // Linear interpolation
        double fraction = index - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
}
