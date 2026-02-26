// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;

namespace AgentEval.Models;

/// <summary>
/// Represents a single test case for agent testing.
/// </summary>
public class TestCase
{
    /// <summary>Name of the test case.</summary>
    public required string Name { get; init; }
    
    /// <summary>Input prompt to send to the agent.</summary>
    public required string Input { get; init; }
    
    /// <summary>Optional substring that should be contained in the output.</summary>
    public string? ExpectedOutputContains { get; init; }
    
    /// <summary>Criteria for AI-powered evaluation.</summary>
    public IReadOnlyList<string>? EvaluationCriteria { get; init; }
    
    /// <summary>Minimum score to pass (0-100). Defaults to <see cref="EvaluationDefaults.DefaultPassingScore"/>.</summary>
    public int PassingScore { get; init; } = EvaluationDefaults.DefaultPassingScore;
    
    /// <summary>Expected tools to be called.</summary>
    public IReadOnlyList<string>? ExpectedTools { get; init; }
    
    /// <summary>Ground truth response (for accuracy metrics).</summary>
    public string? GroundTruth { get; init; }
    
    /// <summary>
    /// Additional metadata for the test case.
    /// <para>Extension point: This property is preserved through test execution and can be accessed
    /// in custom reports, metrics, or plugins. AgentEval does not process this data internally.</para>
    /// </summary>
    public IDictionary<string, object>? Metadata { get; init; }
    
    /// <summary>
    /// Tags for categorizing test cases.
    /// <para>Extension point: Tags can be used by custom test runners for filtering or grouping.
    /// AgentEval does not process tags internally but preserves them for downstream tooling.</para>
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// Result of a single test execution.
/// </summary>
public class TestResult
{
    /// <summary>Name of the test that was run.</summary>
    public required string TestName { get; init; }
    
    /// <summary>Whether the test passed.</summary>
    public bool Passed { get; set; }
    
    /// <summary>Score from 0-100.</summary>
    public int Score { get; set; }
    
    /// <summary>Details about the test result.</summary>
    public string Details { get; set; } = "";
    
    /// <summary>The actual output from the agent.</summary>
    public string? ActualOutput { get; set; }
    
    /// <summary>Suggested improvements.</summary>
    public IReadOnlyList<string>? Suggestions { get; set; }
    
    /// <summary>Individual criteria results.</summary>
    public IReadOnlyList<CriterionResult>? CriteriaResults { get; set; }
    
    /// <summary>Exception if the test errored.</summary>
    public Exception? Error { get; set; }
    
    /// <summary>Tool usage information from the agent run.</summary>
    public ToolUsageReport? ToolUsage { get; set; }
    
    /// <summary>Performance metrics from the agent run.</summary>
    public PerformanceMetrics? Performance { get; set; }
    
    /// <summary>Metric results from custom evaluations.</summary>
    public IReadOnlyList<MetricResult>? MetricResults { get; set; }
    
    /// <summary>
    /// Structured failure report with reasons and suggestions.
    /// Populated when the test fails to provide trace-first debugging.
    /// </summary>
    public FailureReport? Failure { get; set; }
    
    /// <summary>
    /// Timeline of all tool calls with timing information.
    /// Enables trace-first debugging to understand agent behavior.
    /// </summary>
    public ToolCallTimeline? Timeline { get; set; }

    /// <summary>
    /// Results from red team security scanning, if performed.
    /// Populated when test is run with IncludeRedTeam = true or via explicit red-team scan.
    /// </summary>
    public IRedTeamResult? RedTeam { get; set; }
    
    /// <summary>Whether any tools were called during the run.</summary>
    public bool ToolsWereCalled => ToolUsage?.Count > 0;
    
    /// <summary>Number of tool calls made.</summary>
    public int ToolCallCount => ToolUsage?.Count ?? 0;
    
    /// <summary>Whether the test had an error.</summary>
    public bool HasError => Error != null;
}

/// <summary>
/// Summary of a test suite run.
/// </summary>
public class TestSummary
{
    /// <summary>Name of the test suite.</summary>
    public string SuiteName { get; }
    
    /// <summary>All test results.</summary>
    public IReadOnlyList<TestResult> Results { get; }
    
    /// <summary>Total number of tests.</summary>
    public int TotalCount => Results.Count;
    
    /// <summary>Number of passed tests.</summary>
    public int PassedCount => Results.Count(r => r.Passed);
    
    /// <summary>Number of failed tests.</summary>
    public int FailedCount => Results.Count(r => !r.Passed);
    
    /// <summary>Average score across all tests.</summary>
    public double AverageScore => Results.Count > 0 ? Results.Average(r => r.Score) : 0;
    
    /// <summary>Whether all tests passed.</summary>
    public bool AllPassed => Results.All(r => r.Passed);
    
    /// <summary>Total duration across all tests.</summary>
    public TimeSpan TotalDuration => TimeSpan.FromTicks(
        Results.Where(r => r.Performance != null)
               .Sum(r => r.Performance!.TotalDuration.Ticks));
    
    /// <summary>Total estimated cost.</summary>
    public decimal TotalCost => Results.Where(r => r.Performance?.EstimatedCost != null)
                                        .Sum(r => r.Performance!.EstimatedCost!.Value);

    public TestSummary(string suiteName, IEnumerable<TestResult> results)
    {
        SuiteName = suiteName;
        Results = results.ToList();
    }
}
