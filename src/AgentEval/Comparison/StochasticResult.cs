// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Comparison;

/// <summary>
/// Result of a stochastic test run containing all individual results and statistics.
/// </summary>
/// <param name="TestCase">The test case that was run.</param>
/// <param name="IndividualResults">Results from each individual run.</param>
/// <param name="Statistics">Statistical analysis of the results.</param>
/// <param name="Options">Options used for this stochastic run.</param>
/// <param name="Passed">Whether the stochastic test passed based on SuccessRateThreshold.</param>
public record StochasticResult(
    TestCase TestCase,
    IReadOnlyList<TestResult> IndividualResults,
    StochasticStatistics Statistics,
    StochasticOptions Options,
    bool Passed)
{
    /// <summary>
    /// Total duration of all runs combined.
    /// </summary>
    public TimeSpan TotalDuration => TimeSpan.FromTicks(
        IndividualResults.Sum(r => r.Performance?.TotalDuration.Ticks ?? 0));
    
    /// <summary>
    /// Number of individual runs that passed.
    /// </summary>
    public int PassedCount => IndividualResults.Count(r => r.Passed);
    
    /// <summary>
    /// Number of individual runs that failed.
    /// </summary>
    public int FailedCount => IndividualResults.Count(r => !r.Passed);
    
    /// <summary>
    /// Gets a summary message for display.
    /// </summary>
    public string Summary => Passed
        ? $"✅ PASSED: {PassedCount}/{IndividualResults.Count} runs passed ({Statistics.PassRate * 100:F1}% >= {Options.SuccessRateThreshold * 100:F0}% threshold)"
        : $"❌ FAILED: {PassedCount}/{IndividualResults.Count} runs passed ({Statistics.PassRate * 100:F1}% < {Options.SuccessRateThreshold * 100:F0}% threshold)";

    /// <summary>Aggregate duration statistics (ms).</summary>
    public DistributionStatistics DurationStats => StatisticsCalculator.CreateDistribution(GetDurations());

    /// <summary>Aggregate time-to-first-token statistics (ms), null when unavailable.</summary>
    public DistributionStatistics? TimeToFirstTokenStats => GetTimeToFirstToken().Count == 0
        ? null
        : StatisticsCalculator.CreateDistribution(GetTimeToFirstToken());

    /// <summary>Aggregate prompt token statistics.</summary>
    public DistributionStatistics? PromptTokenStats => GetPromptTokens().Count == 0 ? null : StatisticsCalculator.CreateDistribution(GetPromptTokens());

    /// <summary>Aggregate completion token statistics.</summary>
    public DistributionStatistics? CompletionTokenStats => GetCompletionTokens().Count == 0 ? null : StatisticsCalculator.CreateDistribution(GetCompletionTokens());

    /// <summary>Aggregate total token statistics.</summary>
    public DistributionStatistics? TotalTokenStats => GetTotalTokens().Count == 0 ? null : StatisticsCalculator.CreateDistribution(GetTotalTokens());

    /// <summary>Aggregate cost statistics (USD).</summary>
    public DistributionStatistics? CostStats => GetCosts().Count == 0 ? null : StatisticsCalculator.CreateDistribution(GetCosts());

    /// <summary>Aggregate tool call count statistics across runs.</summary>
    public DistributionStatistics ToolCallCountStats => StatisticsCalculator.CreateDistribution(GetToolCallCounts());

    /// <summary>Fastest run by total duration.</summary>
    public (TestResult Result, TimeSpan Duration)? FastestRun =>
        GetRunsByDuration().FirstOrDefault();

    /// <summary>Slowest run by total duration.</summary>
    public (TestResult Result, TimeSpan Duration)? SlowestRun =>
        GetRunsByDuration().LastOrDefault();

    /// <summary>Metric score distributions grouped by metric name (e.g., Faithfulness, Relevance).</summary>
    public IReadOnlyDictionary<string, DistributionStatistics> MetricDistributions => _metricDistributions ??= BuildMetricDistributions();

    private IReadOnlyDictionary<string, DistributionStatistics>? _metricDistributions;

    /// <summary>
    /// Get a summary of tool usage across runs for a specific tool (e.g., "CalculatorTool").
    /// </summary>
    public ToolUsageSummary GetToolUsageSummary(string toolName)
    {
        var callCounts = new List<int>();
        int runsWithTool = 0;
        int runsWithErrors = 0;
        int totalCalls = 0;
        int sampleSize = IndividualResults.Count;
        
        foreach (var result in IndividualResults)
        {
            var toolCalls = result.ToolUsage?.GetCallsByName(toolName) ?? Enumerable.Empty<ToolCallRecord>();
            var callCount = toolCalls.Count();
            callCounts.Add(callCount);
            totalCalls += callCount;
            if (callCount > 0)
            {
                runsWithTool++;
                if (toolCalls.Any(c => c.HasError))
                {
                    runsWithErrors++;
                }
            }
        }
        
        var callStats = StatisticsCalculator.CreateDistribution(callCounts);
        double callRate = sampleSize == 0 ? 0 : (double)runsWithTool / sampleSize;
        double errorRate = sampleSize == 0 ? 0 : (double)runsWithErrors / sampleSize;
        
        return new ToolUsageSummary(
            ToolName: toolName,
            RunsWithTool: runsWithTool,
            RunsWithErrors: runsWithErrors,
            TotalCalls: totalCalls,
            CallCountStats: callStats,
            CallRate: callRate,
            ErrorRate: errorRate);
    }

    private List<TimeSpan> GetDurations() => IndividualResults
        .Where(r => r.Performance?.TotalDuration != null)
        .Select(r => r.Performance!.TotalDuration)
        .ToList();

    private List<double> GetTimeToFirstToken() => IndividualResults
        .Where(r => r.Performance?.TimeToFirstToken != null)
        .Select(r => r.Performance!.TimeToFirstToken!.Value.TotalMilliseconds)
        .ToList();
    
    private List<int> GetPromptTokens() => IndividualResults
        .Where(r => r.Performance?.PromptTokens != null)
        .Select(r => r.Performance!.PromptTokens!.Value)
        .ToList();
    
    private List<int> GetCompletionTokens() => IndividualResults
        .Where(r => r.Performance?.CompletionTokens != null)
        .Select(r => r.Performance!.CompletionTokens!.Value)
        .ToList();
    
    private List<int> GetTotalTokens() => IndividualResults
        .Where(r => r.Performance?.TotalTokens != null)
        .Select(r => r.Performance!.TotalTokens!.Value)
        .ToList();
    
    private List<decimal> GetCosts() => IndividualResults
        .Where(r => r.Performance?.EstimatedCost != null)
        .Select(r => r.Performance!.EstimatedCost!.Value)
        .ToList();
    
    private List<int> GetToolCallCounts() => IndividualResults
        .Select(r => r.ToolUsage?.Count ?? 0)
        .ToList();
    
    private List<(TestResult Result, TimeSpan Duration)> GetRunsByDuration()
    {
        return IndividualResults
            .Where(r => r.Performance?.TotalDuration != null)
            .Select(r => (Result: r, Duration: r.Performance!.TotalDuration))
            .OrderBy(pair => pair.Duration)
            .ToList();
    }
    
    private IReadOnlyDictionary<string, DistributionStatistics> BuildMetricDistributions()
    {
        var metrics = IndividualResults
            .SelectMany(r => r.MetricResults ?? Array.Empty<MetricResult>())
            .GroupBy(m => m.MetricName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => StatisticsCalculator.CreateDistribution(g.Select(m => m.Score).ToList()),
                StringComparer.OrdinalIgnoreCase);
        
        return metrics;
    }
}

/// <summary>
/// Statistical analysis of stochastic test results.
/// </summary>
/// <param name="PassRate">Percentage of tests that passed (0.0-1.0).</param>
/// <param name="MeanScore">Average score across all runs.</param>
/// <param name="MedianScore">Median score across all runs.</param>
/// <param name="StandardDeviation">Standard deviation of scores.</param>
/// <param name="MinScore">Minimum score observed.</param>
/// <param name="MaxScore">Maximum score observed.</param>
/// <param name="Percentile25">25th percentile score.</param>
/// <param name="Percentile75">75th percentile score.</param>
/// <param name="Percentile95">95th percentile score.</param>
/// <param name="ConfidenceInterval">Confidence interval for the mean.</param>
/// <param name="SampleSize">Number of samples used.</param>
public record StochasticStatistics(
    double PassRate,
    double MeanScore,
    double MedianScore,
    double StandardDeviation,
    int MinScore,
    int MaxScore,
    double Percentile25,
    double Percentile75,
    double Percentile95,
    ConfidenceInterval? ConfidenceInterval,
    int SampleSize);

/// <summary>
/// Represents a confidence interval for a statistic.
/// </summary>
/// <param name="Lower">Lower bound of the interval.</param>
/// <param name="Upper">Upper bound of the interval.</param>
/// <param name="Level">Confidence level (e.g., 0.95 for 95%).</param>
public record ConfidenceInterval(double Lower, double Upper, double Level)
{
    /// <summary>
    /// Width of the confidence interval.
    /// </summary>
    public double Width => Upper - Lower;
    
    /// <summary>
    /// Gets a formatted string representation.
    /// </summary>
    public override string ToString() => $"[{Lower:F2}, {Upper:F2}] ({Level * 100:F0}% CI)";
}
