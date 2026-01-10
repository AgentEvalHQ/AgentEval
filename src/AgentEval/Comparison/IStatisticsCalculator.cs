// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Provides statistical calculations for stochastic testing and model comparison.
/// Interface enables testability and dependency injection.
/// </summary>
public interface IStatisticsCalculator
{
    /// <summary>
    /// Calculates the mean of a sequence of values.
    /// </summary>
    double Mean(IReadOnlyList<double> values);
    
    /// <summary>
    /// Calculates the median of a sequence of values.
    /// </summary>
    double Median(IReadOnlyList<double> values);
    
    /// <summary>
    /// Calculates the standard deviation of a sequence of values.
    /// </summary>
    double StandardDeviation(IReadOnlyList<double> values);
    
    /// <summary>
    /// Calculates a percentile of a sequence of values.
    /// </summary>
    /// <param name="values">The values to analyze.</param>
    /// <param name="percentile">The percentile to calculate (0-100).</param>
    double Percentile(IReadOnlyList<double> values, double percentile);
    
    /// <summary>
    /// Calculates a confidence interval for the mean.
    /// </summary>
    /// <param name="values">The values to analyze.</param>
    /// <param name="confidenceLevel">The confidence level (e.g., 0.95 for 95%).</param>
    ConfidenceInterval CalculateConfidenceInterval(IReadOnlyList<double> values, double confidenceLevel = 0.95);
    
    /// <summary>
    /// Calculates pass rate from boolean results.
    /// </summary>
    double CalculatePassRate(IReadOnlyList<bool> results);
    
    /// <summary>
    /// Creates full statistics from a list of scores and pass/fail results.
    /// </summary>
    StochasticStatistics CreateStatistics(
        IReadOnlyList<int> scores,
        IReadOnlyList<bool> passResults,
        double confidenceLevel = 0.95);

    /// <summary>
    /// Creates distribution statistics from a list of values.
    /// </summary>
    DistributionStatistics CreateDistribution(IReadOnlyList<double> values);
}
