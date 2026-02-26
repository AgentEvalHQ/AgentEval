// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// Summary of an evaluation run - the core data model for export.
/// </summary>
public class EvaluationReport
{
    /// <summary>Unique identifier for this evaluation run.</summary>
    public string RunId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    
    /// <summary>Name of this evaluation suite.</summary>
    public string? Name { get; set; }
    
    /// <summary>When the evaluation started.</summary>
    public DateTimeOffset StartTime { get; set; }
    
    /// <summary>When the evaluation completed.</summary>
    public DateTimeOffset EndTime { get; set; }
    
    /// <summary>Total duration of the evaluation.</summary>
    public TimeSpan Duration => EndTime - StartTime;
    
    /// <summary>Total number of tests run.</summary>
    public int TotalTests { get; set; }
    
    /// <summary>Number of tests that passed.</summary>
    public int PassedTests { get; set; }
    
    /// <summary>Number of tests that failed.</summary>
    public int FailedTests { get; set; }
    
    /// <summary>Number of tests that were skipped.</summary>
    public int SkippedTests { get; set; }
    
    /// <summary>Overall score (0-100).</summary>
    public double OverallScore { get; set; }
    
    /// <summary>Pass rate as a percentage.</summary>
    public double PassRate => TotalTests > 0 ? (double)PassedTests / TotalTests * 100 : 0;
    
    /// <summary>Individual test results.</summary>
    public List<TestResultSummary> TestResults { get; set; } = new();
    
    /// <summary>Agent/model information.</summary>
    public AgentInfo? Agent { get; set; }
    
    /// <summary>Custom metadata.</summary>
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Summary of a single test result.
/// </summary>
public class TestResultSummary
{
    /// <summary>Test name/identifier.</summary>
    public string Name { get; set; } = "";
    
    /// <summary>Category or group this test belongs to.</summary>
    public string? Category { get; set; }
    
    /// <summary>Score achieved (0-100).</summary>
    public double Score { get; set; }
    
    /// <summary>Whether the test passed.</summary>
    public bool Passed { get; set; }
    
    /// <summary>Whether the test was skipped.</summary>
    public bool Skipped { get; set; }
    
    /// <summary>Duration in milliseconds.</summary>
    public long DurationMs { get; set; }
    
    /// <summary>Error message if failed.</summary>
    public string? Error { get; set; }
    
    /// <summary>Stack trace if available.</summary>
    public string? StackTrace { get; set; }
    
    /// <summary>Output/logs from the test.</summary>
    public string? Output { get; set; }
    
    /// <summary>Metric scores for this test.</summary>
    public Dictionary<string, double> MetricScores { get; set; } = new();
}

/// <summary>
/// Information about the agent being evaluated.
/// </summary>
public class AgentInfo
{
    /// <summary>Agent name.</summary>
    public string? Name { get; set; }
    
    /// <summary>Model used (e.g., "gpt-4o").</summary>
    public string? Model { get; set; }
    
    /// <summary>Model version or deployment.</summary>
    public string? Version { get; set; }
    
    /// <summary>Agent endpoint if applicable.</summary>
    public string? Endpoint { get; set; }
}
