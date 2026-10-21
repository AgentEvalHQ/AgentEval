// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Comparison;

/// <summary>
/// Common distribution statistics used across stochastic runs.
/// </summary>
public record DistributionStatistics(
    double Min,
    double Max,
    double Mean,
    double Median,
    double Percentile25,
    double Percentile75,
    double Percentile95,
    int SampleSize)
{
    private readonly int _sampleSize = RequireSample(SampleSize);

    /// <summary>Number of measured samples; must be positive, including when copying the record.</summary>
    public int SampleSize { get => _sampleSize; init => _sampleSize = RequireSample(value); }

    private static int RequireSample(int value) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(nameof(SampleSize), value, "At least one measured sample is required; use null for no distribution.");

    /// <summary>Total range of the distribution.</summary>
    public double Range => Max - Min;
}

/// <summary>
/// Summary of tool usage across stochastic runs, over the runs that tracked tool usage: a run without a tool usage
/// report is not a run that made zero calls.
/// </summary>
/// <param name="ToolName">The tool summarised.</param>
/// <param name="RunsWithTool">Runs that called the tool at least once.</param>
/// <param name="RunsWithErrors">Runs in which a call to the tool failed.</param>
/// <param name="TotalCalls">Calls to the tool across the runs.</param>
/// <param name="CallCountStats">
/// Calls per run over the runs that tracked tool usage (<see cref="DistributionStatistics.SampleSize"/> is their count),
/// or <see langword="null"/> when no run tracked it.
/// </param>
/// <param name="CallRate">Share of the tracked runs that called the tool, or <see langword="null"/> when no run tracked tool usage.</param>
/// <param name="ErrorRate">Share of the tracked runs with a failed call, or <see langword="null"/> when no run tracked tool usage.</param>
public record ToolUsageSummary(
    string ToolName,
    int RunsWithTool,
    int RunsWithErrors,
    int TotalCalls,
    DistributionStatistics? CallCountStats,
    double? CallRate,
    double? ErrorRate)
{
    /// <summary>Whether every run that tracked tool usage invoked this tool; false when no run tracked it.</summary>
    public bool AllRunsCalled => CallCountStats is { } stats && RunsWithTool == stats.SampleSize;
    
    /// <summary>Whether all tool calls succeeded without errors.</summary>
    public bool AllCallsSucceeded => RunsWithErrors == 0;
}
