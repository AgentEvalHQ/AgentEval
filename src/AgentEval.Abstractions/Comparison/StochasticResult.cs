// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Linq;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Comparison;

/// <summary>
/// The verdict of a stochastic test over all of its planned runs (<see cref="StochasticResult.DecideVerdict"/>).
/// </summary>
public enum StochasticVerdict
{
    /// <summary>The runs that passed reach the success-rate threshold on their own.</summary>
    Passed,

    /// <summary>The threshold is missed even if every run that was not measured had passed.</summary>
    Failed,

    /// <summary>
    /// Runs that were not measured (errored, or the judge produced no verdict) could still decide it either way.
    /// </summary>
    Undecided,

    /// <summary>No run was measured.</summary>
    NotMeasured,
}

/// <summary>
/// Result of a stochastic test run containing all individual results and statistics.
/// </summary>
/// <param name="TestCase">The test case that was run.</param>
/// <param name="IndividualResults">Results from each individual run, measured or not.</param>
/// <param name="Statistics">
/// Statistical analysis of the measured runs only: a run that errored or whose judge produced no verdict carries a
/// placeholder score of 0, and is not a sample (see <see cref="NotMeasuredCount"/>). Null when none was measured.
/// </param>
/// <param name="Options">Options used for this stochastic run.</param>
/// <param name="Passed">
/// Whether the stochastic test passed: <see langword="true"/> only when <see cref="Verdict"/> is
/// <see cref="StochasticVerdict.Passed"/>.
/// </param>
public record StochasticResult(
    TestCase TestCase,
    IReadOnlyList<TestResult> IndividualResults,
    StochasticStatistics? Statistics,
    StochasticOptions Options,
    bool Passed)
{
    /// <summary>
    /// Total duration of the timed runs (those with <see cref="TestResult.Performance"/>), or <see langword="null"/>
    /// when no run was timed: an untimed run is not a 0 ms run.
    /// </summary>
    public TimeSpan? TotalDuration
    {
        get
        {
            var durations = GetDurations();
            return durations.Count > 0 ? TimeSpan.FromTicks(durations.Sum(d => d.Ticks)) : null;
        }
    }
    
    /// <summary>
    /// Number of individual runs that passed.
    /// </summary>
    public int PassedCount => IndividualResults.Count(r => r.Passed);

    /// <summary>Share of all runs that passed, including unmeasured runs in the denominator; null when there are no runs.</summary>
    public double? PassRateOverAllRuns => IndividualResults.Count > 0
        ? (double)PassedCount / IndividualResults.Count
        : null;

    /// <summary>
    /// Number of measured runs that failed. A run that was not measured is not a failure (<see cref="NotMeasuredCount"/>).
    /// </summary>
    public int FailedCount => IndividualResults.Count(r => !r.Passed && IsMeasured(r));

    /// <summary>
    /// Runs that produced no verdict: the run threw (<see cref="TestResult.Error"/>) or the judge produced none
    /// (<see cref="TestResult.JudgeFailed"/>). They are left out of <see cref="Statistics"/>, never counted as passed.
    /// </summary>
    public int NotMeasuredCount => IndividualResults.Count(r => !IsMeasured(r));

    /// <summary>Runs that produced a verdict: the sample <see cref="Statistics"/> describes.</summary>
    public int MeasuredCount => IndividualResults.Count - NotMeasuredCount;

    /// <summary>
    /// The verdict over all runs, by <see cref="DecideVerdict"/>: it names a result the runs that were not measured could
    /// still change, rather than reading them as failures or leaving them out of the denominator.
    /// </summary>
    public StochasticVerdict Verdict =>
        DecideVerdict(PassedCount, NotMeasuredCount, IndividualResults.Count, Options.SuccessRateThreshold);

    /// <summary>
    /// Whether a run produced a verdict. A run that threw, or whose judge produced none, carries a placeholder
    /// <see cref="TestResult.Score"/> of 0 and <see cref="TestResult.Passed"/> of false: it is not measured.
    /// </summary>
    /// <param name="run">One run's result.</param>
    internal static bool IsMeasured(TestResult run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Error is null && !run.JudgeFailed;
    }

    /// <summary>
    /// The one rule that decides a stochastic verdict over all <paramref name="totalRuns"/> planned runs, the same rule
    /// the AEF writer applies to trials (AtLeast): <see cref="StochasticVerdict.Passed"/> when the passing runs reach the
    /// threshold on their own, so no run that was not measured could undo it; <see cref="StochasticVerdict.Failed"/> when
    /// they miss it even if every run that was not measured had passed; <see cref="StochasticVerdict.Undecided"/>
    /// otherwise; <see cref="StochasticVerdict.NotMeasured"/> when no run was measured.
    /// </summary>
    /// <param name="passedRuns">Runs that passed.</param>
    /// <param name="notMeasuredRuns">Runs that produced no verdict.</param>
    /// <param name="totalRuns">All runs, measured or not.</param>
    /// <param name="successRateThreshold">The pass share required (0.0-1.0).</param>
    /// <remarks>
    /// The share is compared as the runner always compared it (passed / total ≥ threshold), so a result with every run
    /// measured gets the verdict it always had. Kept in one method so the rule can be changed in one place.
    /// </remarks>
    /// <returns>The verdict, including <see cref="StochasticVerdict.NotMeasured"/> when no run was measured.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A count is negative, the passed and unmeasured counts overlap or exceed the total, or the threshold is not finite and within [0, 1].
    /// </exception>
    public static StochasticVerdict DecideVerdict(int passedRuns, int notMeasuredRuns, int totalRuns, double successRateThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalRuns);
        if (notMeasuredRuns < 0 || notMeasuredRuns > totalRuns)
            throw new ArgumentOutOfRangeException(nameof(notMeasuredRuns), notMeasuredRuns, "Expected a count between zero and totalRuns.");
        if (passedRuns < 0 || passedRuns > totalRuns - notMeasuredRuns)
            throw new ArgumentOutOfRangeException(nameof(passedRuns), passedRuns, "Expected a count between zero and the number of measured runs.");
        if (!double.IsFinite(successRateThreshold) || successRateThreshold < 0 || successRateThreshold > 1)
            throw new ArgumentOutOfRangeException(nameof(successRateThreshold), successRateThreshold, "Expected a finite threshold between zero and one.");

        if (totalRuns == 0 || notMeasuredRuns == totalRuns)
            return StochasticVerdict.NotMeasured;
        if ((double)passedRuns / totalRuns >= successRateThreshold)
            return StochasticVerdict.Passed;
        if ((double)(passedRuns + notMeasuredRuns) / totalRuns < successRateThreshold)
            return StochasticVerdict.Failed;
        return StochasticVerdict.Undecided;
    }

    /// <summary>
    /// Gets a summary message for display.
    /// </summary>
    public string Summary => Verdict switch
    {
        StochasticVerdict.Passed when Passed =>
            $"✅ PASSED: {PassedCount}/{IndividualResults.Count} runs passed ({(double)PassedCount / IndividualResults.Count * 100:F1}% >= {Options.SuccessRateThreshold * 100:F0}% threshold)" +
            NotMeasuredNote,
        StochasticVerdict.Undecided =>
            $"⚠️ UNDECIDED: {PassedCount}/{IndividualResults.Count} runs passed and {NotMeasuredCount} were not measured " +
            $"(errored, or the judge produced no verdict); whether the {Options.SuccessRateThreshold * 100:F0}% threshold is met is undecided",
        StochasticVerdict.NotMeasured =>
            $"⚠️ NOT MEASURED: none of {IndividualResults.Count} runs was measured (errored, or the judge produced no verdict)",
        _ =>
            $"❌ FAILED: {PassedCount}/{IndividualResults.Count} runs passed ({(IndividualResults.Count > 0 ? (double)PassedCount / IndividualResults.Count * 100 : 0):F1}% < {Options.SuccessRateThreshold * 100:F0}% threshold)" +
            NotMeasuredNote,
    };

    private string NotMeasuredNote => NotMeasuredCount > 0 ? $"; {NotMeasuredCount} not measured" : "";

    /// <summary>
    /// Aggregate duration statistics (ms) over the timed runs, or <see langword="null"/> when no run recorded a
    /// duration: never a 0 ms mean.
    /// </summary>
    public DistributionStatistics? DurationStats => StatsOrNull(GetDurations());

    // PERF-06: each nullable stat below evaluates its source list ONCE (the source getters
    // re-materialize from IndividualResults), instead of calling GetX() twice — once for the count
    // guard and again for Create.

    /// <summary>Aggregate time-to-first-token statistics (ms), null when unavailable.</summary>
    public DistributionStatistics? TimeToFirstTokenStats => StatsOrNull(GetTimeToFirstToken());

    /// <summary>Aggregate prompt token statistics.</summary>
    public DistributionStatistics? PromptTokenStats => StatsOrNull(GetPromptTokens());

    /// <summary>Aggregate completion token statistics.</summary>
    public DistributionStatistics? CompletionTokenStats => StatsOrNull(GetCompletionTokens());

    /// <summary>Aggregate total token statistics.</summary>
    public DistributionStatistics? TotalTokenStats => StatsOrNull(GetTotalTokens());

    /// <summary>Aggregate cost statistics (USD).</summary>
    public DistributionStatistics? CostStats => StatsOrNull(GetCosts());

    /// <summary>
    /// Aggregate tool call count statistics across the runs that tracked tool usage, or <see langword="null"/> when no
    /// run tracked it. A run without a tool usage report is not a run with zero calls.
    /// </summary>
    public DistributionStatistics? ToolCallCountStats => StatsOrNull(GetToolCallCounts());

    // FirstOrDefault() on an empty list of tuples is default((null, 0)), which converts to a non-null nullable: with no
    // timed run the fastest and slowest read as a null run at 0 ms. An empty list gives null instead.

    /// <summary>Fastest run by total duration, or <see langword="null"/> when no run recorded a duration.</summary>
    public (TestResult Result, TimeSpan Duration)? FastestRun =>
        GetRunsByDuration() is { Count: > 0 } runs ? runs[0] : null;

    /// <summary>Slowest run by total duration, or <see langword="null"/> when no run recorded a duration.</summary>
    public (TestResult Result, TimeSpan Duration)? SlowestRun =>
        GetRunsByDuration() is { Count: > 0 } runs ? runs[^1] : null;

    /// <summary>Metric score distributions grouped by metric name (e.g., Faithfulness, Relevance).</summary>
    public IReadOnlyDictionary<string, DistributionStatistics> MetricDistributions => _metricDistributions ??= BuildMetricDistributions();

    private IReadOnlyDictionary<string, DistributionStatistics>? _metricDistributions;

    /// <summary>
    /// Get a summary of tool usage across runs for a specific tool (e.g., "CalculatorTool"), over the runs that tracked
    /// tool usage: a run without a tool usage report is not a run that made zero calls. With no such run, the call
    /// statistics and both rates are <see langword="null"/>.
    /// </summary>
    public ToolUsageSummary GetToolUsageSummary(string toolName)
    {
        var callCounts = new List<int>();
        int runsWithTool = 0;
        int runsWithErrors = 0;
        int totalCalls = 0;

        foreach (var result in IndividualResults.Where(r => r.ToolUsage != null))
        {
            var toolCalls = result.ToolUsage!.GetCallsByName(toolName).ToList();
            callCounts.Add(toolCalls.Count);
            totalCalls += toolCalls.Count;
            if (toolCalls.Count > 0)
            {
                runsWithTool++;
                if (toolCalls.Any(c => c.HasError))
                {
                    runsWithErrors++;
                }
            }
        }

        int sampleSize = callCounts.Count;
        var callStats = StatsOrNull(callCounts);
        double? callRate = sampleSize == 0 ? null : (double)runsWithTool / sampleSize;
        double? errorRate = sampleSize == 0 ? null : (double)runsWithErrors / sampleSize;

        return new ToolUsageSummary(
            ToolName: toolName,
            RunsWithTool: runsWithTool,
            RunsWithErrors: runsWithErrors,
            TotalCalls: totalCalls,
            CallCountStats: callStats,
            CallRate: callRate,
            ErrorRate: errorRate);
    }

    // PERF-06: build stats from a single materialization, returning null for an empty source.
    private static DistributionStatistics? StatsOrNull(IReadOnlyList<double> values)
        => values.Count == 0 ? null : DistributionStatisticsFactory.Create(values);
    private static DistributionStatistics? StatsOrNull(IReadOnlyList<int> values)
        => values.Count == 0 ? null : DistributionStatisticsFactory.Create(values);
    private static DistributionStatistics? StatsOrNull(IReadOnlyList<decimal> values)
        => values.Count == 0 ? null : DistributionStatisticsFactory.Create(values);
    private static DistributionStatistics? StatsOrNull(IReadOnlyList<TimeSpan> values)
        => values.Count == 0 ? null : DistributionStatisticsFactory.Create(values);

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
        .Where(r => r.ToolUsage != null)
        .Select(r => r.ToolUsage!.Count)
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
            .Where(m => m.Measured)   // a not-measured placeholder 0 is not a sample (B12i)
            .GroupBy(m => m.MetricName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => DistributionStatisticsFactory.Create(g.Select(m => m.Score).ToList())!,
                StringComparer.OrdinalIgnoreCase);
        
        return metrics;
    }
}

/// <summary>
/// Statistical analysis of stochastic test results, over the measured runs only: from the stochastic runner, a run
/// that errored or whose judge produced no verdict is not a sample.
/// At least one run must be measured; otherwise <see cref="StochasticResult.Statistics"/> is null.
/// </summary>
/// <param name="PassRate">Share of the measured runs that passed (0.0-1.0). Not the verdict: see <see cref="StochasticResult.Verdict"/>.</param>
/// <param name="MeanScore">Average score across the measured runs.</param>
/// <param name="MedianScore">Median score across the measured runs.</param>
/// <param name="StandardDeviation">Sample standard deviation, null below two samples or when analysis is disabled.</param>
/// <param name="MinScore">Minimum score observed.</param>
/// <param name="MaxScore">Maximum score observed.</param>
/// <param name="Percentile25">25th percentile score, null when analysis is disabled.</param>
/// <param name="Percentile75">75th percentile score, null when analysis is disabled.</param>
/// <param name="Percentile95">95th percentile score, null when analysis is disabled.</param>
/// <param name="ConfidenceInterval">Confidence interval for the mean; null below two samples or when analysis is disabled.</param>
/// <param name="SampleSize">Number of samples used: the measured runs.</param>
public record StochasticStatistics(
    double PassRate,
    double MeanScore,
    double MedianScore,
    double? StandardDeviation,
    int MinScore,
    int MaxScore,
    double? Percentile25,
    double? Percentile75,
    double? Percentile95,
    ConfidenceInterval? ConfidenceInterval,
    int SampleSize)
{
    private readonly int _sampleSize = RequireSample(SampleSize);

    /// <summary>Number of measured samples; must be positive, including when copying the record.</summary>
    public int SampleSize { get => _sampleSize; init => _sampleSize = RequireSample(value); }

    private static int RequireSample(int value) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(nameof(SampleSize), value, "At least one measured sample is required; use null for no statistics.");
}

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
