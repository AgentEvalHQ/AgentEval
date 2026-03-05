// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using AgentEval.Models;

namespace AgentEval.Assertions;

/// <summary>
/// Fluent assertions for PerformanceMetrics.
/// </summary>
public class PerformanceAssertions
{
    private readonly PerformanceMetrics _metrics;
    private readonly string? _subjectName;
    
    public PerformanceAssertions(PerformanceMetrics metrics, string? subjectName = null)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _subjectName = subjectName;
    }
    
    /// <summary>Assert total duration is under a maximum.</summary>
    /// <param name="max">Maximum allowed duration.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public PerformanceAssertions HaveTotalDurationUnder(TimeSpan max, string? because = null)
    {
        if (_metrics.TotalDuration > max)
        {
            var overage = _metrics.TotalDuration - max;
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected total duration to be under the specified maximum.",
                    metricName: "TotalDuration",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"< {max.TotalMilliseconds:F0}ms"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"{_metrics.TotalDuration.TotalMilliseconds:F0}ms (exceeded by {overage.TotalMilliseconds:F0}ms)"),
                    suggestions: new[] 
                    { 
                        "Consider optimizing slow tool operations",
                        "Check for unnecessary API calls",
                        "Review prompt complexity"
                    },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert total duration is at least a minimum.</summary>
    /// <param name="min">Minimum expected duration.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public PerformanceAssertions HaveTotalDurationAtLeast(TimeSpan min, string? because = null)
    {
        if (_metrics.TotalDuration < min)
        {
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected total duration to be at least the specified minimum.",
                    metricName: "TotalDuration",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"≥ {min.TotalMilliseconds:F0}ms"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"{_metrics.TotalDuration.TotalMilliseconds:F0}ms"),
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert time to first token is under a maximum (streaming only).</summary>
    /// <param name="max">Maximum allowed TTFT.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    /// <remarks>
    /// If TTFT is not available (non-streaming mode), this assertion is skipped
    /// and a debug message is logged. Use streaming mode to capture TTFT.
    /// </remarks>
    [StackTraceHidden]
    public PerformanceAssertions HaveTimeToFirstTokenUnder(TimeSpan max, string? because = null)
    {
        if (!_metrics.TimeToFirstToken.HasValue)
        {
            // Skip gracefully - TTFT requires streaming mode
            System.Diagnostics.Debug.WriteLine(
                "[AgentEval] Skipping TTFT assertion - Time to First Token not available. " +
                "Enable streaming mode to capture TTFT.");
            return this;
        }
        
        if (_metrics.TimeToFirstToken!.Value > max)
        {
            var overage = _metrics.TimeToFirstToken.Value - max;
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected time to first token (TTFT) to be under the specified maximum.",
                    metricName: "TimeToFirstToken",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"< {max.TotalMilliseconds:F0}ms"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"{_metrics.TimeToFirstToken.Value.TotalMilliseconds:F0}ms (exceeded by {overage.TotalMilliseconds:F0}ms)"),
                    suggestions: new[] 
                    { 
                        "TTFT is affected by model load time and prompt processing",
                        "Consider using a faster model for latency-sensitive scenarios"
                    },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert total token count is under a maximum.</summary>
    /// <param name="max">Maximum allowed total tokens.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public PerformanceAssertions HaveTokenCountUnder(int max, string? because = null)
    {
        if (_metrics.TotalTokens > max)
        {
            var breakdown = $"Prompt: {_metrics.PromptTokens}, Completion: {_metrics.CompletionTokens}";
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected total token count to be under the specified maximum.",
                    metricName: "TotalTokens",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"< {max:N0}"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"{_metrics.TotalTokens:N0}"),
                    context: breakdown,
                    suggestions: new[] 
                    { 
                        "Reduce prompt length",
                        "Use more concise system instructions",
                        "Consider summarizing context"
                    },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert prompt tokens under a maximum.</summary>
    /// <param name="max">Maximum allowed prompt tokens.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public PerformanceAssertions HavePromptTokensUnder(int max, string? because = null)
    {
        if (_metrics.PromptTokens > max)
        {
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected prompt tokens to be under the specified maximum.",
                    metricName: "PromptTokens",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"< {max:N0}"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"{_metrics.PromptTokens:N0}"),
                    suggestions: new[] 
                    { 
                        "Shorten system prompt",
                        "Reduce few-shot examples",
                        "Trim conversation history"
                    },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert completion tokens under a maximum.</summary>
    /// <param name="max">Maximum allowed completion tokens.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public PerformanceAssertions HaveCompletionTokensUnder(int max, string? because = null)
    {
        if (_metrics.CompletionTokens > max)
        {
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected completion tokens to be under the specified maximum.",
                    metricName: "CompletionTokens",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"< {max:N0}"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"{_metrics.CompletionTokens:N0}"),
                    suggestions: new[] 
                    { 
                        "Use max_tokens parameter to limit response length",
                        "Adjust prompt to request concise responses"
                    },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert estimated cost is under a maximum (USD).</summary>
    /// <param name="maxUsd">Maximum allowed cost in USD.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    /// <remarks>
    /// If cost is not available (model pricing not configured or tokens not captured),
    /// this assertion is skipped and a debug message is logged.
    /// </remarks>
    [StackTraceHidden]
    public PerformanceAssertions HaveEstimatedCostUnder(decimal maxUsd, string? because = null)
    {
        if (!_metrics.EstimatedCost.HasValue)
        {
            // Skip gracefully - cost requires model pricing and token counting
            System.Diagnostics.Debug.WriteLine(
                "[AgentEval] Skipping cost assertion - estimated cost not available. " +
                "Ensure model pricing is configured and token counting is enabled.");
            return this;
        }
        
        if (_metrics.EstimatedCost!.Value > maxUsd)
        {
            var overage = _metrics.EstimatedCost.Value - maxUsd;
            var breakdown = string.Create(CultureInfo.InvariantCulture, $"Prompt: {_metrics.PromptTokens:N0} tokens, Completion: {_metrics.CompletionTokens:N0} tokens");
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected estimated cost to be under the specified maximum.",
                    metricName: "EstimatedCost",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"< ${maxUsd:F4}"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"${_metrics.EstimatedCost.Value:F4} (exceeded by ${overage:F4})"),
                    context: breakdown,
                    suggestions: new[] 
                    { 
                        "Use a cheaper model",
                        "Reduce token usage",
                        "Consider caching repeated queries"
                    },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert average tool time is under a maximum.</summary>
    /// <param name="max">Maximum allowed average tool execution time.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    /// <remarks>
    /// If tool timing is not available (no tools called or non-streaming mode),
    /// this assertion is skipped and a debug message is logged.
    /// </remarks>
    [StackTraceHidden]
    public PerformanceAssertions HaveAverageToolTimeUnder(TimeSpan max, string? because = null)
    {
        if (_metrics.ToolCallCount == 0 || _metrics.TotalToolTime == TimeSpan.Zero)
        {
            // Skip gracefully - tool timing requires streaming mode or no tools called
            System.Diagnostics.Debug.WriteLine(
                "[AgentEval] Skipping average tool time assertion - tool timing not available. " +
                "Enable streaming mode to capture tool timing.");
            return this;
        }
        
        if (_metrics.AverageToolTime > max)
        {
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected average tool execution time to be under the specified maximum.",
                    metricName: "AverageToolTime",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"< {max.TotalMilliseconds:F0}ms"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"{_metrics.AverageToolTime.TotalMilliseconds:F0}ms"),
                    context: string.Create(CultureInfo.InvariantCulture, $"Total tool time: {_metrics.TotalToolTime.TotalMilliseconds:F0}ms across {_metrics.ToolCallCount} call(s)"),
                    suggestions: new[] 
                    { 
                        "Optimize individual tool implementations",
                        "Check for slow I/O operations",
                        "Consider parallel tool execution"
                    },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert total tool time is under a maximum.</summary>
    /// <param name="max">Maximum allowed total tool execution time.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    /// <remarks>
    /// If tool timing is not available (no tools called or non-streaming mode),
    /// this assertion is skipped and a debug message is logged.
    /// </remarks>
    [StackTraceHidden]
    public PerformanceAssertions HaveTotalToolTimeUnder(TimeSpan max, string? because = null)
    {
        if (_metrics.ToolCallCount == 0 || _metrics.TotalToolTime == TimeSpan.Zero)
        {
            // Skip gracefully - tool timing requires streaming mode or no tools called
            System.Diagnostics.Debug.WriteLine(
                "[AgentEval] Skipping total tool time assertion - tool timing not available. " +
                "Enable streaming mode to capture tool timing.");
            return this;
        }
        
        if (_metrics.TotalToolTime > max)
        {
            var overage = _metrics.TotalToolTime - max;
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected total tool execution time to be under the specified maximum.",
                    metricName: "TotalToolTime",
                    threshold: string.Create(CultureInfo.InvariantCulture, $"< {max.TotalMilliseconds:F0}ms"),
                    measuredValue: string.Create(CultureInfo.InvariantCulture, $"{_metrics.TotalToolTime.TotalMilliseconds:F0}ms (exceeded by {overage.TotalMilliseconds:F0}ms)"),
                    context: string.Create(CultureInfo.InvariantCulture, $"{_metrics.ToolCallCount} tool call(s), average: {_metrics.AverageToolTime.TotalMilliseconds:F0}ms"),
                    suggestions: new[] 
                    { 
                        "Reduce number of tool calls",
                        "Optimize slow tools",
                        "Consider caching tool results"
                    },
                    because: because));
        }
        return this;
    }
    
    /// <summary>Assert tool call count is exactly N.</summary>
    /// <param name="expected">The expected number of tool calls.</param>
    /// <param name="because">Optional reason for the assertion (shown in failure message).</param>
    [StackTraceHidden]
    public PerformanceAssertions HaveToolCallCount(int expected, string? because = null)
    {
        if (_metrics.ToolCallCount != expected)
        {
            AgentEvalScope.FailWith(
                PerformanceAssertionException.Create(
                    "Expected the specified number of tool calls.",
                    metricName: "ToolCallCount",
                    threshold: $"= {expected}",
                    measuredValue: $"{_metrics.ToolCallCount}",
                    because: because));
        }
        return this;
    }
    
    /// <summary>Get the underlying metrics for custom assertions.</summary>
    public PerformanceMetrics Metrics => _metrics;
}
