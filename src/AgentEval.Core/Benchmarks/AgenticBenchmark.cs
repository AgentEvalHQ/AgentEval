// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Benchmarks;

/// <summary>
/// Benchmark for evaluating agent capabilities: tool usage accuracy, task completion, and multi-step reasoning.
/// </summary>
public class AgenticBenchmark
{
    private readonly IEvaluableAgent _agent;
    private readonly IEvaluator? _evaluator;
    private readonly IToolUsageExtractor _toolUsageExtractor;
    private readonly AgenticBenchmarkOptions _options;
    
    /// <summary>
    /// Creates a new agentic benchmark instance.
    /// </summary>
    /// <param name="agent">The agent to benchmark.</param>
    /// <param name="evaluator">Optional AI evaluator for task completion scoring.</param>
    /// <param name="options">Optional benchmark configuration.</param>
    /// <param name="toolUsageExtractor">
    /// Optional tool usage extractor for DI compatibility. 
    /// Defaults to <see cref="DefaultToolUsageExtractor.Instance"/> when not provided.
    /// </param>
    public AgenticBenchmark(
        IEvaluableAgent agent,
        IEvaluator? evaluator = null,
        AgenticBenchmarkOptions? options = null,
        IToolUsageExtractor? toolUsageExtractor = null)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _evaluator = evaluator;
        _toolUsageExtractor = toolUsageExtractor ?? DefaultToolUsageExtractor.Instance;
        _options = options ?? new AgenticBenchmarkOptions();
    }
    
    /// <summary>
    /// Run a tool accuracy benchmark measuring correct tool selection and parameter usage.
    /// </summary>
    public async Task<ToolAccuracyResult> RunToolAccuracyBenchmarkAsync(
        IEnumerable<ToolAccuracyTestCase> testCases,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ToolAccuracyTestResult>();
        
        foreach (var testCase in testCases)
        {
            if (_options.Verbose)
            {
                Console.WriteLine($"   Testing: {testCase.Name}...");
            }
            
            try
            {
                var response = await _agent.InvokeAsync(testCase.Prompt, cancellationToken);
                var toolUsage = _toolUsageExtractor.Extract(response);
                
                // Check if expected tools were called
                var toolsCalledCorrectly = new List<string>();
                var toolsMissed = new List<string>();
                var unexpectedTools = new List<string>();
                var parameterErrors = new List<string>();
                
                foreach (var expectedTool in testCase.ExpectedTools)
                {
                    var matchingCall = toolUsage.Calls.FirstOrDefault(c => 
                        c.Name.Equals(expectedTool.Name, StringComparison.OrdinalIgnoreCase));
                    
                    if (matchingCall == null)
                    {
                        toolsMissed.Add(expectedTool.Name);
                    }
                    else
                    {
                        // Verify required parameters
                        var callArgs = matchingCall.Arguments ?? new Dictionary<string, object?>();
                        var missingParams = expectedTool.RequiredParameters
                            .Where(p => !callArgs.ContainsKey(p))
                            .ToList();
                        
                        if (missingParams.Any())
                        {
                            parameterErrors.Add($"{expectedTool.Name}: missing {string.Join(", ", missingParams)}");
                        }
                        else
                        {
                            toolsCalledCorrectly.Add(expectedTool.Name);
                        }
                    }
                }
                
                // Check for unexpected tools
                var expectedToolNames = testCase.ExpectedTools.Select(t => t.Name.ToLowerInvariant()).ToHashSet();
                foreach (var call in toolUsage.Calls)
                {
                    if (!expectedToolNames.Contains(call.Name.ToLowerInvariant()))
                    {
                        if (!testCase.AllowExtraTools)
                        {
                            unexpectedTools.Add(call.Name);
                        }
                    }
                }
                
                var testResult = new ToolAccuracyTestResult
                {
                    TestCaseName = testCase.Name,
                    Passed = !toolsMissed.Any() && !parameterErrors.Any() && !unexpectedTools.Any(),
                    ToolsCalledCorrectly = toolsCalledCorrectly,
                    ToolsMissed = toolsMissed,
                    UnexpectedTools = unexpectedTools,
                    ParameterErrors = parameterErrors,
                    TotalToolsCalled = toolUsage.Count
                };
                
                results.Add(testResult);
            }
            catch (Exception ex)
            {
                results.Add(new ToolAccuracyTestResult
                {
                    TestCaseName = testCase.Name,
                    Passed = false,
                    Error = ex.Message
                });
            }
        }
        
        return new ToolAccuracyResult
        {
            AgentName = _agent.Name,
            TotalTests = results.Count,
            PassedTests = results.Count(r => r.Passed),
            Results = results,
            OverallAccuracy = results.Count > 0 
                ? (double)results.Count(r => r.Passed) / results.Count 
                : 0
        };
    }
    
    /// <summary>
    /// Run a task completion benchmark measuring end-to-end task success.
    /// </summary>
    public async Task<TaskCompletionResult> RunTaskCompletionBenchmarkAsync(
        IEnumerable<TaskCompletionTestCase> testCases,
        CancellationToken cancellationToken = default)
    {
        if (_evaluator == null)
        {
            throw new InvalidOperationException("Task completion benchmark requires an AI evaluator. Please provide one in the constructor.");
        }
        
        var results = new List<TaskCompletionTestResult>();
        
        foreach (var testCase in testCases)
        {
            if (_options.Verbose)
            {
                Console.WriteLine($"   Testing: {testCase.Name}...");
            }
            
            try
            {
                var response = await _agent.InvokeAsync(testCase.Prompt, cancellationToken);
                
                // Use AI to evaluate task completion
                var evaluationCriteria = new List<string>(testCase.CompletionCriteria);
                
                if (_options.AddDefaultCompletionCriteria)
                {
                    evaluationCriteria.Add("The response fully addresses the user's request");
                    evaluationCriteria.Add("The output is complete and actionable");
                }
                
                var evaluation = await _evaluator.EvaluateAsync(
                    testCase.Prompt,
                    response.Text,
                    evaluationCriteria,
                    cancellationToken);
                
                var testResult = new TaskCompletionTestResult
                {
                    TestCaseName = testCase.Name,
                    Passed = evaluation.OverallScore >= testCase.PassingScore,
                    Score = evaluation.OverallScore,
                    ActualOutput = response.Text,
                    EvaluationSummary = evaluation.Summary,
                    CriteriaResults = evaluation.CriteriaResults.ToList()
                };
                
                results.Add(testResult);
            }
            catch (Exception ex)
            {
                results.Add(new TaskCompletionTestResult
                {
                    TestCaseName = testCase.Name,
                    Passed = false,
                    Score = 0,
                    Error = ex.Message
                });
            }
        }
        
        return new TaskCompletionResult
        {
            AgentName = _agent.Name,
            TotalTests = results.Count,
            PassedTests = results.Count(r => r.Passed),
            Results = results,
            AverageScore = results.Count > 0 
                ? results.Where(r => r.Score.HasValue).Average(r => r.Score!.Value) 
                : 0
        };
    }
    
    /// <summary>
    /// Run a multi-step reasoning benchmark.
    /// </summary>
    public async Task<MultiStepReasoningResult> RunMultiStepReasoningBenchmarkAsync(
        IEnumerable<MultiStepTestCase> testCases,
        CancellationToken cancellationToken = default)
    {
        var results = new List<MultiStepTestResult>();
        
        foreach (var testCase in testCases)
        {
            if (_options.Verbose)
            {
                Console.WriteLine($"   Testing: {testCase.Name}...");
            }
            
            try
            {
                var response = await _agent.InvokeAsync(testCase.Prompt, cancellationToken);
                var toolUsage = _toolUsageExtractor.Extract(response);
                
                // Check step sequence
                var stepResults = new List<StepResult>();
                var previousStepPassed = true;
                
                for (int i = 0; i < testCase.ExpectedSteps.Count; i++)
                {
                    var expectedStep = testCase.ExpectedSteps[i];
                    var matchingCall = toolUsage.Calls.ElementAtOrDefault(i);
                    
                    var stepPassed = matchingCall != null && 
                        matchingCall.Name.Equals(expectedStep.ToolName, StringComparison.OrdinalIgnoreCase);
                    
                    // Check dependencies
                    var dependenciesMet = true;
                    if (expectedStep.DependsOnStep.HasValue)
                    {
                        var dependentStep = stepResults.ElementAtOrDefault(expectedStep.DependsOnStep.Value);
                        if (dependentStep == null || !dependentStep.Passed)
                        {
                            dependenciesMet = false;
                        }
                    }
                    
                    stepResults.Add(new StepResult
                    {
                        StepNumber = i + 1,
                        ExpectedTool = expectedStep.ToolName,
                        ActualTool = matchingCall?.Name,
                        Passed = stepPassed && (previousStepPassed || !testCase.RequireSequentialExecution),
                        DependenciesMet = dependenciesMet
                    });
                    
                    previousStepPassed = stepPassed;
                }
                
                var testResult = new MultiStepTestResult
                {
                    TestCaseName = testCase.Name,
                    Passed = stepResults.All(s => s.Passed),
                    TotalSteps = testCase.ExpectedSteps.Count,
                    CompletedSteps = stepResults.Count(s => s.Passed),
                    StepResults = stepResults
                };
                
                results.Add(testResult);
            }
            catch (Exception ex)
            {
                results.Add(new MultiStepTestResult
                {
                    TestCaseName = testCase.Name,
                    Passed = false,
                    Error = ex.Message
                });
            }
        }
        
        return new MultiStepReasoningResult
        {
            AgentName = _agent.Name,
            TotalTests = results.Count,
            PassedTests = results.Count(r => r.Passed),
            Results = results,
            AverageStepCompletion = results.Any(r => r.TotalSteps > 0)
                ? results.Where(r => r.TotalSteps > 0).Average(r => (double)r.CompletedSteps / r.TotalSteps)
                : 0
        };
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// TEST CASE DEFINITIONS
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Options for agentic benchmarks.
/// </summary>
public class AgenticBenchmarkOptions
{
    /// <summary>
    /// Whether to print progress to console during benchmark execution.
    /// </summary>
    public bool Verbose { get; set; } = true;

    /// <summary>
    /// Whether to automatically add standard completion criteria 
    /// ("fully addresses request", "complete and actionable") to task completion evaluations.
    /// Default: <see langword="true"/> for backward compatibility.
    /// Set to <see langword="false"/> for strict criteria-only evaluation.
    /// </summary>
    public bool AddDefaultCompletionCriteria { get; set; } = true;
}

/// <summary>
/// Test case for tool accuracy benchmark.
/// </summary>
public class ToolAccuracyTestCase
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public required IReadOnlyList<ExpectedTool> ExpectedTools { get; init; }
    public bool AllowExtraTools { get; init; } = true;
}

/// <summary>
/// Expected tool call with required parameters.
/// </summary>
public class ExpectedTool
{
    public required string Name { get; init; }
    public IReadOnlyList<string> RequiredParameters { get; init; } = [];
}

/// <summary>
/// Test case for task completion benchmark.
/// </summary>
public class TaskCompletionTestCase
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public IReadOnlyList<string> CompletionCriteria { get; init; } = [];
    public int PassingScore { get; init; } = EvaluationDefaults.DefaultPassingScore;
}

/// <summary>
/// Test case for multi-step reasoning benchmark.
/// </summary>
public class MultiStepTestCase
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public required IReadOnlyList<ExpectedStep> ExpectedSteps { get; init; }
    public bool RequireSequentialExecution { get; init; } = true;
}

/// <summary>
/// Expected step in a multi-step workflow.
/// </summary>
public class ExpectedStep
{
    public required string ToolName { get; init; }
    public int? DependsOnStep { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════════
// RESULT TYPES
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Results from a tool accuracy benchmark.
/// </summary>
public class ToolAccuracyResult
{
    public required string AgentName { get; init; }
    public int TotalTests { get; init; }
    public int PassedTests { get; init; }
    public double OverallAccuracy { get; init; }
    public IReadOnlyList<ToolAccuracyTestResult> Results { get; init; } = [];
    
    public override string ToString() =>
        $"Tool Accuracy: {AgentName}\n" +
        $"  Passed: {PassedTests}/{TotalTests} ({OverallAccuracy:P1})";
}

public class ToolAccuracyTestResult
{
    public required string TestCaseName { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<string> ToolsCalledCorrectly { get; init; } = [];
    public IReadOnlyList<string> ToolsMissed { get; init; } = [];
    public IReadOnlyList<string> UnexpectedTools { get; init; } = [];
    public IReadOnlyList<string> ParameterErrors { get; init; } = [];
    public int TotalToolsCalled { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Results from a task completion benchmark.
/// </summary>
public class TaskCompletionResult
{
    public required string AgentName { get; init; }
    public int TotalTests { get; init; }
    public int PassedTests { get; init; }
    public double AverageScore { get; init; }
    public IReadOnlyList<TaskCompletionTestResult> Results { get; init; } = [];
    
    public override string ToString() =>
        $"Task Completion: {AgentName}\n" +
        $"  Passed: {PassedTests}/{TotalTests} | Avg Score: {AverageScore:F1}/100";
}

public class TaskCompletionTestResult
{
    public required string TestCaseName { get; init; }
    public bool Passed { get; init; }
    public double? Score { get; init; }
    public string? ActualOutput { get; init; }
    public string? EvaluationSummary { get; init; }
    public IReadOnlyList<CriterionResult> CriteriaResults { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>
/// Results from a multi-step reasoning benchmark.
/// </summary>
public class MultiStepReasoningResult
{
    public required string AgentName { get; init; }
    public int TotalTests { get; init; }
    public int PassedTests { get; init; }
    public double AverageStepCompletion { get; init; }
    public IReadOnlyList<MultiStepTestResult> Results { get; init; } = [];
    
    public override string ToString() =>
        $"Multi-Step Reasoning: {AgentName}\n" +
        $"  Passed: {PassedTests}/{TotalTests} | Avg Step Completion: {AverageStepCompletion:P1}";
}

public class MultiStepTestResult
{
    public required string TestCaseName { get; init; }
    public bool Passed { get; init; }
    public int TotalSteps { get; init; }
    public int CompletedSteps { get; init; }
    public IReadOnlyList<StepResult> StepResults { get; init; } = [];
    public string? Error { get; init; }
}

public class StepResult
{
    public int StepNumber { get; init; }
    public required string ExpectedTool { get; init; }
    public string? ActualTool { get; init; }
    public bool Passed { get; init; }
    public bool DependenciesMet { get; init; } = true;
}
