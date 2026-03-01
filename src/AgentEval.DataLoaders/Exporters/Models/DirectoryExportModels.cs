// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Exporters.Models;

/// <summary>
/// A single test result line for results.jsonl (JSON Lines format).
/// Each instance serializes to one JSON line — streaming-friendly and append-friendly.
/// </summary>
internal sealed class DirectoryTestResult
{
    /// <summary>Test name/identifier.</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Category or group this test belongs to.</summary>
    public string? Category { get; set; }
    
    /// <summary>Whether the test passed.</summary>
    public bool Passed { get; set; }
    
    /// <summary>Whether the test was skipped.</summary>
    public bool Skipped { get; set; }
    
    /// <summary>Score achieved (0-100).</summary>
    public double Score { get; set; }
    
    /// <summary>Duration in milliseconds.</summary>
    public long DurationMs { get; set; }
    
    /// <summary>Error message if failed.</summary>
    public string? Error { get; set; }
    
    /// <summary>Per-metric scores for this test.</summary>
    public Dictionary<string, double>? Metrics { get; set; }
}

/// <summary>
/// Aggregate statistics for summary.json.
/// Contains pass rates, totals, and per-metric distribution statistics.
/// </summary>
internal sealed class DirectorySummary
{
    /// <summary>Unique run identifier.</summary>
    public string RunId { get; set; } = "";
    
    /// <summary>Optional suite name.</summary>
    public string? Name { get; set; }
    
    /// <summary>When the evaluation was run.</summary>
    public DateTimeOffset Timestamp { get; set; }
    
    /// <summary>Total duration formatted as hh:mm:ss.</summary>
    public string Duration { get; set; } = "";
    
    /// <summary>Overall aggregate statistics.</summary>
    public DirectorySummaryStats Stats { get; set; } = new();
    
    /// <summary>Overall score (0-100).</summary>
    public double OverallScore { get; set; }
    
    /// <summary>Per-metric aggregate statistics.</summary>
    public Dictionary<string, DirectoryMetricStats> Metrics { get; set; } = new();
}

/// <summary>
/// Pass/fail aggregate stats for summary.json.
/// </summary>
internal sealed class DirectorySummaryStats
{
    /// <summary>Total test count.</summary>
    public int Total { get; set; }
    
    /// <summary>Number of passing tests.</summary>
    public int Passed { get; set; }
    
    /// <summary>Number of failing tests.</summary>
    public int Failed { get; set; }
    
    /// <summary>Number of skipped tests.</summary>
    public int Skipped { get; set; }
    
    /// <summary>Pass rate (0.0 – 1.0).</summary>
    public double PassRate { get; set; }
}

/// <summary>
/// Per-metric distribution statistics for summary.json.
/// </summary>
internal sealed class DirectoryMetricStats
{
    /// <summary>Mean score across all tests.</summary>
    public double Mean { get; set; }
    
    /// <summary>Minimum score.</summary>
    public double Min { get; set; }
    
    /// <summary>Maximum score.</summary>
    public double Max { get; set; }
    
    /// <summary>Standard deviation.</summary>
    public double StdDev { get; set; }
    
    /// <summary>50th percentile (median).</summary>
    public double P50 { get; set; }
    
    /// <summary>95th percentile.</summary>
    public double P95 { get; set; }
    
    /// <summary>99th percentile.</summary>
    public double P99 { get; set; }
    
    /// <summary>Number of samples (tests that reported this metric).</summary>
    public int SampleSize { get; set; }
}

/// <summary>
/// Run metadata for run.json — captures everything needed for reproducibility.
/// </summary>
internal sealed class DirectoryRunMetadata
{
    /// <summary>Unique run identifier (matches summary.json).</summary>
    public string RunId { get; set; } = "";
    
    /// <summary>Optional run name (e.g., "Baseline v1.2.3").</summary>
    public string? Name { get; set; }
    
    /// <summary>When the evaluation started.</summary>
    public DateTimeOffset Timestamp { get; set; }
    
    /// <summary>Total duration formatted as hh:mm:ss.</summary>
    public string Duration { get; set; } = "";
    
    /// <summary>Agent/model information.</summary>
    public DirectoryAgentInfo? Agent { get; set; }
    
    /// <summary>Execution environment details.</summary>
    public DirectoryEnvironmentInfo Environment { get; set; } = new();
    
    /// <summary>Custom metadata key-value pairs.</summary>
    public Dictionary<string, string>? Parameters { get; set; }
}

/// <summary>
/// Agent/model information for run.json.
/// </summary>
internal sealed class DirectoryAgentInfo
{
    /// <summary>Agent name.</summary>
    public string? Name { get; set; }
    
    /// <summary>Model identifier (e.g., "gpt-4o").</summary>
    public string? Model { get; set; }
    
    /// <summary>Model version or deployment name.</summary>
    public string? Version { get; set; }
}

/// <summary>
/// Execution environment details for run.json — enables reproducibility diagnosis.
/// </summary>
internal sealed class DirectoryEnvironmentInfo
{
    /// <summary>Machine name.</summary>
    public string Machine { get; set; } = "";
    
    /// <summary>Operating system description.</summary>
    public string Os { get; set; } = "";
    
    /// <summary>.NET runtime version.</summary>
    public string DotnetVersion { get; set; } = "";
}
