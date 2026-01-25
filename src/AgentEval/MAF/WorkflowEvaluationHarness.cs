// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.MAF;

/// <summary>
/// evaluation harness specifically designed for workflow/multi-agent testing.
/// Provides visibility into individual executor steps and workflow-level assertions.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="MAFEvaluationHarness"/> which treats agents as black boxes,
/// <see cref="WorkflowEvaluationHarness"/> captures per-executor details:
/// </para>
/// <list type="bullet">
///   <item>Individual executor outputs</item>
///   <item>Per-executor timing</item>
///   <item>Tool calls per executor</item>
///   <item>Workflow orchestration order</item>
/// </list>
/// </remarks>
public class WorkflowEvaluationHarness
{
    private readonly IEvaluator? _evaluator;
    private readonly IAgentEvalLogger _logger;

    /// <summary>
    /// Creates a workflow evaluation harness without AI evaluation.
    /// </summary>
    /// <param name="verbose">Whether to output verbose logging.</param>
    public WorkflowEvaluationHarness(bool verbose = false)
        : this(null, verbose ? new ConsoleAgentEvalLogger() : NullAgentEvalLogger.Instance)
    {
    }

    /// <summary>
    /// Creates a workflow evaluation harness with AI evaluation support.
    /// </summary>
    /// <param name="evaluator">Evaluator for AI-based assessment.</param>
    /// <param name="logger">Logger for output.</param>
    public WorkflowEvaluationHarness(IEvaluator? evaluator, IAgentEvalLogger logger)
    {
        _evaluator = evaluator;
        _logger = logger ?? NullAgentEvalLogger.Instance;
    }

    /// <summary>
    /// Runs a workflow test and returns detailed results.
    /// </summary>
    /// <param name="workflow">The workflow to test.</param>
    /// <param name="testCase">The test case to run.</param>
    /// <param name="options">Optional test options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detailed test result with per-executor information.</returns>
    public async Task<WorkflowTestResult> RunWorkflowTestAsync(
        IWorkflowEvaluableAgent workflow,
        WorkflowTestCase testCase,
        WorkflowTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(testCase);

        options ??= new WorkflowTestOptions();
        var stopwatch = Stopwatch.StartNew();

        _logger.LogInformation($"🔄 Running workflow test: {testCase.Name}");
        _logger.LogDebug($"   Workflow: {workflow.Name}");
        if (workflow.WorkflowType != null)
        {
            _logger.LogDebug($"   Type: {workflow.WorkflowType}");
        }

        WorkflowExecutionResult? executionResult = null;
        Exception? error = null;
        var assertionResults = new List<WorkflowAssertionResult>();
        var failureMessages = new List<string>();

        try
        {
            // Create timeout cancellation
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.Timeout);

            executionResult = await workflow.ExecuteWorkflowAsync(testCase.Input, timeoutCts.Token);

            if (options.Verbose)
            {
                LogExecutionDetails(executionResult);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            error = new TimeoutException($"Workflow execution timed out after {options.Timeout.TotalSeconds}s");
            failureMessages.Add($"Timeout: Workflow did not complete within {options.Timeout.TotalSeconds}s");
            _logger.LogError($"⏱️ Timeout: Workflow did not complete within {options.Timeout.TotalSeconds}s");
        }
        catch (Exception ex)
        {
            error = ex;
            failureMessages.Add($"Exception: {ex.Message}");
            _logger.LogError($"❌ Workflow execution failed: {ex.Message}");
        }

        stopwatch.Stop();

        // Run built-in assertions if execution succeeded
        if (executionResult != null)
        {
            // Check expected executors
            if (testCase.ExpectedExecutors?.Any() == true)
            {
                var actualExecutors = executionResult.Steps.Select(s => s.ExecutorId).ToList();

                if (testCase.StrictExecutorOrder)
                {
                    var orderMatches = actualExecutors.SequenceEqual(testCase.ExpectedExecutors);
                    assertionResults.Add(new WorkflowAssertionResult
                    {
                        AssertionName = "Executor Order",
                        Passed = orderMatches,
                        FailureMessage = orderMatches ? null : 
                            $"Expected order [{string.Join(" → ", testCase.ExpectedExecutors)}] but got [{string.Join(" → ", actualExecutors)}]"
                    });

                    if (!orderMatches)
                    {
                        failureMessages.Add($"Executor order mismatch");
                    }
                }
                else
                {
                    var allPresent = testCase.ExpectedExecutors.All(e => 
                        actualExecutors.Contains(e, StringComparer.OrdinalIgnoreCase));
                    var missingExecutors = testCase.ExpectedExecutors
                        .Where(e => !actualExecutors.Contains(e, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    assertionResults.Add(new WorkflowAssertionResult
                    {
                        AssertionName = "Expected Executors",
                        Passed = allPresent,
                        FailureMessage = allPresent ? null : 
                            $"Missing executors: [{string.Join(", ", missingExecutors)}]"
                    });

                    if (!allPresent)
                    {
                        failureMessages.Add($"Missing executors: {string.Join(", ", missingExecutors)}");
                    }
                }
            }

            // Check expected output content
            if (!string.IsNullOrEmpty(testCase.ExpectedOutputContains))
            {
                var containsExpected = executionResult.FinalOutput
                    .Contains(testCase.ExpectedOutputContains, StringComparison.OrdinalIgnoreCase);
                
                assertionResults.Add(new WorkflowAssertionResult
                {
                    AssertionName = "Output Contains",
                    Passed = containsExpected,
                    FailureMessage = containsExpected ? null : 
                        $"Output does not contain: \"{testCase.ExpectedOutputContains}\""
                });

                if (!containsExpected)
                {
                    failureMessages.Add($"Output missing expected content");
                }
            }

            // Check max duration
            if (testCase.MaxDuration.HasValue)
            {
                var withinTime = executionResult.TotalDuration <= testCase.MaxDuration.Value;
                assertionResults.Add(new WorkflowAssertionResult
                {
                    AssertionName = "Duration",
                    Passed = withinTime,
                    FailureMessage = withinTime ? null : 
                        $"Took {executionResult.TotalDuration.TotalSeconds:F1}s, expected under {testCase.MaxDuration.Value.TotalSeconds:F1}s"
                });

                if (!withinTime)
                {
                    failureMessages.Add($"Duration exceeded maximum");
                }
            }

            // Check for errors
            if (executionResult.Errors?.Any() == true)
            {
                assertionResults.Add(new WorkflowAssertionResult
                {
                    AssertionName = "No Errors",
                    Passed = false,
                    FailureMessage = $"Workflow had {executionResult.Errors.Count} error(s): {executionResult.Errors[0].Message}"
                });
                failureMessages.Add($"Workflow errors: {executionResult.Errors.Count}");
            }
        }

        // Determine overall pass/fail
        var passed = error == null && 
                     executionResult != null && 
                     assertionResults.All(a => a.Passed);

        // Log result
        if (passed)
        {
            _logger.LogInformation($"✅ PASSED: {testCase.Name} ({stopwatch.Elapsed.TotalMilliseconds:F0}ms)");
        }
        else
        {
            _logger.LogError($"❌ FAILED: {testCase.Name}");
            foreach (var msg in failureMessages)
            {
                _logger.LogError($"   • {msg}");
            }
        }

        return new WorkflowTestResult
        {
            TestName = testCase.Name,
            WorkflowName = workflow.Name,
            Input = testCase.Input,
            Passed = passed,
            ExecutionResult = executionResult,
            TotalDuration = stopwatch.Elapsed,
            Error = error,
            AssertionResults = assertionResults,
            FailureMessages = failureMessages.Count > 0 ? failureMessages : null
        };
    }

    /// <summary>
    /// Runs multiple workflow tests.
    /// </summary>
    /// <param name="suiteName">Name of the test suite.</param>
    /// <param name="workflow">The workflow to test.</param>
    /// <param name="testCases">Test cases to run.</param>
    /// <param name="options">Optional test options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Summary of all test results.</returns>
    public async Task<WorkflowTestSummary> RunWorkflowTestSuiteAsync(
        string suiteName,
        IWorkflowEvaluableAgent workflow,
        IEnumerable<WorkflowTestCase> testCases,
        WorkflowTestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(suiteName);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(testCases);

        options ??= new WorkflowTestOptions();
        var results = new List<WorkflowTestResult>();
        var overallStopwatch = Stopwatch.StartNew();

        _logger.LogInformation("╔══════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation($"║  WORKFLOW TEST SUITE: {suiteName.PadRight(43)}║");
        _logger.LogInformation($"║  Workflow: {workflow.Name.PadRight(54)}║");
        _logger.LogInformation("╚══════════════════════════════════════════════════════════════════╝");

        foreach (var testCase in testCases)
        {
            var result = await RunWorkflowTestAsync(workflow, testCase, options, cancellationToken);
            results.Add(result);

            if (!result.Passed && !options.ContinueOnFailure)
            {
                _logger.LogWarning("⛔ Stopping suite - ContinueOnFailure is false");
                break;
            }
        }

        overallStopwatch.Stop();

        var summary = new WorkflowTestSummary
        {
            SuiteName = suiteName,
            WorkflowName = workflow.Name,
            Results = results,
            TotalTests = results.Count,
            PassedTests = results.Count(r => r.Passed),
            FailedTests = results.Count(r => !r.Passed),
            TotalDuration = overallStopwatch.Elapsed
        };

        LogSummary(summary);

        return summary;
    }

    private void LogExecutionDetails(WorkflowExecutionResult result)
    {
        _logger.LogDebug($"   Duration: {result.TotalDuration.TotalMilliseconds:F0}ms");
        _logger.LogDebug($"   Steps: {result.Steps.Count}");
        
        foreach (var step in result.Steps)
        {
            var toolInfo = step.HasToolCalls ? $" [🔧 {step.ToolCalls!.Count} tools]" : "";
            _logger.LogDebug($"     [{step.StepIndex}] {step.ExecutorId}: {step.Duration.TotalMilliseconds:F0}ms{toolInfo}");
            
            if (!string.IsNullOrEmpty(step.Output) && step.Output.Length <= 100)
            {
                _logger.LogDebug($"         → \"{step.Output}\"");
            }
            else if (!string.IsNullOrEmpty(step.Output))
            {
                _logger.LogDebug($"         → \"{step.Output[..97]}...\" ({step.Output.Length} chars)");
            }
        }
    }

    private void LogSummary(WorkflowTestSummary summary)
    {
        _logger.LogInformation("\n╔══════════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║                     WORKFLOW TEST SUMMARY                        ║");
        _logger.LogInformation("╚══════════════════════════════════════════════════════════════════╝\n");

        _logger.LogInformation($"   Suite: {summary.SuiteName}");
        _logger.LogInformation($"   Workflow: {summary.WorkflowName}");
        _logger.LogInformation($"   Tests: {summary.PassedTests}/{summary.TotalTests} passed ({summary.PassRate:F0}%)");
        _logger.LogInformation($"   Duration: {summary.TotalDuration.TotalMilliseconds:F0}ms");

        foreach (var result in summary.Results)
        {
            var status = result.Passed ? "✅" : "❌";
            var level = result.Passed ? LogLevel.Information : LogLevel.Error;
            var stepCount = result.ExecutionResult?.Steps.Count ?? 0;
            _logger.Log(level, $"   {status} {result.TestName} ({stepCount} steps)");
        }

        if (summary.AllPassed)
        {
            _logger.LogInformation("\n   🎉 All workflow tests passed!\n");
        }
        else
        {
            _logger.LogWarning($"\n   ⚠️ {summary.FailedTests} test(s) need attention.\n");
        }
    }
}
