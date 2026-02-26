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
    /// <summary>Total range of the distribution.</summary>
    public double Range => Max - Min;
}

/// <summary>
/// Summary of tool usage across stochastic runs.
/// </summary>
public record ToolUsageSummary(
    string ToolName,
    int RunsWithTool,
    int RunsWithErrors,
    int TotalCalls,
    DistributionStatistics CallCountStats,
    double CallRate,
    double ErrorRate)
{
    /// <summary>Whether every run invoked this tool.</summary>
    public bool AllRunsCalled => RunsWithTool == CallCountStats.SampleSize;
    
    /// <summary>Whether all tool calls succeeded without errors.</summary>
    public bool AllCallsSucceeded => RunsWithErrors == 0;
}