// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Provides statistical calculations for stochastic testing.
/// </summary>
public static class StatisticsCalculator
{
    /// <summary>
    /// Calculates the mean of a sequence of values.
    /// </summary>
    public static double Mean(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        return values.Sum() / values.Count;
    }
    
    /// <summary>
    /// Calculates the median of a sequence of values.
    /// </summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
    
    /// <summary>
    /// Calculates the standard deviation of a sequence of values.
    /// </summary>
    public static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2) return 0;
        
        double mean = Mean(values);
        double sumSquaredDiff = values.Sum(v => (v - mean) * (v - mean));
        
        // Sample standard deviation (n-1)
        return Math.Sqrt(sumSquaredDiff / (values.Count - 1));
    }
    
    /// <summary>
    /// Calculates a percentile of a sequence of values.
    /// </summary>
    /// <param name="values">The values to analyze.</param>
    /// <param name="percentile">The percentile to calculate (0-100).</param>
    public static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        if (percentile < 0 || percentile > 100)
            throw new ArgumentOutOfRangeException(nameof(percentile), "Must be between 0 and 100.");
        
        var sorted = values.OrderBy(v => v).ToList();
        
        if (percentile == 0) return sorted[0];
        if (percentile == 100) return sorted[^1];
        
        double index = (percentile / 100.0) * (sorted.Count - 1);
        int lower = (int)Math.Floor(index);
        int upper = (int)Math.Ceiling(index);
        
        if (lower == upper) return sorted[lower];
        
        // Linear interpolation
        double fraction = index - lower;
        return sorted[lower] + (sorted[upper] - sorted[lower]) * fraction;
    }
    
    /// <summary>
    /// Calculates a confidence interval for the mean.
    /// </summary>
    /// <param name="values">The values to analyze.</param>
    /// <param name="confidenceLevel">The confidence level (e.g., 0.95 for 95%).</param>
    public static ConfidenceInterval CalculateConfidenceInterval(IReadOnlyList<double> values, double confidenceLevel = 0.95)
    {
        if (values.Count < 2)
            return new ConfidenceInterval(0, 0, confidenceLevel);
        
        double mean = Mean(values);
        double stdDev = StandardDeviation(values);
        double standardError = stdDev / Math.Sqrt(values.Count);
        
        // Use t-distribution critical value
        // For simplicity, using approximate values for common confidence levels
        double tCritical = GetTCriticalValue(values.Count - 1, confidenceLevel);
        double marginOfError = tCritical * standardError;
        
        return new ConfidenceInterval(
            Lower: mean - marginOfError,
            Upper: mean + marginOfError,
            Level: confidenceLevel);
    }
    
    /// <summary>
    /// Gets the t-critical value for a given degrees of freedom and confidence level.
    /// Uses approximation for common values.
    /// </summary>
    private static double GetTCriticalValue(int degreesOfFreedom, double confidenceLevel)
    {
        // Common t-values for two-tailed tests
        // For a full implementation, use a statistics library
        
        double alpha = 1 - confidenceLevel;
        
        // Approximation using normal distribution for large samples
        if (degreesOfFreedom >= 120)
        {
            return confidenceLevel switch
            {
                >= 0.99 => 2.576,
                >= 0.95 => 1.96,
                >= 0.90 => 1.645,
                _ => 1.96
            };
        }
        
        // Use a simplified lookup for smaller samples
        // These are approximate values
        if (confidenceLevel >= 0.95)
        {
            return degreesOfFreedom switch
            {
                1 => 12.706,
                2 => 4.303,
                3 => 3.182,
                4 => 2.776,
                5 => 2.571,
                6 => 2.447,
                7 => 2.365,
                8 => 2.306,
                9 => 2.262,
                10 => 2.228,
                15 => 2.131,
                20 => 2.086,
                30 => 2.042,
                60 => 2.000,
                _ => 2.0 + 0.5 / Math.Sqrt(degreesOfFreedom)
            };
        }
        
        // Default fallback
        return 1.96;
    }
    
    /// <summary>
    /// Calculates pass rate from boolean results.
    /// </summary>
    public static double CalculatePassRate(IReadOnlyList<bool> results)
    {
        if (results.Count == 0) return 0;
        return (double)results.Count(r => r) / results.Count;
    }
    
    /// <summary>
    /// Creates full statistics from a list of scores and pass/fail results.
    /// </summary>
    public static StochasticStatistics CreateStatistics(
        IReadOnlyList<int> scores,
        IReadOnlyList<bool> passResults,
        double confidenceLevel = 0.95)
    {
        var doubleScores = scores.Select(s => (double)s).ToList();
        
        return new StochasticStatistics(
            PassRate: CalculatePassRate(passResults),
            MeanScore: Mean(doubleScores),
            MedianScore: Median(doubleScores),
            StandardDeviation: StandardDeviation(doubleScores),
            MinScore: scores.Count > 0 ? scores.Min() : 0,
            MaxScore: scores.Count > 0 ? scores.Max() : 0,
            Percentile25: Percentile(doubleScores, 25),
            Percentile75: Percentile(doubleScores, 75),
            Percentile95: Percentile(doubleScores, 95),
            ConfidenceInterval: CalculateConfidenceInterval(doubleScores, confidenceLevel),
            SampleSize: scores.Count);
    }

    /// <summary>
    /// Creates distribution statistics from a list of values.
    /// </summary>
    public static DistributionStatistics CreateDistribution(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return new DistributionStatistics(0, 0, 0, 0, 0, 0, 0, 0);
        }
        
        var sorted = values.OrderBy(v => v).ToList();
        return new DistributionStatistics(
            Min: sorted.First(),
            Max: sorted.Last(),
            Mean: Mean(values),
            Median: Median(values),
            Percentile25: Percentile(values, 25),
            Percentile75: Percentile(values, 75),
            Percentile95: Percentile(values, 95),
            SampleSize: values.Count);
    }
    
    /// <summary>
    /// Creates distribution statistics from a list of time spans (converted to milliseconds).
    /// </summary>
    public static DistributionStatistics CreateDistribution(IReadOnlyList<TimeSpan> values)
    {
        return CreateDistribution(values.Select(v => v.TotalMilliseconds).ToList());
    }
    
    /// <summary>
    /// Creates distribution statistics from integer values.
    /// </summary>
    public static DistributionStatistics CreateDistribution(IReadOnlyList<int> values)
    {
        return CreateDistribution(values.Select(v => (double)v).ToList());
    }
    
    /// <summary>
    /// Creates distribution statistics from decimal values.
    /// </summary>
    public static DistributionStatistics CreateDistribution(IReadOnlyList<decimal> values)
    {
        return CreateDistribution(values.Select(v => (double)v).ToList());
    }
}
