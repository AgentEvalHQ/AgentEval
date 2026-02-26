// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Provides statistical calculations for stochastic evaluation and model comparison.
/// This interface enables testability through dependency injection and allows
/// for alternative statistical implementations (e.g., GPU-accelerated, distributed).
/// </summary>
/// <remarks>
/// Implementations should be stateless and thread-safe to support concurrent operations.
/// All methods should handle empty collections gracefully by returning sensible defaults (e.g., 0).
/// </remarks>
public interface IStatisticsCalculator
{
    /// <summary>
    /// Calculates the arithmetic mean of a sequence of values.
    /// </summary>
    /// <param name="values">The values to analyze. Empty collections return 0.</param>
    /// <returns>The mean value, or 0 if the collection is empty.</returns>
    double Mean(IReadOnlyList<double> values);
    
    /// <summary>
    /// Calculates the median of a sequence of values.
    /// </summary>
    /// <param name="values">The values to analyze. Empty collections return 0.</param>
    /// <returns>The median value, or 0 if the collection is empty.</returns>
    double Median(IReadOnlyList<double> values);
    
    /// <summary>
    /// Calculates the standard deviation of a sequence of values.
    /// </summary>
    /// <param name="values">The values to analyze. Collections with less than 2 items return 0.</param>
    /// <returns>The standard deviation, or 0 if insufficient data.</returns>
    double StandardDeviation(IReadOnlyList<double> values);
    
    /// <summary>
    /// Calculates a percentile of a sequence of values.
    /// </summary>
    /// <param name="values">The values to analyze.</param>
    /// <param name="percentile">The percentile to calculate (0-100). Values outside this range throw ArgumentOutOfRangeException.</param>
    /// <returns>The calculated percentile value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when percentile is not between 0 and 100.</exception>
    double Percentile(IReadOnlyList<double> values, double percentile);
    
    /// <summary>
    /// Calculates a confidence interval for the mean using t-distribution.
    /// </summary>
    /// <param name="values">The values to analyze. Collections with less than 2 items return (0, 0).</param>
    /// <param name="confidenceLevel">The confidence level (e.g., 0.95 for 95%). Must be between 0 and 1.</param>
    /// <returns>A confidence interval with lower and upper bounds.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when confidenceLevel is not between 0 and 1.</exception>
    ConfidenceInterval CalculateConfidenceInterval(IReadOnlyList<double> values, double confidenceLevel = 0.95);
    
    /// <summary>
    /// Calculates the pass rate (percentage of true values) from boolean results.
    /// </summary>
    /// <param name="results">The boolean results to analyze. Empty collections return 0.</param>
    /// <returns>The pass rate as a value between 0.0 and 1.0.</returns>
    double CalculatePassRate(IReadOnlyList<bool> results);
    
    /// <summary>
    /// Creates comprehensive statistics from test scores and pass/fail results.
    /// </summary>
    /// <param name="scores">The test scores.</param>
    /// <param name="passResults">The corresponding pass/fail results. Must have same count as scores.</param>
    /// <param name="confidenceLevel">The confidence level for intervals (e.g., 0.95 for 95%).</param>
    /// <returns>Complete stochastic statistics including mean, median, distribution, and confidence intervals.</returns>
    /// <exception cref="ArgumentException">Thrown when scores and passResults have different counts.</exception>
    StochasticStatistics CreateStatistics(
        IReadOnlyList<int> scores,
        IReadOnlyList<bool> passResults,
        double confidenceLevel = 0.95);

    /// <summary>
    /// Creates distribution statistics from a list of double values.
    /// </summary>
    /// <param name="values">The values to analyze.</param>
    /// <returns>Distribution statistics including min, max, mean, median, and percentiles.</returns>
    DistributionStatistics CreateDistribution(IReadOnlyList<double> values);
    
    /// <summary>
    /// Creates distribution statistics from a list of TimeSpan values.
    /// Values are converted to milliseconds for analysis.
    /// </summary>
    /// <param name="values">The time span values to analyze.</param>
    /// <returns>Distribution statistics with values in milliseconds.</returns>
    DistributionStatistics CreateDistribution(IReadOnlyList<TimeSpan> values);
    
    /// <summary>
    /// Creates distribution statistics from a list of integer values.
    /// Values are converted to doubles for analysis.
    /// </summary>
    /// <param name="values">The integer values to analyze.</param>
    /// <returns>Distribution statistics.</returns>
    DistributionStatistics CreateDistribution(IReadOnlyList<int> values);
    
    /// <summary>
    /// Creates distribution statistics from a list of decimal values.
    /// Values are converted to doubles for analysis.
    /// </summary>
    /// <param name="values">The decimal values to analyze.</param>
    /// <returns>Distribution statistics.</returns>
    DistributionStatistics CreateDistribution(IReadOnlyList<decimal> values);
}
