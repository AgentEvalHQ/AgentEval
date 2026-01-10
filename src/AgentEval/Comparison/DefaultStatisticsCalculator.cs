// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Default implementation of IStatisticsCalculator that delegates to StatisticsCalculator static methods.
/// This allows dependency injection while maintaining backward compatibility with existing static usage.
/// </summary>
public class DefaultStatisticsCalculator : IStatisticsCalculator
{
    /// <summary>
    /// Singleton instance for use in dependency injection.
    /// </summary>
    public static IStatisticsCalculator Instance { get; } = new DefaultStatisticsCalculator();

    /// <inheritdoc />
    public double Mean(IReadOnlyList<double> values) 
        => StatisticsCalculator.Mean(values);

    /// <inheritdoc />
    public double Median(IReadOnlyList<double> values) 
        => StatisticsCalculator.Median(values);

    /// <inheritdoc />
    public double StandardDeviation(IReadOnlyList<double> values) 
        => StatisticsCalculator.StandardDeviation(values);

    /// <inheritdoc />
    public double Percentile(IReadOnlyList<double> values, double percentile) 
        => StatisticsCalculator.Percentile(values, percentile);

    /// <inheritdoc />
    public ConfidenceInterval CalculateConfidenceInterval(IReadOnlyList<double> values, double confidenceLevel = 0.95) 
        => StatisticsCalculator.CalculateConfidenceInterval(values, confidenceLevel);

    /// <inheritdoc />
    public double CalculatePassRate(IReadOnlyList<bool> results) 
        => StatisticsCalculator.CalculatePassRate(results);

    /// <inheritdoc />
    public StochasticStatistics CreateStatistics(
        IReadOnlyList<int> scores,
        IReadOnlyList<bool> passResults,
        double confidenceLevel = 0.95) 
        => StatisticsCalculator.CreateStatistics(scores, passResults, confidenceLevel);

    /// <inheritdoc />
    public DistributionStatistics CreateDistribution(IReadOnlyList<double> values) 
        => StatisticsCalculator.CreateDistribution(values);
}
