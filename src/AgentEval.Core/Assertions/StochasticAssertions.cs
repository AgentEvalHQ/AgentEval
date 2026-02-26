// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;

namespace AgentEval.Assertions;

/// <summary>
/// Fluent assertions for stochastic evaluation results.
/// </summary>
public class StochasticAssertions
{
    private readonly Comparison.StochasticResult _result;
    
    /// <summary>
    /// Creates assertions for a stochastic result.
    /// </summary>
    public StochasticAssertions(Comparison.StochasticResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }
    
    /// <summary>
    /// Assert that the pass rate meets or exceeds the threshold.
    /// </summary>
    /// <param name="threshold">Minimum pass rate (0.0-1.0). Default: uses the options threshold.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HavePassRateAtLeast(double? threshold = null, string? because = null)
    {
        double effectiveThreshold = threshold ?? _result.Options.SuccessRateThreshold;
        
        if (_result.Statistics.PassRate < effectiveThreshold)
        {
            throw new StochasticAssertionException(
                $"Expected pass rate >= {effectiveThreshold:P1}, but was {_result.Statistics.PassRate:P1}. " +
                $"({_result.PassedCount}/{_result.IndividualResults.Count} passed)" +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
    
    /// <summary>
    /// Assert that the mean score meets or exceeds a threshold.
    /// </summary>
    /// <param name="minimumScore">Minimum mean score required.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HaveMeanScoreAtLeast(double minimumScore, string? because = null)
    {
        if (_result.Statistics.MeanScore < minimumScore)
        {
            throw new StochasticAssertionException(
                $"Expected mean score >= {minimumScore:F2}, but was {_result.Statistics.MeanScore:F2}." +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
    
    /// <summary>
    /// Assert that the score standard deviation is within acceptable bounds.
    /// Low variance indicates consistent behavior.
    /// </summary>
    /// <param name="maxStandardDeviation">Maximum acceptable standard deviation.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HaveStandardDeviationAtMost(double maxStandardDeviation, string? because = null)
    {
        if (_result.Statistics.StandardDeviation > maxStandardDeviation)
        {
            throw new StochasticAssertionException(
                $"Expected standard deviation <= {maxStandardDeviation:F2}, but was {_result.Statistics.StandardDeviation:F2}. " +
                "Results are too inconsistent." +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
    
    /// <summary>
    /// Assert that the median score meets or exceeds a threshold.
    /// </summary>
    /// <param name="minimumScore">Minimum median score required.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HaveMedianScoreAtLeast(double minimumScore, string? because = null)
    {
        if (_result.Statistics.MedianScore < minimumScore)
        {
            throw new StochasticAssertionException(
                $"Expected median score >= {minimumScore:F2}, but was {_result.Statistics.MedianScore:F2}." +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
    
    /// <summary>
    /// Assert that the 95th percentile score meets or exceeds a threshold.
    /// Useful for ensuring most runs perform well.
    /// </summary>
    /// <param name="minimumScore">Minimum 95th percentile score.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HavePercentile95AtLeast(double minimumScore, string? because = null)
    {
        if (_result.Statistics.Percentile95 < minimumScore)
        {
            throw new StochasticAssertionException(
                $"Expected 95th percentile >= {minimumScore:F2}, but was {_result.Statistics.Percentile95:F2}." +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
    
    /// <summary>
    /// Assert that the minimum score across all runs meets a threshold.
    /// Ensures no run falls below acceptable quality.
    /// </summary>
    /// <param name="minimumScore">Minimum score required for any single run.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HaveMinScoreAtLeast(int minimumScore, string? because = null)
    {
        if (_result.Statistics.MinScore < minimumScore)
        {
            throw new StochasticAssertionException(
                $"Expected minimum score >= {minimumScore}, but was {_result.Statistics.MinScore}. " +
                "At least one run performed below the threshold." +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
    
    /// <summary>
    /// Assert that the confidence interval for mean score is above a threshold.
    /// </summary>
    /// <param name="minimumLowerBound">Minimum acceptable lower bound of confidence interval.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HaveConfidenceIntervalLowerBoundAtLeast(double minimumLowerBound, string? because = null)
    {
        if (_result.Statistics.ConfidenceInterval == null)
        {
            throw new StochasticAssertionException(
                "Confidence interval was not calculated. Enable statistical analysis in options.");
        }
        
        if (_result.Statistics.ConfidenceInterval.Lower < minimumLowerBound)
        {
            throw new StochasticAssertionException(
                $"Expected confidence interval lower bound >= {minimumLowerBound:F2}, " +
                $"but was {_result.Statistics.ConfidenceInterval.Lower:F2}. " +
                $"Full CI: {_result.Statistics.ConfidenceInterval}" +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
    
    /// <summary>
    /// Assert that no individual run failed.
    /// Strictest assertion - requires 100% pass rate.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HaveNoFailures(string? because = null)
    {
        if (_result.FailedCount > 0)
        {
            var failedResults = _result.IndividualResults
                .Select((r, i) => (Result: r, Index: i))
                .Where(x => !x.Result.Passed)
                .ToList();
            
            var failedIndices = string.Join(", ", failedResults.Select(x => x.Index + 1));
            
            throw new StochasticAssertionException(
                $"Expected no failures, but {_result.FailedCount} run(s) failed. " +
                $"Failed runs: {failedIndices}" +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
    
    /// <summary>
    /// Assert that the coefficient of variation (CV) is within bounds.
    /// CV = StdDev / Mean, useful for comparing variability across different score ranges.
    /// </summary>
    /// <param name="maxCoefficientOfVariation">Maximum acceptable CV (0.0-1.0 typically).</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public StochasticAssertions HaveCoefficientOfVariationAtMost(double maxCoefficientOfVariation, string? because = null)
    {
        if (_result.Statistics.MeanScore == 0)
        {
            throw new StochasticAssertionException(
                "Cannot calculate coefficient of variation with mean score of 0.");
        }
        
        double cv = _result.Statistics.StandardDeviation / _result.Statistics.MeanScore;
        
        if (cv > maxCoefficientOfVariation)
        {
            throw new StochasticAssertionException(
                $"Expected coefficient of variation <= {maxCoefficientOfVariation:F3}, but was {cv:F3}. " +
                "Results are too variable relative to the mean." +
                (because != null ? $" because {because}" : ""));
        }
        
        return this;
    }
}

/// <summary>
/// Exception thrown when a stochastic assertion fails.
/// </summary>
public class StochasticAssertionException : AgentEvalAssertionException
{
    /// <summary>
    /// Creates a new stochastic assertion exception.
    /// </summary>
    public StochasticAssertionException(string message) : base(message) { }
    
    /// <summary>
    /// Creates a new stochastic assertion exception with inner exception.
    /// </summary>
    public StochasticAssertionException(string message, Exception innerException) 
        : base(message, innerException) { }
}
