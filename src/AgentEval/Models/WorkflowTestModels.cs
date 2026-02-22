// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Models;

/// <summary>
/// A test case specifically for workflow testing.
/// </summary>
public record WorkflowTestCase
{
    /// <summary>
    /// Name of the test case.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The input prompt to send to the workflow.
    /// </summary>
    public required string Input { get; init; }

    /// <summary>
    /// Optional description of what the test validates.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Expected executors to be invoked (in order if StrictExecutorOrder is true).
    /// </summary>
    public IReadOnlyList<string>? ExpectedExecutors { get; init; }

    /// <summary>
    /// Whether executor order must match exactly.
    /// </summary>
    public bool StrictExecutorOrder { get; init; } = false;

    /// <summary>
    /// Expected content in the final output.
    /// </summary>
    public string? ExpectedOutputContains { get; init; }

    /// <summary>
    /// Maximum expected duration for the workflow.
    /// </summary>
    public TimeSpan? MaxDuration { get; init; }

    /// <summary>
    /// Tools that must be called during workflow execution (across all executors).
    /// </summary>
    public IReadOnlyList<string>? ExpectedTools { get; init; }

    /// <summary>
    /// Per-executor expected tools. Key is executor ID, value is list of expected tool names.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? PerExecutorExpectedTools { get; init; }

    /// <summary>
    /// Tags for filtering and categorization.
    /// </summary>
    public IReadOnlyList<string>? Tags { get; init; }
}

/// <summary>
/// Result of a workflow test.
/// </summary>
public record WorkflowTestResult
{
    /// <summary>
    /// Name of the test that was run.
    /// </summary>
    public required string TestName { get; init; }

    /// <summary>
    /// Name of the workflow that was tested.
    /// </summary>
    public required string WorkflowName { get; init; }

    /// <summary>
    /// The input that was provided.
    /// </summary>
    public required string Input { get; init; }

    /// <summary>
    /// Whether the test passed all assertions.
    /// </summary>
    public bool Passed { get; set; }

    /// <summary>
    /// The workflow execution result (null if execution failed).
    /// </summary>
    public WorkflowExecutionResult? ExecutionResult { get; init; }

    /// <summary>
    /// Total duration including assertions.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>
    /// Exception if workflow execution failed.
    /// </summary>
    public Exception? Error { get; init; }

    /// <summary>
    /// Results of individual assertions.
    /// </summary>
    public IReadOnlyList<WorkflowAssertionResult>? AssertionResults { get; set; }

    /// <summary>
    /// Failure messages for display.
    /// </summary>
    public IReadOnlyList<string>? FailureMessages { get; set; }
}

/// <summary>
/// Result of a single workflow assertion.
/// </summary>
public record WorkflowAssertionResult
{
    /// <summary>
    /// Name/description of the assertion.
    /// </summary>
    public required string AssertionName { get; init; }

    /// <summary>
    /// Whether the assertion passed.
    /// </summary>
    public bool Passed { get; init; }

    /// <summary>
    /// Failure message if assertion failed.
    /// </summary>
    public string? FailureMessage { get; init; }
}

/// <summary>
/// Summary of a workflow test suite.
/// </summary>
public record WorkflowTestSummary
{
    /// <summary>
    /// Name of the test suite.
    /// </summary>
    public required string SuiteName { get; init; }

    /// <summary>
    /// Name of the workflow that was tested.
    /// </summary>
    public required string WorkflowName { get; init; }

    /// <summary>
    /// Individual test results.
    /// </summary>
    public required IReadOnlyList<WorkflowTestResult> Results { get; init; }

    /// <summary>
    /// Total number of tests.
    /// </summary>
    public int TotalTests { get; init; }

    /// <summary>
    /// Number of passed tests.
    /// </summary>
    public int PassedTests { get; init; }

    /// <summary>
    /// Number of failed tests.
    /// </summary>
    public int FailedTests { get; init; }

    /// <summary>
    /// Total duration of all tests.
    /// </summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>
    /// Pass rate as a percentage (0-100).
    /// </summary>
    public double PassRate => TotalTests > 0 ? (double)PassedTests / TotalTests * 100 : 0;

    /// <summary>
    /// Whether all tests passed.
    /// </summary>
    public bool AllPassed => FailedTests == 0;
}

/// <summary>
/// Options for workflow testing.
/// </summary>
public record WorkflowTestOptions
{
    /// <summary>
    /// Maximum time to wait for workflow completion.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether to capture detailed telemetry.
    /// </summary>
    public bool CaptureTelemetry { get; init; } = true;

    /// <summary>
    /// Whether to continue running tests after a failure.
    /// </summary>
    public bool ContinueOnFailure { get; init; } = true;

    /// <summary>
    /// Whether to output verbose logging.
    /// </summary>
    public bool Verbose { get; init; } = false;
}
