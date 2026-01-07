// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentEval.Models;

namespace AgentEval.Assertions;

/// <summary>
/// Fluent assertions entry point for ToolUsageReport.
/// </summary>
public class ToolUsageAssertions
{
    private readonly ToolUsageReport _report;
    private readonly string? _subjectName;
    
    public ToolUsageAssertions(ToolUsageReport report, string? subjectName = null)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _subjectName = subjectName;
    }
    
    /// <summary>Assert that a specific tool was called at least once.</summary>
    /// <param name="toolName">The name of the tool expected to be called.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion HaveCalledTool(string toolName, string? because = null)
    {
        if (!_report.WasToolCalled(toolName))
        {
            var calledTools = _report.UniqueToolNames.ToList();
            var suggestions = new List<string>();
            
            // Find similar tool names for suggestions
            var similar = calledTools.Where(t => 
                t.Contains(toolName, StringComparison.OrdinalIgnoreCase) ||
                toolName.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            
            if (similar.Count > 0)
            {
                suggestions.Add($"Did you mean: {string.Join(", ", similar)}?");
            }
            
            if (calledTools.Count == 0)
            {
                suggestions.Add("Verify the agent has access to the expected tools");
                suggestions.Add("Check if the prompt clearly requests tool usage");
            }
            
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected tool '{toolName}' to be called, but it was not.",
                    toolName: toolName,
                    calledTools: calledTools,
                    expected: $"Tool '{toolName}' called at least once",
                    actual: calledTools.Count > 0 
                        ? $"Tools called: [{string.Join(", ", calledTools)}]"
                        : "No tools were called",
                    suggestions: suggestions.Count > 0 ? suggestions : null,
                    because: because));
        }
        
        var call = _report.GetCallsByName(toolName).First();
        return new ToolCallAssertion(this, _report, call, toolName);
    }
    
    /// <summary>Assert that a specific tool was NOT called.</summary>
    /// <param name="toolName">The name of the tool expected NOT to be called.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolUsageAssertions NotHaveCalledTool(string toolName, string? because = null)
    {
        if (_report.WasToolCalled(toolName))
        {
            var callCount = _report.GetCallsByName(toolName).Count();
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected tool '{toolName}' NOT to be called, but it was called {callCount} time(s).",
                    toolName: toolName,
                    calledTools: _report.UniqueToolNames.ToList(),
                    expected: $"Tool '{toolName}' not called",
                    actual: $"Tool '{toolName}' called {callCount} time(s)",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert exact number of tool calls.</summary>
    /// <param name="expectedCount">The expected total number of tool calls.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolUsageAssertions HaveCallCount(int expectedCount, string? because = null)
    {
        if (_report.Count != expectedCount)
        {
            var timeline = BuildTimeline();
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected {expectedCount} tool call(s), but {_report.Count} call(s) were made.",
                    calledTools: _report.UniqueToolNames.ToList(),
                    expected: $"{expectedCount} tool call(s)",
                    actual: $"{_report.Count} tool call(s)",
                    context: timeline,
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert at least N tool calls.</summary>
    /// <param name="minCount">The minimum number of tool calls expected.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolUsageAssertions HaveCallCountAtLeast(int minCount, string? because = null)
    {
        if (_report.Count < minCount)
        {
            var timeline = BuildTimeline();
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected at least {minCount} tool call(s), but only {_report.Count} call(s) were made.",
                    calledTools: _report.UniqueToolNames.ToList(),
                    expected: $"At least {minCount} tool call(s)",
                    actual: $"{_report.Count} tool call(s)",
                    context: timeline,
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert no tool calls resulted in errors.</summary>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolUsageAssertions HaveNoErrors(string? because = null)
    {
        var errors = _report.Calls.Where(c => c.HasError).ToList();
        if (errors.Count > 0)
        {
            var errorDetails = string.Join("\n", errors.Select(e => 
                $"• {e.Name}: {e.Exception?.Message ?? "(unknown error)"}"));
            
            var suggestions = new List<string>
            {
                "Check tool implementations for error handling",
                "Verify input arguments are valid"
            };
            
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected no tool errors, but {errors.Count} error(s) occurred.",
                    calledTools: errors.Select(e => e.Name).ToList(),
                    expected: "No tool errors",
                    actual: $"{errors.Count} tool error(s)",
                    context: errorDetails,
                    suggestions: suggestions,
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert that tools were called in a specific order.</summary>
    /// <param name="expectedOrder">The expected order of tool names.</param>
    [StackTraceHidden]
    public ToolUsageAssertions HaveCallOrder(params string[] expectedOrder)
    {
        for (int i = 0; i < expectedOrder.Length; i++)
        {
            var expectedTool = expectedOrder[i];
            var actualOrder = _report.GetToolOrder(expectedTool);
            
            if (actualOrder == 0)
            {
                var timeline = BuildTimeline();
                AgentEvalScope.FailWith(
                    ToolAssertionException.Create(
                        $"Expected tool '{expectedTool}' at position {i + 1}, but it was never called.",
                        toolName: expectedTool,
                        calledTools: _report.UniqueToolNames.ToList(),
                        expected: $"Tool order: [{string.Join(" → ", expectedOrder)}]",
                        actual: $"Tool '{expectedTool}' not found in call sequence",
                        context: timeline));
            }
            
            if (i > 0)
            {
                var previousTool = expectedOrder[i - 1];
                var previousOrder = _report.GetToolOrder(previousTool);
                
                if (actualOrder <= previousOrder)
                {
                    var timeline = BuildTimeline();
                    AgentEvalScope.FailWith(
                        ToolAssertionException.Create(
                            $"Expected '{expectedTool}' to be called after '{previousTool}', but order was reversed.",
                            calledTools: _report.UniqueToolNames.ToList(),
                            expected: $"'{previousTool}' (#{previousOrder}) → '{expectedTool}' (after #{previousOrder})",
                            actual: $"'{previousTool}' (#{previousOrder}) → '{expectedTool}' (#{actualOrder})",
                            context: timeline));
                }
            }
        }
        return this;
    }
    
    /// <summary>Assert that at least one tool was called.</summary>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolUsageAssertions HaveCalledAnyTool(string? because = null)
    {
        if (_report.Count == 0)
        {
            var suggestions = new List<string>
            {
                "Verify the agent has access to tools",
                "Check if the prompt encourages tool usage",
                "Ensure tools are properly registered with the agent"
            };
            
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    "Expected at least one tool to be called, but no tools were called.",
                    calledTools: Array.Empty<string>(),
                    expected: "At least 1 tool call",
                    actual: "0 tool calls",
                    suggestions: suggestions,
                    because: because));
        }
        return this;
    }
    
    /// <summary>Get the underlying report for custom assertions.</summary>
    public ToolUsageReport Report => _report;
    
    private string BuildTimeline()
    {
        if (_report.Count == 0) return "No tools were called.";
        
        var lines = new List<string> { "Tool call timeline:" };
        foreach (var call in _report.Calls.OrderBy(c => c.Order))
        {
            var status = call.HasError ? "✗" : "✓";
            var duration = call.HasTiming ? $" ({call.Duration?.TotalMilliseconds:F0}ms)" : "";
            lines.Add($"  {call.Order}. [{status}] {call.Name}{duration}");
        }
        return string.Join("\n", lines);
    }
}

/// <summary>
/// Fluent assertions for a specific tool call.
/// </summary>
public class ToolCallAssertion
{
    private readonly ToolUsageAssertions _parent;
    private readonly ToolUsageReport _report;
    private readonly ToolCallRecord _call;
    private readonly string _toolName;
    
    internal ToolCallAssertion(ToolUsageAssertions parent, ToolUsageReport report, ToolCallRecord call, string toolName)
    {
        _parent = parent;
        _report = report;
        _call = call;
        _toolName = toolName;
    }
    
    /// <summary>Assert this tool was called before another tool.</summary>
    /// <param name="otherToolName">The tool that should have been called after.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion BeforeTool(string otherToolName, string? because = null)
    {
        var otherOrder = _report.GetToolOrder(otherToolName);
        if (otherOrder == 0)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' to be called before '{otherToolName}', but '{otherToolName}' was never called.",
                    toolName: otherToolName,
                    calledTools: _report.UniqueToolNames.ToList(),
                    expected: $"'{_toolName}' → '{otherToolName}'",
                    actual: $"'{otherToolName}' not called",
                    because: because));
        }
        
        if (_call.Order >= otherOrder)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' to be called before '{otherToolName}'.",
                    calledTools: _report.UniqueToolNames.ToList(),
                    expected: $"'{_toolName}' (#{_call.Order}) before '{otherToolName}'",
                    actual: $"'{_toolName}' (#{_call.Order}) after '{otherToolName}' (#{otherOrder})",
                    context: BuildTimeline(),
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert this tool was called after another tool.</summary>
    /// <param name="otherToolName">The tool that should have been called before.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion AfterTool(string otherToolName, string? because = null)
    {
        var otherOrder = _report.GetToolOrder(otherToolName);
        if (otherOrder == 0)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' to be called after '{otherToolName}', but '{otherToolName}' was never called.",
                    toolName: otherToolName,
                    calledTools: _report.UniqueToolNames.ToList(),
                    expected: $"'{otherToolName}' → '{_toolName}'",
                    actual: $"'{otherToolName}' not called",
                    because: because));
        }
        
        if (_call.Order <= otherOrder)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' to be called after '{otherToolName}'.",
                    calledTools: _report.UniqueToolNames.ToList(),
                    expected: $"'{otherToolName}' (#{otherOrder}) before '{_toolName}'",
                    actual: $"'{_toolName}' (#{_call.Order}) before '{otherToolName}' (#{otherOrder})",
                    context: BuildTimeline(),
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert a specific argument value (equality).</summary>
    /// <param name="paramName">The parameter name to check.</param>
    /// <param name="expectedValue">The expected value.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion WithArgument(string paramName, object expectedValue, string? because = null)
    {
        object? actualValue = null;
        var hasArgument = _call.Arguments?.TryGetValue(paramName, out actualValue) ?? false;
        
        if (!hasArgument)
        {
            var available = _call.Arguments?.Keys.Any() == true 
                ? string.Join(", ", _call.Arguments.Keys) 
                : "(none)";
            
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' to have argument '{paramName}', but it was not found.",
                    toolName: _toolName,
                    expected: $"Argument '{paramName}' present",
                    actual: $"Available arguments: [{available}]",
                    suggestions: new[] { $"Check the argument name spelling", $"Available: {available}" },
                    because: because));
            return this; // Return if in scope mode
        }
        
        var actualStr = actualValue is JsonElement je ? je.GetRawText().Trim('"') : actualValue?.ToString();
        var expectedStr = expectedValue?.ToString();
        
        if (!string.Equals(actualStr, expectedStr, StringComparison.Ordinal))
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' argument '{paramName}' to have a different value.",
                    toolName: _toolName,
                    expected: $"'{paramName}' = \"{expectedValue}\"",
                    actual: $"'{paramName}' = \"{actualValue}\"",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert an argument contains a substring (case-insensitive).</summary>
    /// <param name="paramName">The parameter name to check.</param>
    /// <param name="substring">The substring that should be contained.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion WithArgumentContaining(string paramName, string substring, string? because = null)
    {
        object? actualValue = null;
        var hasArgument = _call.Arguments?.TryGetValue(paramName, out actualValue) ?? false;
        
        if (!hasArgument)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' to have argument '{paramName}' containing '{substring}', but argument was not found.",
                    toolName: _toolName,
                    expected: $"Argument '{paramName}' containing \"{substring}\"",
                    actual: "Argument not found",
                    because: because));
            return this; // Return if in scope mode
        }
        
        var actualStr = actualValue is JsonElement je ? je.GetString() : actualValue?.ToString();
        
        if (actualStr == null || !actualStr.Contains(substring, StringComparison.OrdinalIgnoreCase))
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' argument '{paramName}' to contain '{substring}'.",
                    toolName: _toolName,
                    expected: $"'{paramName}' containing \"{substring}\"",
                    actual: $"'{paramName}' = \"{Truncate(actualStr ?? "(null)", 100)}\"",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert the tool result contains a substring (case-insensitive).</summary>
    /// <param name="substring">The substring that should be in the result.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion WithResultContaining(string substring, string? because = null)
    {
        var resultStr = _call.Result?.ToString();
        
        if (resultStr == null || !resultStr.Contains(substring, StringComparison.OrdinalIgnoreCase))
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' result to contain '{substring}'.",
                    toolName: _toolName,
                    expected: $"Result containing \"{substring}\"",
                    actual: $"Result: \"{Truncate(resultStr ?? "(null)", 100)}\"",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert the tool completed without error.</summary>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion WithoutError(string? because = null)
    {
        if (_call.HasError)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' to complete without error.",
                    toolName: _toolName,
                    expected: "Successful completion",
                    actual: $"Error: {_call.Exception?.Message ?? "(unknown)"}",
                    context: _call.Exception?.StackTrace,
                    suggestions: new[] { "Check tool implementation", "Verify input arguments" },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert tool duration is under a maximum.</summary>
    /// <param name="max">The maximum allowed duration.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion WithDurationUnder(TimeSpan max, string? because = null)
    {
        if (!_call.HasTiming)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Cannot assert duration for '{_toolName}' - timing information not available.",
                    toolName: _toolName,
                    suggestions: new[] { "Enable streaming to capture timing", "Use AgentEvalBuilder.WithTimingCapture()" },
                    because: because));
        }
        
        if (_call.Duration > max)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' duration under {max.TotalMilliseconds:F0}ms.",
                    toolName: _toolName,
                    expected: $"Duration < {max.TotalMilliseconds:F0}ms",
                    actual: $"Duration = {_call.Duration!.Value.TotalMilliseconds:F0}ms",
                    suggestions: new[] { "Consider optimizing the tool implementation", "Check for slow I/O operations" },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert this tool was called exactly N times total.</summary>
    /// <param name="expectedCount">The expected number of times the tool was called.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public ToolCallAssertion Times(int expectedCount, string? because = null)
    {
        var actualCount = _report.GetCallsByName(_toolName).Count();
        if (actualCount != expectedCount)
        {
            AgentEvalScope.FailWith(
                ToolAssertionException.Create(
                    $"Expected '{_toolName}' to be called {expectedCount} time(s), but was called {actualCount} time(s).",
                    toolName: _toolName,
                    expected: $"{expectedCount} call(s)",
                    actual: $"{actualCount} call(s)",
                    context: BuildTimeline(),
                    because: because));
        }
        return this;
    }
    
    /// <summary>Return to parent assertions for chaining.</summary>
    public ToolUsageAssertions And() => _parent;
    
    private string BuildTimeline()
    {
        var lines = new List<string> { "Tool call timeline:" };
        foreach (var call in _report.Calls.OrderBy(c => c.Order))
        {
            var marker = call.Name == _toolName ? "→" : " ";
            var status = call.HasError ? "✗" : "✓";
            var duration = call.HasTiming ? $" ({call.Duration?.TotalMilliseconds:F0}ms)" : "";
            lines.Add($"  {marker} {call.Order}. [{status}] {call.Name}{duration}");
        }
        return string.Join("\n", lines);
    }
    
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}
