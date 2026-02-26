// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Default implementation of <see cref="IStatisticsCalculator"/> that delegates to <see cref="StatisticsCalculator"/> static methods.
/// This adapter allows dependency injection while maintaining backward compatibility with existing static usage.
/// </summary>
/// <remarks>
/// This implementation is stateless and thread-safe. The singleton instance can be shared across
/// the application. For custom statistical implementations, implement <see cref="IStatisticsCalculator"/> directly.
/// </remarks>
public sealed class DefaultStatisticsCalculator : IStatisticsCalculator
{
    /// <summary>
    /// Singleton instance for use in dependency injection.
    /// Using a singleton is safe because the implementation is stateless.
    /// </summary>
    public static IStatisticsCalculator Instance { get; } = new DefaultStatisticsCalculator();

    /// <summary>
    /// Public constructor for dependency injection container.
    /// For most scenarios, prefer using the <see cref="Instance"/> singleton.
    /// </summary>
    public DefaultStatisticsCalculator() { }

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
        => DistributionStatisticsFactory.Create(values);
    
    /// <inheritdoc />
    public DistributionStatistics CreateDistribution(IReadOnlyList<TimeSpan> values) 
        => DistributionStatisticsFactory.Create(values);
    
    /// <inheritdoc />
    public DistributionStatistics CreateDistribution(IReadOnlyList<int> values) 
        => DistributionStatisticsFactory.Create(values);
    
    /// <inheritdoc />
    public DistributionStatistics CreateDistribution(IReadOnlyList<decimal> values) 
        => DistributionStatisticsFactory.Create(values);
}
