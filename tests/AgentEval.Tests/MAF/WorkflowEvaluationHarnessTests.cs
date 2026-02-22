// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Core;

namespace AgentEval.Tests.MAF;

/// <summary>
/// Unit tests for WorkflowEvaluationHarness.
/// </summary>
public class WorkflowEvaluationHarnessTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // BASIC EXECUTION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunWorkflowTestAsync_SimpleWorkflow_ReturnsResult()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor1", "output1"),
            ("executor2", "output2"));

        var testCase = new WorkflowTestCase
        {
            Name = "Basic Test",
            Input = "Test input"
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.NotNull(result);
        Assert.Equal("Basic Test", result.TestName);
        Assert.Equal("TestWorkflow", result.WorkflowName);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RunWorkflowTestAsync_CapturesExecutionResult()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("step1", "output1"),
            ("step2", "output2"),
            ("step3", "output3"));

        var testCase = new WorkflowTestCase
        {
            Name = "Execution Test",
            Input = "Test input"
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.NotNull(result.ExecutionResult);
        Assert.Equal(3, result.ExecutionResult.Steps.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EXPECTED EXECUTORS VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunWorkflowTestAsync_ExpectedExecutors_AllPresent_Passes()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("researcher", "research output"),
            ("writer", "article output"),
            ("editor", "final output"));

        var testCase = new WorkflowTestCase
        {
            Name = "Executor Test",
            Input = "Test input",
            ExpectedExecutors = new[] { "researcher", "writer", "editor" }
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RunWorkflowTestAsync_ExpectedExecutors_MissingExecutor_Fails()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("writer", "article output"));

        var testCase = new WorkflowTestCase
        {
            Name = "Missing Executor Test",
            Input = "Test input",
            ExpectedExecutors = new[] { "researcher", "writer" }
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.False(result.Passed);
        Assert.Contains(result.FailureMessages!, m => m.Contains("Missing") || m.Contains("researcher"));
    }

    [Fact]
    public async Task RunWorkflowTestAsync_StrictOrder_CorrectOrder_Passes()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("step1", "out1"),
            ("step2", "out2"),
            ("step3", "out3"));

        var testCase = new WorkflowTestCase
        {
            Name = "Order Test",
            Input = "Test input",
            ExpectedExecutors = new[] { "step1", "step2", "step3" },
            StrictExecutorOrder = true
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RunWorkflowTestAsync_StrictOrder_WrongOrder_Fails()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("step2", "out2"),
            ("step1", "out1"));

        var testCase = new WorkflowTestCase
        {
            Name = "Order Test",
            Input = "Test input",
            ExpectedExecutors = new[] { "step1", "step2" },
            StrictExecutorOrder = true
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.False(result.Passed);
        Assert.Contains(result.FailureMessages!, m => m.Contains("order"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // OUTPUT VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunWorkflowTestAsync_ExpectedOutputContains_Matches_Passes()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "The final polished article"));

        var testCase = new WorkflowTestCase
        {
            Name = "Output Test",
            Input = "Test input",
            ExpectedOutputContains = "polished"
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RunWorkflowTestAsync_ExpectedOutputContains_Missing_Fails()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "Some other output"));

        var testCase = new WorkflowTestCase
        {
            Name = "Output Test",
            Input = "Test input",
            ExpectedOutputContains = "polished"
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.False(result.Passed);
        Assert.Contains(result.FailureMessages!, m => m.Contains("missing expected content") || m.Contains("polished"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DURATION VALIDATION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunWorkflowTestAsync_MaxDuration_WithinLimit_Passes()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "output"));

        var testCase = new WorkflowTestCase
        {
            Name = "Duration Test",
            Input = "Test input",
            MaxDuration = TimeSpan.FromMinutes(5)
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.True(result.Passed);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ERROR HANDLING
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunWorkflowTestAsync_WorkflowError_Fails()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = new MAFWorkflowAdapter(
            "ErrorWorkflow",
            (prompt, ct) => ExecuteWithError());

        var testCase = new WorkflowTestCase
        {
            Name = "Error Test",
            Input = "Test input"
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.False(result.Passed);
        Assert.Contains(result.FailureMessages!, m => m.Contains("error"));
    }

    private static async IAsyncEnumerable<WorkflowEvent> ExecuteWithError()
    {
        yield return new ExecutorOutputEvent("executor1", "partial");
        await Task.Yield();
        yield return new ExecutorErrorEvent("executor1", "Something went wrong");
        yield return new WorkflowCompleteEvent();
    }

    [Fact]
    public async Task RunWorkflowTestAsync_WorkflowException_Fails()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = new MAFWorkflowAdapter(
            "ThrowingWorkflow",
            (prompt, ct) => ThrowingWorkflow());

        var testCase = new WorkflowTestCase
        {
            Name = "Exception Test",
            Input = "Test input"
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        // Exceptions are caught by adapter and recorded as errors in execution result
        Assert.False(result.Passed);
        Assert.NotNull(result.ExecutionResult);
        Assert.NotNull(result.ExecutionResult.Errors);
        Assert.Contains(result.ExecutionResult.Errors, e => e.Message.Contains("exception", StringComparison.OrdinalIgnoreCase));
    }

    private static async IAsyncEnumerable<WorkflowEvent> ThrowingWorkflow()
    {
        await Task.Yield();
        throw new InvalidOperationException("Test exception");
        #pragma warning disable CS0162
        yield break;
        #pragma warning restore CS0162
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TEST SUITE EXECUTION
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunWorkflowTestSuiteAsync_RunsAllTests()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "output"));

        var testCases = new[]
        {
            new WorkflowTestCase { Name = "Test1", Input = "Input1" },
            new WorkflowTestCase { Name = "Test2", Input = "Input2" },
            new WorkflowTestCase { Name = "Test3", Input = "Input3" }
        };

        var summary = await harness.RunWorkflowTestSuiteAsync("TestSuite", workflow, testCases);

        Assert.Equal(3, summary.TotalTests);
        Assert.Equal(3, summary.PassedTests);
        Assert.Equal("TestSuite", summary.SuiteName);
        Assert.Equal("TestWorkflow", summary.WorkflowName);
    }

    [Fact]
    public async Task RunWorkflowTestSuiteAsync_CalculatesPassRate()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "output"));

        var testCases = new[]
        {
            new WorkflowTestCase { Name = "Pass1", Input = "Input1" },
            new WorkflowTestCase { Name = "Pass2", Input = "Input2" },
            new WorkflowTestCase { Name = "Fail", Input = "Input3", ExpectedOutputContains = "missing" }
        };

        var summary = await harness.RunWorkflowTestSuiteAsync("MixedSuite", workflow, testCases);

        Assert.Equal(2, summary.PassedTests);
        Assert.Equal(1, summary.FailedTests);
        Assert.InRange(summary.PassRate, 66.0, 67.0); // ~66.67%
    }

    [Fact]
    public async Task RunWorkflowTestSuiteAsync_ContinueOnFailure_RunsAllTests()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "output"));

        var testCases = new[]
        {
            new WorkflowTestCase { Name = "Fail1", Input = "Input1", ExpectedOutputContains = "missing" },
            new WorkflowTestCase { Name = "Pass", Input = "Input2" },
            new WorkflowTestCase { Name = "Fail2", Input = "Input3", ExpectedOutputContains = "also_missing" }
        };

        var options = new WorkflowTestOptions { ContinueOnFailure = true };
        var summary = await harness.RunWorkflowTestSuiteAsync("Suite", workflow, testCases, options);

        Assert.Equal(3, summary.Results.Count);
    }

    [Fact]
    public async Task RunWorkflowTestSuiteAsync_StopOnFailure_StopsAtFirstFailure()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "output"));

        var testCases = new[]
        {
            new WorkflowTestCase { Name = "Fail", Input = "Input1", ExpectedOutputContains = "missing" },
            new WorkflowTestCase { Name = "Pass", Input = "Input2" }
        };

        var options = new WorkflowTestOptions { ContinueOnFailure = false };
        var summary = await harness.RunWorkflowTestSuiteAsync("Suite", workflow, testCases, options);

        Assert.Single(summary.Results);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TIMEOUT
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunWorkflowTestAsync_Timeout_Fails()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = new MAFWorkflowAdapter(
            "SlowWorkflow",
            (prompt, ct) => SlowWorkflow(ct));

        var testCase = new WorkflowTestCase
        {
            Name = "Timeout Test",
            Input = "Test input"
        };

        var options = new WorkflowTestOptions { Timeout = TimeSpan.FromMilliseconds(50) };
        var result = await harness.RunWorkflowTestAsync(workflow, testCase, options);

        // Timeout triggers cancellation which is caught and recorded as error
        Assert.False(result.Passed);
        Assert.NotNull(result.FailureMessages);
        Assert.Contains(result.FailureMessages, m => m.Contains("Timeout") || m.Contains("error"));
    }

    private static async IAsyncEnumerable<WorkflowEvent> SlowWorkflow(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        yield return new ExecutorOutputEvent("executor", "output");
        yield return new WorkflowCompleteEvent();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ASSERTION RESULTS
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunWorkflowTestAsync_RecordsAssertionResults()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("step1", "out1"),
            ("step2", "out2"));

        var testCase = new WorkflowTestCase
        {
            Name = "Assertions Test",
            Input = "Test input",
            ExpectedExecutors = new[] { "step1", "step2" },
            StrictExecutorOrder = true,
            MaxDuration = TimeSpan.FromMinutes(1)
        };

        var result = await harness.RunWorkflowTestAsync(workflow, testCase);

        Assert.NotNull(result.AssertionResults);
        Assert.True(result.AssertionResults.Count >= 2);
        Assert.All(result.AssertionResults, a => Assert.True(a.Passed));
    }

    [Fact]
    public async Task RunWorkflowTestAsync_AllPassed_ReturnsTrue()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "output"));

        var testCases = new[]
        {
            new WorkflowTestCase { Name = "Test1", Input = "Input1" },
            new WorkflowTestCase { Name = "Test2", Input = "Input2" }
        };

        var summary = await harness.RunWorkflowTestSuiteAsync("Suite", workflow, testCases);

        Assert.True(summary.AllPassed);
    }

    [Fact]
    public async Task RunWorkflowTestAsync_AnyFailed_AllPassedReturnsFalse()
    {
        var harness = new WorkflowEvaluationHarness(verbose: false);
        var workflow = MAFWorkflowAdapter.FromSteps(
            "TestWorkflow",
            ("executor", "output"));

        var testCases = new[]
        {
            new WorkflowTestCase { Name = "Pass", Input = "Input1" },
            new WorkflowTestCase { Name = "Fail", Input = "Input2", ExpectedOutputContains = "missing" }
        };

        var summary = await harness.RunWorkflowTestSuiteAsync("Suite", workflow, testCases);

        Assert.False(summary.AllPassed);
    }
}
