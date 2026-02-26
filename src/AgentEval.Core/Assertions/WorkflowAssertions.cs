// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Models;

namespace AgentEval.Assertions;

/// <summary>
/// Fluent assertion builder for workflow execution results.
/// </summary>
/// <remarks>
/// <para>
/// Provides a fluent API for asserting on workflow execution:
/// </para>
/// <code>
/// result.Should()
///     .HaveStepCount(3)
///     .HaveExecutedInOrder("researcher", "writer", "editor")
///     .HaveNoErrors()
///     .ForExecutor("writer")
///         .HaveOutputContaining("article")
///         .HaveCompletedWithin(TimeSpan.FromSeconds(30))
///     .And()
///     .Validate();
/// </code>
/// </remarks>
public static class WorkflowAssertionsExtensions
{
    /// <summary>
    /// Begin fluent assertions on a workflow execution result.
    /// </summary>
    /// <param name="result">The workflow execution result to assert on.</param>
    /// <returns>A fluent assertion builder.</returns>
    public static WorkflowAssertionBuilder Should(this WorkflowExecutionResult result)
        => new(result);
}

/// <summary>
/// Fluent assertion builder for workflow execution results.
/// </summary>
public class WorkflowAssertionBuilder
{
    private readonly WorkflowExecutionResult _result;
    private readonly List<(string Message, string? Because)> _failures = [];
    private string? _currentBecause;

    /// <summary>
    /// Creates a new assertion builder for a workflow result.
    /// </summary>
    internal WorkflowAssertionBuilder(WorkflowExecutionResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }

    /// <summary>
    /// Assert the workflow has a specific number of executor steps.
    /// </summary>
    /// <param name="expected">Expected number of steps.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveStepCount(int expected, string? because = null)
    {
        _currentBecause = because;
        if (_result.Steps.Count != expected)
        {
            AddFailure($"Expected {expected} steps but found {_result.Steps.Count}");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow has at least a minimum number of steps.
    /// </summary>
    /// <param name="minimum">Minimum number of steps.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveAtLeastSteps(int minimum, string? because = null)
    {
        _currentBecause = because;
        if (_result.Steps.Count < minimum)
        {
            AddFailure($"Expected at least {minimum} steps but found {_result.Steps.Count}");
        }
        return this;
    }

    /// <summary>
    /// Assert a specific executor was invoked.
    /// </summary>
    /// <param name="executorId">The executor ID to check.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveInvokedExecutor(string executorId, string? because = null)
    {
        _currentBecause = because;
        if (!_result.Steps.Any(s => s.ExecutorId.Equals(executorId, StringComparison.OrdinalIgnoreCase)))
        {
            AddFailure($"Expected executor '{executorId}' was not invoked. " +
                          $"Invoked executors: [{string.Join(", ", _result.Steps.Select(s => s.ExecutorId))}]");
        }
        return this;
    }

    /// <summary>
    /// Assert executors were invoked in a specific order.
    /// </summary>
    /// <param name="executorIds">Expected executor order.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveExecutedInOrder(params string[] executorIds)
        => HaveExecutedInOrderBecause(null, executorIds);

    /// <summary>
    /// Assert executors were invoked in a specific order with reason.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    /// <param name="executorIds">Expected executor order.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveExecutedInOrderBecause(string? because, params string[] executorIds)
    {
        _currentBecause = because;
        var actualOrder = _result.Steps.Select(s => s.ExecutorId).ToList();

        if (actualOrder.Count != executorIds.Length)
        {
            AddFailure($"Expected executor order [{string.Join(" → ", executorIds)}] " +
                          $"but found [{string.Join(" → ", actualOrder)}] (different count)");
            return this;
        }

        for (int i = 0; i < executorIds.Length; i++)
        {
            if (!actualOrder[i].Equals(executorIds[i], StringComparison.OrdinalIgnoreCase))
            {
                AddFailure($"Expected executor order [{string.Join(" → ", executorIds)}] " +
                              $"but found [{string.Join(" → ", actualOrder)}]");
                break;
            }
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow completed within a time limit.
    /// </summary>
    /// <param name="maxDuration">Maximum acceptable duration.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveCompletedWithin(TimeSpan maxDuration, string? because = null)
    {
        _currentBecause = because;
        if (_result.TotalDuration > maxDuration)
        {
            AddFailure($"Expected completion within {maxDuration.TotalSeconds:F1}s " +
                          $"but took {_result.TotalDuration.TotalSeconds:F1}s");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow had no errors.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveNoErrors(string? because = null)
    {
        _currentBecause = because;
        if (_result.Errors?.Any() == true)
        {
            AddFailure($"Expected no errors but found {_result.Errors.Count}: " +
                          $"{string.Join(", ", _result.Errors.Select(e => $"[{e.ExecutorId}] {e.Message}"))}");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow completed successfully.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveSucceeded(string? because = null)
    {
        _currentBecause = because;
        if (!_result.IsSuccess)
        {
            var errorMsg = _result.Errors?.FirstOrDefault()?.Message ?? "Unknown error";
            AddFailure($"Expected workflow to succeed but it failed: {errorMsg}");
        }
        return this;
    }

    /// <summary>
    /// Assert the final output contains a string.
    /// </summary>
    /// <param name="expected">Expected substring.</param>
    /// <param name="caseSensitive">Whether comparison is case-sensitive.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveFinalOutputContaining(string expected, bool caseSensitive = false, string? because = null)
    {
        _currentBecause = because;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!_result.FinalOutput.Contains(expected, comparison))
        {
            AddFailure($"Expected final output to contain '{expected}' " +
                          $"but output was: \"{Truncate(_result.FinalOutput, 100)}\"");
        }
        return this;
    }

    /// <summary>
    /// Assert the final output matches a pattern.
    /// </summary>
    /// <param name="pattern">Regex pattern to match.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveFinalOutputMatching(string pattern, string? because = null)
    {
        _currentBecause = because;
        if (!System.Text.RegularExpressions.Regex.IsMatch(_result.FinalOutput, pattern))
        {
            AddFailure($"Expected final output to match pattern '{pattern}' " +
                          $"but output was: \"{Truncate(_result.FinalOutput, 100)}\"");
        }
        return this;
    }

    /// <summary>
    /// Assert the final output is not empty.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveNonEmptyOutput(string? because = null)
    {
        _currentBecause = because;
        if (string.IsNullOrWhiteSpace(_result.FinalOutput))
        {
            AddFailure("Expected non-empty final output but output was empty");
        }
        return this;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EDGE AND GRAPH ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Assert that a tool was called by any executor in the workflow.
    /// </summary>
    /// <param name="toolName">Name of the expected tool.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveAnyExecutorCalledTool(string toolName, string? because = null)
    {
        _currentBecause = because;
        var allToolCalls = _result.Steps
            .Where(s => s.HasToolCalls)
            .SelectMany(s => s.ToolCalls!)
            .ToList();

        if (!allToolCalls.Any(tc => tc.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase)))
        {
            var calledTools = allToolCalls.Select(tc => tc.Name).Distinct().ToList();
            AddFailure($"Expected tool '{toolName}' to be called by some executor, " +
                          $"but it was not. Tools called: [{string.Join(", ", calledTools)}]");
        }
        return this;
    }

    /// <summary>
    /// Assert the total number of tool calls across the entire workflow.
    /// </summary>
    /// <param name="expected">Expected total tool call count.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveTotalToolCallCount(int expected, string? because = null)
    {
        _currentBecause = because;
        var actual = _result.Steps
            .Where(s => s.HasToolCalls)
            .Sum(s => s.ToolCalls!.Count);

        if (actual != expected)
        {
            AddFailure($"Expected {expected} total tool calls across all executors but found {actual}");
        }
        return this;
    }

    /// <summary>
    /// Assert the total tool call count is at least a minimum.
    /// </summary>
    /// <param name="minimum">Minimum expected total tool call count.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveAtLeastTotalToolCalls(int minimum, string? because = null)
    {
        _currentBecause = because;
        var actual = _result.Steps
            .Where(s => s.HasToolCalls)
            .Sum(s => s.ToolCalls!.Count);

        if (actual < minimum)
        {
            AddFailure($"Expected at least {minimum} total tool calls but found {actual}");
        }
        return this;
    }

    /// <summary>
    /// Assert no tool calls across the entire workflow resulted in errors.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveNoToolErrors(string? because = null)
    {
        _currentBecause = because;
        var errors = _result.Steps
            .Where(s => s.HasToolCalls)
            .SelectMany(s => s.ToolCalls!)
            .Where(tc => tc.HasError)
            .ToList();

        if (errors.Count > 0)
        {
            var errorDetails = string.Join(", ", errors.Select(e =>
                $"[{e.ExecutorId ?? "?"}] {e.Name}: {e.Exception?.Message ?? "(unknown)"}"));
            AddFailure($"Expected no tool errors across workflow but found {errors.Count}: {errorDetails}");
        }
        return this;
    }

    /// <summary>
    /// Assert that a tool was called anywhere in the workflow and return a rich tool call assertion builder.
    /// Enables fluent chaining across all executors: <c>.HaveCalledTool("X").BeforeTool("Y").WithoutError().And()</c>
    /// </summary>
    /// <param name="toolName">Name of the expected tool.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    /// <returns>A <see cref="WorkflowToolCallAssertionBuilder"/> for further tool-specific assertions across all executors.</returns>
    /// <example>
    /// <code>
    /// result.Should()
    ///     .HaveCalledTool("SearchFlights", because: "must search before booking")
    ///         .BeforeTool("BookFlight", because: "can't book without search results")
    ///         .WithoutError()
    ///     .And()
    ///     .HaveCalledTool("BookFlight")
    ///         .WithoutError()
    ///     .And()
    ///     .HaveNoToolErrors()
    ///     .Validate();
    /// </code>
    /// </example>
    [StackTraceHidden]
    public WorkflowToolCallAssertionBuilder HaveCalledTool(string toolName, string? because = null)
    {
        _currentBecause = because;
        var allToolCalls = _result.Steps
            .Where(s => s.HasToolCalls)
            .SelectMany(s => s.ToolCalls!)
            .ToList();

        var call = allToolCalls.FirstOrDefault(tc =>
            tc.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));

        if (call == null)
        {
            var calledTools = allToolCalls.Select(tc => tc.Name).Distinct().ToList();
            AddFailure($"Expected tool '{toolName}' to be called in workflow, " +
                       $"but it was not. Tools called: [{string.Join(", ", calledTools)}]");
        }

        return new WorkflowToolCallAssertionBuilder(this, _result, call, toolName);
    }

    /// <summary>
    /// Assert the workflow has a graph structure.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveGraphStructure(string? because = null)
    {
        _currentBecause = because;
        if (_result.Graph == null)
        {
            AddFailure("Expected workflow to have graph structure but Graph was null");
        }
        return this;
    }

    /// <summary>
    /// Assert a specific edge was traversed during execution.
    /// </summary>
    /// <param name="sourceExecutorId">Source executor ID.</param>
    /// <param name="targetExecutorId">Target executor ID.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveTraversedEdge(string sourceExecutorId, string targetExecutorId, string? because = null)
    {
        _currentBecause = because;
        var traversedEdges = _result.Graph?.TraversedEdges;
        if (traversedEdges == null || !traversedEdges.Any(e =>
            e.SourceExecutorId.Equals(sourceExecutorId, StringComparison.OrdinalIgnoreCase) &&
            e.TargetExecutorId.Equals(targetExecutorId, StringComparison.OrdinalIgnoreCase)))
        {
            AddFailure($"Expected edge '{sourceExecutorId}' → '{targetExecutorId}' to be traversed but it was not. " +
                          $"Traversed edges: [{string.Join(", ", traversedEdges?.Select(e => $"{e.SourceExecutorId}→{e.TargetExecutorId}") ?? [])}]");
        }
        return this;
    }

    /// <summary>
    /// Assert a specific edge type was used.
    /// </summary>
    /// <param name="edgeType">Expected edge type.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveUsedEdgeType(EdgeType edgeType, string? because = null)
    {
        _currentBecause = because;
        var traversedEdges = _result.Graph?.TraversedEdges;
        if (traversedEdges == null || !traversedEdges.Any(e => e.EdgeType == edgeType))
        {
            AddFailure($"Expected edge type '{edgeType}' to be used but it was not. " +
                          $"Used edge types: [{string.Join(", ", traversedEdges?.Select(e => e.EdgeType.ToString()).Distinct() ?? [])}]");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow used conditional routing.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveConditionalRouting(string? because = null)
    {
        _currentBecause = because;
        if (!_result.HasConditionalRouting)
        {
            AddFailure("Expected workflow to have conditional routing but it did not");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow had parallel execution.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveParallelExecution(string? because = null)
    {
        _currentBecause = because;
        if (!_result.HasParallelExecution)
        {
            AddFailure("Expected workflow to have parallel execution but it did not");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow has a specific number of parallel branches.
    /// </summary>
    /// <param name="expectedCount">Expected number of parallel branches.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveParallelBranchCount(int expectedCount, string? because = null)
    {
        _currentBecause = because;
        var actualCount = _result.Graph?.ParallelBranches?.Count ?? 0;
        if (actualCount != expectedCount)
        {
            AddFailure($"Expected {expectedCount} parallel branches but found {actualCount}");
        }
        return this;
    }

    /// <summary>
    /// Assert a specific routing decision was made.
    /// </summary>
    /// <param name="deciderExecutorId">The executor that made the decision.</param>
    /// <param name="selectedEdgeId">The edge that was selected.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveRoutingDecision(string deciderExecutorId, string selectedEdgeId, string? because = null)
    {
        _currentBecause = because;
        var decisions = _result.RoutingDecisions;
        if (decisions == null || !decisions.Any(d =>
            d.DeciderExecutorId.Equals(deciderExecutorId, StringComparison.OrdinalIgnoreCase) &&
            d.SelectedEdgeId.Equals(selectedEdgeId, StringComparison.OrdinalIgnoreCase)))
        {
            AddFailure($"Expected routing decision from '{deciderExecutorId}' selecting '{selectedEdgeId}' " +
                          $"but it was not found");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow execution path matches expected sequence.
    /// </summary>
    /// <param name="expectedPath">Expected executor ID sequence.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveExecutionPath(params string[] expectedPath)
    {
        var actualPath = _result.GetExecutionPath().ToList();
        
        if (!actualPath.SequenceEqual(expectedPath, StringComparer.OrdinalIgnoreCase))
        {
            AddFailure($"Expected execution path [{string.Join(" → ", expectedPath)}] " +
                          $"but actual path was [{string.Join(" → ", actualPath)}]");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow execution path matches expected sequence.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    /// <param name="expectedPath">Expected executor ID sequence.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveExecutionPathBecause(string? because, params string[] expectedPath)
    {
        _currentBecause = because;
        return HaveExecutionPath(expectedPath);
    }

    /// <summary>
    /// Assert the workflow contains specific nodes.
    /// </summary>
    /// <param name="nodeIds">Expected node IDs.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveNodes(params string[] nodeIds)
    {
        if (_result.Graph == null)
        {
            AddFailure("Cannot check nodes: workflow has no graph structure");
            return this;
        }

        var graphNodeIds = _result.Graph.Nodes.Select(n => n.NodeId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingNodes = nodeIds.Where(id => !graphNodeIds.Contains(id)).ToList();
        
        if (missingNodes.Count > 0)
        {
            AddFailure($"Expected nodes [{string.Join(", ", missingNodes)}] not found in graph. " +
                          $"Available nodes: [{string.Join(", ", graphNodeIds)}]");
        }
        return this;
    }

    /// <summary>
    /// Assert the workflow contains specific nodes.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    /// <param name="nodeIds">Expected node IDs.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveNodesBecause(string? because, params string[] nodeIds)
    {
        _currentBecause = because;
        return HaveNodes(nodeIds);
    }

    /// <summary>
    /// Assert the workflow graph has a specific entry point.
    /// </summary>
    /// <param name="entryNodeId">Expected entry node ID.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowAssertionBuilder HaveEntryPoint(string entryNodeId, string? because = null)
    {
        _currentBecause = because;
        if (_result.Graph == null)
        {
            AddFailure("Cannot check entry point: workflow has no graph structure");
        }
        else if (!_result.Graph.EntryNodeId.Equals(entryNodeId, StringComparison.OrdinalIgnoreCase))
        {
            AddFailure($"Expected entry point '{entryNodeId}' but found '{_result.Graph.EntryNodeId}'");
        }
        return this;
    }

    /// <summary>
    /// Get assertion builder for edge-related assertions.
    /// </summary>
    public EdgeAssertionBuilder ForEdge(string sourceExecutorId, string targetExecutorId)
    {
        var edge = _result.Graph?.TraversedEdges?.FirstOrDefault(e =>
            e.SourceExecutorId.Equals(sourceExecutorId, StringComparison.OrdinalIgnoreCase) &&
            e.TargetExecutorId.Equals(targetExecutorId, StringComparison.OrdinalIgnoreCase));
        return new EdgeAssertionBuilder(this, edge, sourceExecutorId, targetExecutorId);
    }

    /// <summary>
    /// Get assertion builder for a specific executor step.
    /// </summary>
    /// <param name="executorId">The executor ID to assert on.</param>
    public ExecutorStepAssertionBuilder ForExecutor(string executorId)
    {
        var step = _result.Steps.FirstOrDefault(s => 
            s.ExecutorId.Equals(executorId, StringComparison.OrdinalIgnoreCase));
        return new ExecutorStepAssertionBuilder(this, step, executorId);
    }

    /// <summary>
    /// Get assertion builder for a specific step by index.
    /// </summary>
    /// <param name="index">Zero-based step index.</param>
    public ExecutorStepAssertionBuilder ForStep(int index)
    {
        var step = index >= 0 && index < _result.Steps.Count ? _result.Steps[index] : null;
        return new ExecutorStepAssertionBuilder(this, step, $"step[{index}]");
    }

    /// <summary>
    /// Validate all assertions and throw if any failed.
    /// </summary>
    /// <exception cref="WorkflowAssertionException">Thrown if any assertions failed.</exception>
    [StackTraceHidden]
    public void Validate()
    {
        if (_failures.Count > 0)
        {
            var failureMessages = _failures.Select(f => f.Message).ToList();
            var becauseClause = _failures.FirstOrDefault(f => !string.IsNullOrEmpty(f.Because)).Because;
            
            throw WorkflowAssertionException.Create(
                $"Workflow assertion failed ({_failures.Count} issue(s))",
                failures: failureMessages,
                expected: "(see failures list)",
                actual: "(see failures list)",
                context: _result.ToString(),
                suggestions: GetSuggestions(),
                because: becauseClause);
        }
    }

    /// <summary>
    /// Get whether all assertions have passed so far.
    /// </summary>
    public bool IsValid => _failures.Count == 0;

    /// <summary>
    /// Get the list of failure messages.
    /// </summary>
    public IReadOnlyList<string> Failures => _failures.Select(f => f.Message).ToList();

    /// <summary>
    /// Get the list of failure details including 'because' context.
    /// </summary>
    public IReadOnlyList<(string Message, string? Because)> FailureDetails => _failures;

    internal void AddFailure(string failure) => _failures.Add((failure, _currentBecause));

    private IReadOnlyList<string>? GetSuggestions()
    {
        var suggestions = new List<string>();
        
        foreach (var (message, _) in _failures)
        {
            if (message.Contains("not found"))
                suggestions.Add("Verify the executor or node ID matches the workflow configuration");
            if (message.Contains("took") && message.Contains("expected under"))
                suggestions.Add("Consider increasing timeout or optimizing executor performance");
            if (message.Contains("execution path"))
                suggestions.Add("Check workflow routing logic and edge conditions");
        }
        
        return suggestions.Count > 0 ? suggestions.Distinct().ToList() : null;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}

/// <summary>
/// Fluent assertion builder for individual executor steps.
/// </summary>
public class ExecutorStepAssertionBuilder
{
    private readonly WorkflowAssertionBuilder _parent;
    private readonly ExecutorStep? _step;
    private readonly string _executorId;
    private string? _currentBecause;

    internal ExecutorStepAssertionBuilder(WorkflowAssertionBuilder parent, ExecutorStep? step, string executorId)
    {
        _parent = parent;
        _step = step;
        _executorId = executorId;
    }

    /// <summary>
    /// Set a 'because' reason for the next assertion.
    /// </summary>
    /// <param name="because">The reason for the assertion.</param>
    public ExecutorStepAssertionBuilder Because(string because)
    {
        _currentBecause = because;
        return this;
    }

    private void AddFailure(string message)
    {
        // Append because to message if available
        var fullMessage = !string.IsNullOrEmpty(_currentBecause) 
            ? $"{message} because {_currentBecause}"
            : message;
        _parent.AddFailure(fullMessage);
        _currentBecause = null; // Reset after use
    }

    /// <summary>
    /// Adds a failure message from a child assertion builder (e.g., ExecutorToolCallAssertionBuilder).
    /// </summary>
    internal void AddFailureFromChild(string message) => _parent.AddFailure(message);

    /// <summary>
    /// Assert the executor's output contains a string.
    /// </summary>
    /// <param name="expected">Expected substring.</param>
    /// <param name="caseSensitive">Whether comparison is case-sensitive.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveOutputContaining(string expected, bool caseSensitive = false, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!_step.Output.Contains(expected, comparison))
            {
                AddFailure($"Executor '{_executorId}' output does not contain '{expected}'. " +
                                   $"Actual output: \"{Truncate(_step.Output, 80)}\"");
            }
        }
        return this;
    }

    /// <summary>
    /// Assert the executor completed within a time limit.
    /// </summary>
    /// <param name="maxDuration">Maximum acceptable duration.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveCompletedWithin(TimeSpan maxDuration, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else if (_step.Duration > maxDuration)
        {
            AddFailure($"Executor '{_executorId}' took {_step.Duration.TotalMilliseconds:F0}ms, " +
                               $"expected under {maxDuration.TotalMilliseconds:F0}ms");
        }
        return this;
    }

    /// <summary>
    /// Assert the executor called a specific tool and return a rich tool call assertion builder.
    /// Enables fluent chaining: <c>.HaveCalledTool("X").BeforeTool("Y").WithoutError().And()</c>
    /// </summary>
    /// <param name="toolName">Name of the expected tool.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    /// <returns>An <see cref="ExecutorToolCallAssertionBuilder"/> for further tool-specific assertions.</returns>
    /// <example>
    /// <code>
    /// result.Should()
    ///     .ForExecutor("FlightReservation")
    ///         .HaveCalledTool("SearchFlights", because: "must search before booking")
    ///             .BeforeTool("BookFlight")
    ///             .WithoutError()
    ///         .And()
    ///         .HaveCalledTool("BookFlight")
    ///             .WithoutError()
    ///         .And()
    ///     .And()
    ///     .Validate();
    /// </code>
    /// </example>
    [StackTraceHidden]
    public ExecutorToolCallAssertionBuilder HaveCalledTool(string toolName, string? because = null)
    {
        if (because != null) _currentBecause = because;

        ToolCallRecord? call = null;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else
        {
            call = _step.ToolCalls?.FirstOrDefault(t =>
                t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
            if (call == null)
            {
                var calledTools = _step.ToolCalls?.Select(t => t.Name).Distinct().ToList() ?? [];
                AddFailure($"Executor '{_executorId}' did not call tool '{toolName}'. " +
                           $"Called tools: [{string.Join(", ", calledTools)}]");
            }
        }

        return new ExecutorToolCallAssertionBuilder(this, _step, call, toolName, _executorId);
    }

    /// <summary>
    /// Assert the executor made a specific number of tool calls.
    /// </summary>
    /// <param name="expected">Expected number of tool calls.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveToolCallCount(int expected, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else
        {
            var actual = _step.ToolCalls?.Count ?? 0;
            if (actual != expected)
            {
                AddFailure($"Executor '{_executorId}' made {actual} tool calls, expected {expected}");
            }
        }
        return this;
    }

    /// <summary>
    /// Assert the executor called a tool with a specific argument value.
    /// </summary>
    /// <param name="toolName">Name of the expected tool.</param>
    /// <param name="argumentName">Name of the expected argument.</param>
    /// <param name="argumentValue">Expected argument value (case-insensitive substring match).</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveCalledToolWithArgument(
        string toolName, string argumentName, string argumentValue, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else
        {
            var matchingCalls = _step.ToolCalls?
                .Where(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                .ToList() ?? [];

            if (matchingCalls.Count == 0)
            {
                var calledTools = _step.ToolCalls?.Select(t => t.Name).ToList() ?? [];
                AddFailure($"Executor '{_executorId}' did not call tool '{toolName}'. " +
                                   $"Called tools: [{string.Join(", ", calledTools)}]");
            }
            else
            {
                var hasMatchingArg = matchingCalls.Any(tc =>
                    tc.Arguments != null &&
                    tc.Arguments.Any(a =>
                        a.Key.Equals(argumentName, StringComparison.OrdinalIgnoreCase) &&
                        a.Value?.ToString()?.Contains(argumentValue, StringComparison.OrdinalIgnoreCase) == true));

                if (!hasMatchingArg)
                {
                    AddFailure($"Executor '{_executorId}' called '{toolName}' but not with argument " +
                                       $"'{argumentName}' containing '{argumentValue}'");
                }
            }
        }
        return this;
    }

    /// <summary>
    /// Assert the executor has non-empty output.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveNonEmptyOutput(string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else if (string.IsNullOrWhiteSpace(_step.Output))
        {
            AddFailure($"Executor '{_executorId}' produced empty output");
        }
        return this;
    }

    /// <summary>
    /// Assert the executor had no tool call errors.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveNoToolErrors(string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else
        {
            var errors = _step.ToolCalls?.Where(tc => tc.HasError).ToList() ?? [];
            if (errors.Count > 0)
            {
                var errorDetails = string.Join(", ", errors.Select(e =>
                    $"{e.Name}: {e.Exception?.Message ?? "(unknown error)"}"));
                AddFailure($"Executor '{_executorId}' had {errors.Count} tool error(s): {errorDetails}");
            }
        }
        return this;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // EDGE-RELATED STEP ASSERTIONS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Assert the executor was reached via conditional routing.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveBeenConditionallyRouted(string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else if (!_step.WasConditionallyRouted)
        {
            AddFailure($"Executor '{_executorId}' was not reached via conditional routing");
        }
        return this;
    }

    /// <summary>
    /// Assert the executor is part of a parallel branch.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder BeInParallelBranch(string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else if (!_step.IsParallelBranch)
        {
            AddFailure($"Executor '{_executorId}' is not part of a parallel branch");
        }
        return this;
    }

    /// <summary>
    /// Assert the executor is part of a specific parallel branch.
    /// </summary>
    /// <param name="branchId">Expected branch ID.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder BeInParallelBranch(string branchId, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else if (!string.Equals(_step.ParallelBranchId, branchId, StringComparison.OrdinalIgnoreCase))
        {
            AddFailure($"Executor '{_executorId}' expected branch '{branchId}' but was in '{_step.ParallelBranchId ?? "(none)"}'");
        }
        return this;
    }

    /// <summary>
    /// Assert the executor has an incoming edge.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveIncomingEdge(string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else if (_step.IncomingEdge == null)
        {
            AddFailure($"Executor '{_executorId}' has no incoming edge");
        }
        return this;
    }

    /// <summary>
    /// Assert the executor has outgoing edges.
    /// </summary>
    /// <param name="expectedCount">Optional expected count of outgoing edges.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveOutgoingEdges(int? expectedCount = null, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else
        {
            var actualCount = _step.OutgoingEdges?.Count ?? 0;
            if (actualCount == 0)
            {
                AddFailure($"Executor '{_executorId}' has no outgoing edges");
            }
            else if (expectedCount.HasValue && actualCount != expectedCount.Value)
            {
                AddFailure($"Executor '{_executorId}' expected {expectedCount} outgoing edges but had {actualCount}");
            }
        }
        return this;
    }

    /// <summary>
    /// Assert the executor's incoming edge is of a specific type.
    /// </summary>
    /// <param name="expectedType">Expected edge type.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorStepAssertionBuilder HaveIncomingEdgeOfType(EdgeType expectedType, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_step == null)
        {
            AddFailure($"Executor '{_executorId}' was not found");
        }
        else if (_step.IncomingEdge == null)
        {
            AddFailure($"Executor '{_executorId}' has no incoming edge");
        }
        else if (_step.IncomingEdge.EdgeType != expectedType)
        {
            AddFailure($"Executor '{_executorId}' incoming edge expected type '{expectedType}' but was '{_step.IncomingEdge.EdgeType}'");
        }
        return this;
    }

    /// <summary>
    /// Return to the parent builder for continued chaining.
    /// </summary>
    public WorkflowAssertionBuilder And() => _parent;

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}

/// <summary>
/// Fluent assertion builder for a specific tool call across the entire workflow.
/// Enables rich per-tool assertions at the workflow level independent of executor.
/// </summary>
/// <remarks>
/// <para>
/// Returned by <see cref="WorkflowAssertionBuilder.HaveCalledTool"/>.
/// Chain assertions and call <c>.And()</c> to return to the workflow builder.
/// </para>
/// <code>
/// result.Should()
///     .HaveCalledTool("SearchFlights", because: "must search")
///         .BeforeTool("BookFlight", because: "can't book without results")
///         .WithoutError()
///     .And()
///     .HaveNoToolErrors()
///     .Validate();
/// </code>
/// </remarks>
public class WorkflowToolCallAssertionBuilder
{
    private readonly WorkflowAssertionBuilder _parent;
    private readonly WorkflowExecutionResult _result;
    private readonly ToolCallRecord? _call;
    private readonly string _toolName;

    internal WorkflowToolCallAssertionBuilder(
        WorkflowAssertionBuilder parent,
        WorkflowExecutionResult result,
        ToolCallRecord? call,
        string toolName)
    {
        _parent = parent;
        _result = result;
        _call = call;
        _toolName = toolName;
    }

    /// <summary>
    /// Assert this tool was called before another tool anywhere in the workflow.
    /// Uses the tool call Order property for comparison across all executors.
    /// </summary>
    /// <param name="otherToolName">The tool that should have been called after.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowToolCallAssertionBuilder BeforeTool(string otherToolName, string? because = null)
    {
        if (_call == null) return this; // Already reported missing

        var allToolCalls = _result.Steps
            .Where(s => s.HasToolCalls)
            .SelectMany(s => s.ToolCalls!)
            .ToList();

        var otherCall = allToolCalls.FirstOrDefault(tc =>
            tc.Name.Equals(otherToolName, StringComparison.OrdinalIgnoreCase));

        if (otherCall == null)
        {
            AddFailure(
                $"Expected '{_toolName}' to be called before '{otherToolName}' in workflow, " +
                $"but '{otherToolName}' was never called.",
                because);
        }
        else if (_call.Order >= otherCall.Order)
        {
            AddFailure(
                $"Expected '{_toolName}' (#{_call.Order}) to be called before '{otherToolName}' (#{otherCall.Order}) " +
                $"in workflow.",
                because);
        }
        return this;
    }

    /// <summary>
    /// Assert this tool was called after another tool anywhere in the workflow.
    /// </summary>
    /// <param name="otherToolName">The tool that should have been called before.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowToolCallAssertionBuilder AfterTool(string otherToolName, string? because = null)
    {
        if (_call == null) return this;

        var allToolCalls = _result.Steps
            .Where(s => s.HasToolCalls)
            .SelectMany(s => s.ToolCalls!)
            .ToList();

        var otherCall = allToolCalls.FirstOrDefault(tc =>
            tc.Name.Equals(otherToolName, StringComparison.OrdinalIgnoreCase));

        if (otherCall == null)
        {
            AddFailure(
                $"Expected '{_toolName}' to be called after '{otherToolName}' in workflow, " +
                $"but '{otherToolName}' was never called.",
                because);
        }
        else if (_call.Order <= otherCall.Order)
        {
            AddFailure(
                $"Expected '{_toolName}' (#{_call.Order}) to be called after '{otherToolName}' (#{otherCall.Order}) " +
                $"in workflow.",
                because);
        }
        return this;
    }

    /// <summary>
    /// Assert this tool completed without error.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowToolCallAssertionBuilder WithoutError(string? because = null)
    {
        if (_call == null) return this;

        if (_call.HasError)
        {
            AddFailure(
                $"Expected '{_toolName}' in workflow to complete without error, " +
                $"but got: {_call.Exception?.Message ?? "(unknown error)"}",
                because);
        }
        return this;
    }

    /// <summary>
    /// Assert a specific argument value (equality).
    /// </summary>
    /// <param name="paramName">The parameter name to check.</param>
    /// <param name="expectedValue">The expected value.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowToolCallAssertionBuilder WithArgument(string paramName, object expectedValue, string? because = null)
    {
        if (_call == null) return this;

        object? actualValue = null;
        var hasArgument = _call.Arguments?.TryGetValue(paramName, out actualValue) ?? false;

        if (!hasArgument)
        {
            var available = _call.Arguments?.Keys.Any() == true
                ? string.Join(", ", _call.Arguments.Keys)
                : "(none)";
            AddFailure(
                $"Expected '{_toolName}' in workflow to have argument '{paramName}', " +
                $"but available arguments: [{available}]",
                because);
        }
        else
        {
            var actualStr = actualValue is System.Text.Json.JsonElement je
                ? je.GetRawText().Trim('"')
                : actualValue?.ToString();
            var expectedStr = expectedValue?.ToString();

            if (!string.Equals(actualStr, expectedStr, StringComparison.Ordinal))
            {
                AddFailure(
                    $"Expected '{_toolName}' in workflow argument '{paramName}' = \"{expectedValue}\" " +
                    $"but was \"{actualValue}\"",
                    because);
            }
        }
        return this;
    }

    /// <summary>
    /// Assert an argument contains a substring (case-insensitive).
    /// </summary>
    /// <param name="paramName">The parameter name to check.</param>
    /// <param name="substring">The substring that should be contained.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowToolCallAssertionBuilder WithArgumentContaining(string paramName, string substring, string? because = null)
    {
        if (_call == null) return this;

        object? actualValue = null;
        var hasArgument = _call.Arguments?.TryGetValue(paramName, out actualValue) ?? false;

        if (!hasArgument)
        {
            AddFailure(
                $"Expected '{_toolName}' in workflow to have argument '{paramName}' containing " +
                $"'{substring}', but argument was not found.",
                because);
        }
        else
        {
            var actualStr = actualValue is System.Text.Json.JsonElement je
                ? je.GetString()
                : actualValue?.ToString();

            if (actualStr == null || !actualStr.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                AddFailure(
                    $"Expected '{_toolName}' in workflow argument '{paramName}' to contain " +
                    $"'{substring}' but was \"{Truncate(actualStr ?? "(null)", 100)}\"",
                    because);
            }
        }
        return this;
    }

    /// <summary>
    /// Assert the tool result contains a substring (case-insensitive).
    /// </summary>
    /// <param name="substring">The substring that should be in the result.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowToolCallAssertionBuilder WithResultContaining(string substring, string? because = null)
    {
        if (_call == null) return this;

        var resultStr = _call.Result?.ToString();
        if (resultStr == null || !resultStr.Contains(substring, StringComparison.OrdinalIgnoreCase))
        {
            AddFailure(
                $"Expected '{_toolName}' in workflow result to contain '{substring}' " +
                $"but was \"{Truncate(resultStr ?? "(null)", 100)}\"",
                because);
        }
        return this;
    }

    /// <summary>
    /// Assert tool duration is under a maximum.
    /// If timing information is not available, the assertion is skipped.
    /// </summary>
    /// <param name="max">The maximum allowed duration.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public WorkflowToolCallAssertionBuilder WithDurationUnder(TimeSpan max, string? because = null)
    {
        if (_call == null) return this;

        if (!_call.HasTiming)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AgentEval] Skipping duration assertion for '{_toolName}' in workflow " +
                "- timing not available.");
            return this;
        }

        if (_call.Duration > max)
        {
            AddFailure(
                $"Expected '{_toolName}' in workflow duration under {max.TotalMilliseconds:F0}ms " +
                $"but was {_call.Duration!.Value.TotalMilliseconds:F0}ms",
                because);
        }
        return this;
    }

    /// <summary>
    /// Return to the parent workflow builder for continued chaining.
    /// </summary>
    public WorkflowAssertionBuilder And() => _parent;

    private void AddFailure(string message, string? because)
    {
        var fullMessage = !string.IsNullOrEmpty(because)
            ? $"{message} because {because}"
            : message;
        _parent.AddFailure(fullMessage);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}

/// <summary>
/// Fluent assertion builder for a specific tool call within an executor step.
/// Enables rich per-tool assertions: ordering, error checking, argument validation.
/// </summary>
/// <remarks>
/// <para>
/// Returned by <see cref="ExecutorStepAssertionBuilder.HaveCalledTool"/>.
/// Chain assertions and call <c>.And()</c> to return to the executor builder.
/// </para>
/// <code>
/// result.Should()
///     .ForExecutor("FlightReservation")
///         .HaveCalledTool("SearchFlights")
///             .BeforeTool("BookFlight", because: "can't book without search results")
///             .WithoutError()
///         .And()
///     .And()
///     .Validate();
/// </code>
/// </remarks>
public class ExecutorToolCallAssertionBuilder
{
    private readonly ExecutorStepAssertionBuilder _parent;
    private readonly ExecutorStep? _step;
    private readonly ToolCallRecord? _call;
    private readonly string _toolName;
    private readonly string _executorId;

    internal ExecutorToolCallAssertionBuilder(
        ExecutorStepAssertionBuilder parent,
        ExecutorStep? step,
        ToolCallRecord? call,
        string toolName,
        string executorId)
    {
        _parent = parent;
        _step = step;
        _call = call;
        _toolName = toolName;
        _executorId = executorId;
    }

    /// <summary>
    /// Assert this tool was called before another tool within the same executor.
    /// </summary>
    /// <param name="otherToolName">The tool that should have been called after.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorToolCallAssertionBuilder BeforeTool(string otherToolName, string? because = null)
    {
        if (_call == null || _step == null) return this; // Already reported missing

        var otherCall = _step.ToolCalls?.FirstOrDefault(t =>
            t.Name.Equals(otherToolName, StringComparison.OrdinalIgnoreCase));

        if (otherCall == null)
        {
            AddFailure(
                $"Expected '{_toolName}' to be called before '{otherToolName}' in executor '{_executorId}', " +
                $"but '{otherToolName}' was never called.",
                because);
        }
        else if (_call.Order >= otherCall.Order)
        {
            AddFailure(
                $"Expected '{_toolName}' (#{_call.Order}) to be called before '{otherToolName}' (#{otherCall.Order}) " +
                $"in executor '{_executorId}'.",
                because);
        }
        return this;
    }

    /// <summary>
    /// Assert this tool was called after another tool within the same executor.
    /// </summary>
    /// <param name="otherToolName">The tool that should have been called before.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorToolCallAssertionBuilder AfterTool(string otherToolName, string? because = null)
    {
        if (_call == null || _step == null) return this;

        var otherCall = _step.ToolCalls?.FirstOrDefault(t =>
            t.Name.Equals(otherToolName, StringComparison.OrdinalIgnoreCase));

        if (otherCall == null)
        {
            AddFailure(
                $"Expected '{_toolName}' to be called after '{otherToolName}' in executor '{_executorId}', " +
                $"but '{otherToolName}' was never called.",
                because);
        }
        else if (_call.Order <= otherCall.Order)
        {
            AddFailure(
                $"Expected '{_toolName}' (#{_call.Order}) to be called after '{otherToolName}' (#{otherCall.Order}) " +
                $"in executor '{_executorId}'.",
                because);
        }
        return this;
    }

    /// <summary>
    /// Assert this tool completed without error.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorToolCallAssertionBuilder WithoutError(string? because = null)
    {
        if (_call == null) return this;

        if (_call.HasError)
        {
            AddFailure(
                $"Expected '{_toolName}' in executor '{_executorId}' to complete without error, " +
                $"but got: {_call.Exception?.Message ?? "(unknown error)"}",
                because);
        }
        return this;
    }

    /// <summary>
    /// Assert a specific argument value (equality).
    /// </summary>
    /// <param name="paramName">The parameter name to check.</param>
    /// <param name="expectedValue">The expected value.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorToolCallAssertionBuilder WithArgument(string paramName, object expectedValue, string? because = null)
    {
        if (_call == null) return this;

        object? actualValue = null;
        var hasArgument = _call.Arguments?.TryGetValue(paramName, out actualValue) ?? false;

        if (!hasArgument)
        {
            var available = _call.Arguments?.Keys.Any() == true
                ? string.Join(", ", _call.Arguments.Keys)
                : "(none)";
            AddFailure(
                $"Expected '{_toolName}' in executor '{_executorId}' to have argument '{paramName}', " +
                $"but available arguments: [{available}]",
                because);
        }
        else
        {
            var actualStr = actualValue is System.Text.Json.JsonElement je
                ? je.GetRawText().Trim('"')
                : actualValue?.ToString();
            var expectedStr = expectedValue?.ToString();

            if (!string.Equals(actualStr, expectedStr, StringComparison.Ordinal))
            {
                AddFailure(
                    $"Expected '{_toolName}' in executor '{_executorId}' argument '{paramName}' = \"{expectedValue}\" " +
                    $"but was \"{actualValue}\"",
                    because);
            }
        }
        return this;
    }

    /// <summary>
    /// Assert an argument contains a substring (case-insensitive).
    /// </summary>
    /// <param name="paramName">The parameter name to check.</param>
    /// <param name="substring">The substring that should be contained.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorToolCallAssertionBuilder WithArgumentContaining(string paramName, string substring, string? because = null)
    {
        if (_call == null) return this;

        object? actualValue = null;
        var hasArgument = _call.Arguments?.TryGetValue(paramName, out actualValue) ?? false;

        if (!hasArgument)
        {
            AddFailure(
                $"Expected '{_toolName}' in executor '{_executorId}' to have argument '{paramName}' containing " +
                $"'{substring}', but argument was not found.",
                because);
        }
        else
        {
            var actualStr = actualValue is System.Text.Json.JsonElement je
                ? je.GetString()
                : actualValue?.ToString();

            if (actualStr == null || !actualStr.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                AddFailure(
                    $"Expected '{_toolName}' in executor '{_executorId}' argument '{paramName}' to contain " +
                    $"'{substring}' but was \"{Truncate(actualStr ?? "(null)", 100)}\"",
                    because);
            }
        }
        return this;
    }

    /// <summary>
    /// Assert the tool result contains a substring (case-insensitive).
    /// </summary>
    /// <param name="substring">The substring that should be in the result.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorToolCallAssertionBuilder WithResultContaining(string substring, string? because = null)
    {
        if (_call == null) return this;

        var resultStr = _call.Result?.ToString();
        if (resultStr == null || !resultStr.Contains(substring, StringComparison.OrdinalIgnoreCase))
        {
            AddFailure(
                $"Expected '{_toolName}' in executor '{_executorId}' result to contain '{substring}' " +
                $"but was \"{Truncate(resultStr ?? "(null)", 100)}\"",
                because);
        }
        return this;
    }

    /// <summary>
    /// Assert tool duration is under a maximum.
    /// If timing information is not available, the assertion is skipped.
    /// </summary>
    /// <param name="max">The maximum allowed duration.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public ExecutorToolCallAssertionBuilder WithDurationUnder(TimeSpan max, string? because = null)
    {
        if (_call == null) return this;

        if (!_call.HasTiming)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AgentEval] Skipping duration assertion for '{_toolName}' in executor '{_executorId}' " +
                "- timing not available.");
            return this;
        }

        if (_call.Duration > max)
        {
            AddFailure(
                $"Expected '{_toolName}' in executor '{_executorId}' duration under {max.TotalMilliseconds:F0}ms " +
                $"but was {_call.Duration!.Value.TotalMilliseconds:F0}ms",
                because);
        }
        return this;
    }

    /// <summary>
    /// Return to the parent executor step builder for continued chaining.
    /// </summary>
    public ExecutorStepAssertionBuilder And() => _parent;

    /// <summary>
    /// Skip directly to the workflow builder, bypassing the executor step.
    /// This is a convenience shortcut equivalent to <c>.And().And()</c>.
    /// </summary>
    /// <remarks>
    /// Use <c>.Done()</c> when you don't need further executor-level assertions
    /// and want to continue at the workflow level:
    /// <code>
    /// result.Should()
    ///     .ForExecutor("FlightReservation")
    ///         .HaveCalledTool("SearchFlights")
    ///             .BeforeTool("BookFlight")
    ///             .WithoutError()
    ///         .Done()   // jumps straight to WorkflowAssertionBuilder
    ///     .Validate();
    /// </code>
    /// </remarks>
    public WorkflowAssertionBuilder Done() => _parent.And();

    private void AddFailure(string message, string? because)
    {
        var fullMessage = !string.IsNullOrEmpty(because)
            ? $"{message} because {because}"
            : message;
        _parent.AddFailureFromChild(fullMessage);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}

/// <summary>
/// Exception thrown when workflow assertions fail.
/// </summary>
public class WorkflowAssertionException : AgentEvalAssertionException
{
    /// <summary>
    /// Gets the list of all failures when multiple assertions failed.
    /// </summary>
    public IReadOnlyList<string> Failures { get; init; } = [];

    /// <summary>
    /// Creates a new workflow assertion exception.
    /// </summary>
    /// <param name="message">Failure message.</param>
    public WorkflowAssertionException(string message) 
        : base(message) 
    {
    }

    /// <summary>
    /// Creates a new workflow assertion exception with inner exception.
    /// </summary>
    /// <param name="message">Failure message.</param>
    /// <param name="inner">Inner exception.</param>
    public WorkflowAssertionException(string message, Exception inner) 
        : base(message, inner) 
    {
    }

    /// <summary>
    /// Creates a workflow assertion exception with full context.
    /// </summary>
    public static WorkflowAssertionException Create(
        string message,
        IReadOnlyList<string>? failures = null,
        string? expected = null,
        string? actual = null,
        string? context = null,
        IReadOnlyList<string>? suggestions = null,
        string? because = null)
    {
        var formattedMessage = FormatWorkflowMessage(message, failures, expected, actual, context, suggestions, because);
        
        return new WorkflowAssertionException(formattedMessage)
        {
            Failures = failures ?? [],
            Expected = expected,
            Actual = actual,
            Context = context,
            Suggestions = suggestions,
            Because = because
        };
    }

    private static string FormatWorkflowMessage(
        string message,
        IReadOnlyList<string>? failures,
        string? expected,
        string? actual,
        string? context,
        IReadOnlyList<string>? suggestions,
        string? because)
    {
        var sb = new System.Text.StringBuilder();
        
        // Main message with optional "because" reason
        sb.Append(message);
        if (!string.IsNullOrWhiteSpace(because))
        {
            sb.Append($" because {because}");
        }
        sb.AppendLine();

        // Failures list
        if (failures?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Failures:");
            foreach (var failure in failures)
            {
                sb.AppendLine($"  • {failure}");
            }
        }

        // Expected vs Actual
        if (expected != null || actual != null)
        {
            sb.AppendLine();
            if (expected != null)
                sb.AppendLine($"Expected: {expected}");
            if (actual != null)
                sb.AppendLine($"Actual:   {actual}");
        }

        // Additional context
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine();
            sb.AppendLine("Context:");
            foreach (var line in context.Split('\n'))
            {
                sb.AppendLine($"  {line.TrimEnd()}");
            }
        }

        // Suggestions
        if (suggestions?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Suggestions:");
            foreach (var suggestion in suggestions)
            {
                sb.AppendLine($"  → {suggestion}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// Fluent assertion builder for edge-related assertions.
/// </summary>
public class EdgeAssertionBuilder
{
    private readonly WorkflowAssertionBuilder _parent;
    private readonly EdgeExecution? _edge;
    private readonly string _sourceId;
    private readonly string _targetId;
    private string? _currentBecause;

    internal EdgeAssertionBuilder(WorkflowAssertionBuilder parent, EdgeExecution? edge, string sourceId, string targetId)
    {
        _parent = parent;
        _edge = edge;
        _sourceId = sourceId;
        _targetId = targetId;
    }

    /// <summary>
    /// Set a 'because' reason for the next assertion.
    /// </summary>
    /// <param name="because">The reason for the assertion.</param>
    public EdgeAssertionBuilder Because(string because)
    {
        _currentBecause = because;
        return this;
    }

    private void AddFailure(string message)
    {
        var fullMessage = !string.IsNullOrEmpty(_currentBecause) 
            ? $"{message} because {_currentBecause}"
            : message;
        _parent.AddFailure(fullMessage);
        _currentBecause = null;
    }

    /// <summary>
    /// Assert the edge exists.
    /// </summary>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public EdgeAssertionBuilder Exist(string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_edge == null)
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' was not traversed");
        }
        return this;
    }

    /// <summary>
    /// Assert the edge is of a specific type.
    /// </summary>
    /// <param name="expectedType">Expected edge type.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public EdgeAssertionBuilder BeOfType(EdgeType expectedType, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_edge == null)
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' was not found");
        }
        else if (_edge.EdgeType != expectedType)
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' expected type '{expectedType}' but was '{_edge.EdgeType}'");
        }
        return this;
    }

    /// <summary>
    /// Assert the conditional edge had a specific result.
    /// </summary>
    /// <param name="expectedResult">Expected condition result.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public EdgeAssertionBuilder HaveConditionResult(bool expectedResult, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_edge == null)
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' was not found");
        }
        else if (_edge.ConditionResult != expectedResult)
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' expected condition result '{expectedResult}' " +
                               $"but was '{_edge.ConditionResult}'");
        }
        return this;
    }

    /// <summary>
    /// Assert the switch edge matched a specific label.
    /// </summary>
    /// <param name="expectedLabel">Expected switch label.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public EdgeAssertionBuilder HaveMatchedSwitchLabel(string expectedLabel, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_edge == null)
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' was not found");
        }
        else if (!string.Equals(_edge.MatchedSwitchLabel, expectedLabel, StringComparison.OrdinalIgnoreCase))
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' expected switch label '{expectedLabel}' " +
                               $"but was '{_edge.MatchedSwitchLabel}'");
        }
        return this;
    }

    /// <summary>
    /// Assert the edge transferred specific data.
    /// </summary>
    /// <param name="expectedData">Expected data substring.</param>
    /// <param name="because">Optional reason for the assertion.</param>
    [StackTraceHidden]
    public EdgeAssertionBuilder HaveTransferredDataContaining(string expectedData, string? because = null)
    {
        if (because != null) _currentBecause = because;
        if (_edge == null)
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' was not found");
        }
        else if (_edge.TransferredData == null || !_edge.TransferredData.Contains(expectedData, StringComparison.OrdinalIgnoreCase))
        {
            AddFailure($"Edge '{_sourceId}' → '{_targetId}' expected data containing '{expectedData}' " +
                               $"but transferred: '{_edge.TransferredData ?? "(null)"}'");
        }
        return this;
    }

    /// <summary>
    /// Return to the parent builder for continued chaining.
    /// </summary>
    public WorkflowAssertionBuilder And() => _parent;
}
