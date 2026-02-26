// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Pure factory methods for creating <see cref="DistributionStatistics"/> from value collections.
/// Lightweight utility that can move to Abstractions in Phase 1 alongside
/// <see cref="DistributionStatistics"/> and <see cref="StochasticResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="StatisticsCalculator"/> during Phase 0.6 to decouple
/// <see cref="StochasticResult"/> from the full statistics calculator.
/// </para>
/// <para>
/// Delegates to <see cref="StatisticsCalculator"/> for Mean, Median, and Percentile
/// to avoid code duplication. In Phase 1, when this class moves to Abstractions,
/// the math helpers will be inlined or a shared utility extracted.
/// </para>
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

        return new DistributionStatistics(
            Min: values.Min(),
            Max: values.Max(),
            Mean: StatisticsCalculator.Mean(values),
            Median: StatisticsCalculator.Median(values),
            Percentile25: StatisticsCalculator.Percentile(values, 25),
            Percentile75: StatisticsCalculator.Percentile(values, 75),
            Percentile95: StatisticsCalculator.Percentile(values, 95),
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
}
