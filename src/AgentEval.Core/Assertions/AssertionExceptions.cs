// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace AgentEval.Assertions;

/// <summary>
/// Base exception for all AgentEval assertion failures.
/// Provides rich context for test failures including expected/actual values,
/// suggestions, and formatted output similar to FluentAssertions.
/// </summary>
public class AgentEvalAssertionException : Exception
{
    /// <summary>The expected value or condition.</summary>
    public string? Expected { get; init; }
    
    /// <summary>The actual value or condition encountered.</summary>
    public string? Actual { get; init; }
    
    /// <summary>Additional context about the failure.</summary>
    public string? Context { get; init; }
    
    /// <summary>Suggestions for fixing the issue.</summary>
    public IReadOnlyList<string>? Suggestions { get; init; }
    
    /// <summary>The name of the subject being asserted (e.g., variable name).</summary>
    public string? SubjectName { get; init; }
    
    /// <summary>The reason provided via "because" parameter.</summary>
    public string? Because { get; init; }
    
    public AgentEvalAssertionException(string message) : base(message) { }
    
    public AgentEvalAssertionException(string message, Exception inner) : base(message, inner) { }
    
    /// <summary>
    /// Creates an exception with full structured information.
    /// </summary>
    public static AgentEvalAssertionException Create(
        string message,
        string? expected = null,
        string? actual = null,
        string? context = null,
        IReadOnlyList<string>? suggestions = null,
        string? subjectName = null,
        string? because = null)
    {
        var formattedMessage = FormatMessage(message, expected, actual, context, suggestions, subjectName, because);
        
        return new AgentEvalAssertionException(formattedMessage)
        {
            Expected = expected,
            Actual = actual,
            Context = context,
            Suggestions = suggestions,
            SubjectName = subjectName,
            Because = because
        };
    }
    
    private static string FormatMessage(
        string message,
        string? expected,
        string? actual,
        string? context,
        IReadOnlyList<string>? suggestions,
        string? subjectName,
        string? because)
    {
        var sb = new StringBuilder();
        
        // Main message with optional "because" reason
        sb.Append(message);
        if (!string.IsNullOrWhiteSpace(because))
        {
            sb.Append($" because {because}");
        }
        sb.AppendLine();
        
        // Subject identification
        if (!string.IsNullOrWhiteSpace(subjectName))
        {
            sb.AppendLine();
            sb.AppendLine($"Subject: {subjectName}");
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
/// Exception thrown when a tool usage assertion fails.
/// </summary>
public class ToolAssertionException : AgentEvalAssertionException
{
    /// <summary>The tool that was expected or involved in the assertion.</summary>
    public string? ToolName { get; init; }
    
    /// <summary>The list of tools that were actually called.</summary>
    public IReadOnlyList<string>? CalledTools { get; init; }
    
    public ToolAssertionException(string message) : base(message) { }
    
    /// <summary>
    /// Creates a tool assertion exception with full context.
    /// </summary>
    public static ToolAssertionException Create(
        string message,
        string? toolName = null,
        IReadOnlyList<string>? calledTools = null,
        string? expected = null,
        string? actual = null,
        string? context = null,
        IReadOnlyList<string>? suggestions = null,
        string? because = null)
    {
        var formattedMessage = FormatToolMessage(message, toolName, calledTools, expected, actual, context, suggestions, because);
        
        return new ToolAssertionException(formattedMessage)
        {
            ToolName = toolName,
            CalledTools = calledTools,
            Expected = expected,
            Actual = actual,
            Context = context,
            Suggestions = suggestions,
            Because = because
        };
    }
    
    private static string FormatToolMessage(
        string message,
        string? toolName,
        IReadOnlyList<string>? calledTools,
        string? expected,
        string? actual,
        string? context,
        IReadOnlyList<string>? suggestions,
        string? because)
    {
        var sb = new StringBuilder();
        
        // Main message with optional "because" reason
        sb.Append(message);
        if (!string.IsNullOrWhiteSpace(because))
        {
            sb.Append($" because {because}");
        }
        sb.AppendLine();
        
        // Tool identification
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            sb.AppendLine();
            sb.AppendLine($"Tool: {toolName}");
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
        
        // Called tools list
        if (calledTools?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Tools called:");
            foreach (var tool in calledTools)
            {
                sb.AppendLine($"  • {tool}");
            }
        }
        else if (calledTools != null)
        {
            sb.AppendLine();
            sb.AppendLine("Tools called: (none)");
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
/// Exception thrown when a performance assertion fails.
/// </summary>
public class PerformanceAssertionException : AgentEvalAssertionException
{
    /// <summary>The metric that failed (e.g., "TotalDuration", "TokenCount").</summary>
    public string? MetricName { get; init; }
    
    /// <summary>The threshold that was exceeded or not met.</summary>
    public string? Threshold { get; init; }
    
    /// <summary>The actual measured value.</summary>
    public string? MeasuredValue { get; init; }
    
    public PerformanceAssertionException(string message) : base(message) { }
    
    /// <summary>
    /// Creates a performance assertion exception with full context.
    /// </summary>
    public static PerformanceAssertionException Create(
        string message,
        string? metricName = null,
        string? threshold = null,
        string? measuredValue = null,
        string? context = null,
        IReadOnlyList<string>? suggestions = null,
        string? because = null)
    {
        var formattedMessage = FormatPerformanceMessage(message, metricName, threshold, measuredValue, context, suggestions, because);
        
        return new PerformanceAssertionException(formattedMessage)
        {
            MetricName = metricName,
            Threshold = threshold,
            MeasuredValue = measuredValue,
            Expected = threshold,
            Actual = measuredValue,
            Context = context,
            Suggestions = suggestions,
            Because = because
        };
    }
    
    private static string FormatPerformanceMessage(
        string message,
        string? metricName,
        string? threshold,
        string? measuredValue,
        string? context,
        IReadOnlyList<string>? suggestions,
        string? because)
    {
        var sb = new StringBuilder();
        
        // Main message with optional "because" reason
        sb.Append(message);
        if (!string.IsNullOrWhiteSpace(because))
        {
            sb.Append($" because {because}");
        }
        sb.AppendLine();
        
        // Metric identification
        if (!string.IsNullOrWhiteSpace(metricName))
        {
            sb.AppendLine();
            sb.AppendLine($"Metric: {metricName}");
        }
        
        // Threshold vs Measured
        if (threshold != null || measuredValue != null)
        {
            sb.AppendLine();
            if (threshold != null)
                sb.AppendLine($"Threshold: {threshold}");
            if (measuredValue != null)
                sb.AppendLine($"Measured:  {measuredValue}");
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
/// Exception thrown when a response assertion fails.
/// </summary>
public class ResponseAssertionException : AgentEvalAssertionException
{
    /// <summary>A preview of the response text (truncated for display).</summary>
    public string? ResponsePreview { get; init; }
    
    public ResponseAssertionException(string message) : base(message) { }
    
    /// <summary>
    /// Creates a response assertion exception with full context.
    /// </summary>
    public static ResponseAssertionException Create(
        string message,
        string? responsePreview = null,
        string? expected = null,
        string? actual = null,
        string? context = null,
        IReadOnlyList<string>? suggestions = null,
        string? because = null)
    {
        var formattedMessage = FormatResponseMessage(message, responsePreview, expected, actual, context, suggestions, because);
        
        return new ResponseAssertionException(formattedMessage)
        {
            ResponsePreview = responsePreview,
            Expected = expected,
            Actual = actual,
            Context = context,
            Suggestions = suggestions,
            Because = because
        };
    }
    
    private static string FormatResponseMessage(
        string message,
        string? responsePreview,
        string? expected,
        string? actual,
        string? context,
        IReadOnlyList<string>? suggestions,
        string? because)
    {
        var sb = new StringBuilder();
        
        // Main message with optional "because" reason
        sb.Append(message);
        if (!string.IsNullOrWhiteSpace(because))
        {
            sb.Append($" because {because}");
        }
        sb.AppendLine();
        
        // Expected vs Actual
        if (expected != null || actual != null)
        {
            sb.AppendLine();
            if (expected != null)
                sb.AppendLine($"Expected: {expected}");
            if (actual != null)
                sb.AppendLine($"Actual:   {actual}");
        }
        
        // Response preview
        if (!string.IsNullOrWhiteSpace(responsePreview))
        {
            sb.AppendLine();
            sb.AppendLine("Response preview:");
            sb.AppendLine($"  \"{responsePreview}\"");
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
/// Exception thrown when multiple assertions fail within an AgentEvalScope.
/// </summary>
public class AgentEvalScopeException : AgentEvalAssertionException
{
    /// <summary>All failures collected within the scope.</summary>
    public IReadOnlyList<AgentEvalAssertionException> Failures { get; }
    
    public AgentEvalScopeException(IReadOnlyList<AgentEvalAssertionException> failures)
        : base(FormatScopeMessage(failures))
    {
        Failures = failures;
    }
    
    private static string FormatScopeMessage(IReadOnlyList<AgentEvalAssertionException> failures)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Multiple assertion failures occurred ({failures.Count} total):");
        sb.AppendLine(new string('─', 60));
        
        for (int i = 0; i < failures.Count; i++)
        {
            sb.AppendLine();
            sb.AppendLine($"Failure {i + 1}:");
            foreach (var line in failures[i].Message.Split('\n'))
            {
                sb.AppendLine($"  {line}");
            }
            
            if (i < failures.Count - 1)
            {
                sb.AppendLine();
                sb.AppendLine(new string('─', 40));
            }
        }
        
        return sb.ToString().TrimEnd();
    }
}
