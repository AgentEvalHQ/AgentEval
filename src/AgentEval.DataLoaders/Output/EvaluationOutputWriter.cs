// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentEval.Core;
using AgentEval.Models;

namespace AgentEval.Output;

/// <summary>
/// Writes evaluation results to console with formatting based on verbosity level.
/// </summary>
public class EvaluationOutputWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly VerbositySettings _settings;
    private readonly TextWriter _output;

    /// <summary>
    /// Creates a new EvaluationOutputWriter with default settings writing to Console.Out.
    /// </summary>
    public EvaluationOutputWriter() : this(new VerbositySettings(), Console.Out)
    {
    }

    /// <summary>
    /// Creates a new EvaluationOutputWriter with specified settings writing to Console.Out.
    /// </summary>
    /// <param name="settings">Verbosity settings.</param>
    public EvaluationOutputWriter(VerbositySettings settings) : this(settings, Console.Out)
    {
    }

    /// <summary>
    /// Creates a new EvaluationOutputWriter with specified settings and output destination.
    /// </summary>
    /// <param name="settings">Verbosity settings.</param>
    /// <param name="output">Where to write output.</param>
    public EvaluationOutputWriter(VerbositySettings settings, TextWriter output)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>
    /// Writes an evaluation result based on the configured verbosity level.
    /// </summary>
    /// <param name="result">The evaluation result to output.</param>
    public void WriteTestResult(TestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        switch (_settings.Level)
        {
            case VerbosityLevel.None:
                WriteNone(result);
                break;
            case VerbosityLevel.Summary:
                WriteSummary(result);
                break;
            case VerbosityLevel.Detailed:
                WriteDetailed(result);
                break;
            case VerbosityLevel.Full:
                WriteFull(result);
                break;
        }
    }

    /// <summary>
    /// Writes only pass/fail status.
    /// </summary>
    private void WriteNone(TestResult result)
    {
        var status = result.Passed ? "✓ PASS" : "✗ FAIL";
        _output.WriteLine($"{status}: {result.TestName}");
    }

    /// <summary>
    /// Writes summary with basic metrics.
    /// </summary>
    private void WriteSummary(TestResult result)
    {
        var status = result.Passed ? "✓ PASS" : "✗ FAIL";
        _output.WriteLine($"{status}: {result.TestName}");
        _output.WriteLine($"  Duration: {result.Performance?.TotalDuration.TotalMilliseconds:F0}ms");

        if (result.ToolUsage is not null)
        {
            var errorCount = result.ToolUsage.Calls.Count(c => c.HasError);
            var successCount = result.ToolUsage.Count - errorCount;
            _output.WriteLine($"  Tool calls: {result.ToolUsage.Count} ({successCount} succeeded)");
        }

        if (result.Performance?.TotalTokens > 0)
        {
            _output.WriteLine($"  Tokens: {result.Performance.TotalTokens}");
        }

        _output.WriteLine();
    }

    /// <summary>
    /// Writes detailed output including tool timeline and performance breakdown.
    /// </summary>
    private void WriteDetailed(TestResult result)
    {
        var status = result.Passed ? "✓ PASS" : "✗ FAIL";
        var border = new string('─', 60);

        _output.WriteLine(border);
        _output.WriteLine($"{status}: {result.TestName}");
        _output.WriteLine(border);

        // Performance section
        if (_settings.IncludePerformanceMetrics && result.Performance is not null)
        {
            WritePerformanceSection(result.Performance);
        }

        // Tool usage section
        if (result.ToolUsage is not null)
        {
            WriteToolUsageSection(result.ToolUsage);
        }

        // Error section
        if (result.HasError && result.Error is not null)
        {
            _output.WriteLine();
            _output.WriteLine("📛 Error:");
            _output.WriteLine($"   {result.Error.Message}");
        }

        // Failure section
        if (result.Failure is not null)
        {
            _output.WriteLine();
            _output.WriteLine("📛 Failure:");
            _output.WriteLine($"   {result.Failure.WhyItFailed}");
            if (result.Failure.Suggestions.Count > 0)
            {
                _output.WriteLine("   Suggestions:");
                foreach (var suggestion in result.Failure.Suggestions)
                {
                    _output.WriteLine($"     • {suggestion.Title}");
                }
            }
        }

        // Metrics section
        if (result.MetricResults?.Count > 0)
        {
            WriteMetricsSection(result.MetricResults);
        }

        _output.WriteLine(border);
        _output.WriteLine();
    }

    /// <summary>
    /// Writes full output including JSON trace for time-travel debugging.
    /// </summary>
    private void WriteFull(TestResult result)
    {
        // First write detailed output
        WriteDetailed(result);

        // Add full JSON trace
        _output.WriteLine("📋 Full JSON Trace:");
        _output.WriteLine("```json");
        
        var traceData = BuildTraceData(result);
        _output.WriteLine(JsonSerializer.Serialize(traceData, s_jsonOptions));
        
        _output.WriteLine("```");
        _output.WriteLine();
    }

    /// <summary>
    /// Writes the performance metrics section.
    /// </summary>
    private void WritePerformanceSection(PerformanceMetrics performance)
    {
        _output.WriteLine();
        _output.WriteLine("⏱️  Performance:");
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   Total Duration: {performance.TotalDuration.TotalMilliseconds:F0}ms"));

        if (performance.TimeToFirstToken.HasValue)
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   Time to First Token: {performance.TimeToFirstToken.Value.TotalMilliseconds:F0}ms"));
        }

        if (performance.TotalTokens > 0)
        {
            _output.WriteLine($"   Tokens: {performance.PromptTokens ?? 0} in / {performance.CompletionTokens ?? 0} out = {performance.TotalTokens} total");
        }

        if (performance.EstimatedCost > 0)
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"   Est. Cost: ${performance.EstimatedCost:F4}"));
        }
    }

    /// <summary>
    /// Writes the tool usage section with timeline.
    /// </summary>
    private void WriteToolUsageSection(ToolUsageReport toolUsage)
    {
        _output.WriteLine();
        _output.WriteLine($"🔧 Tools ({toolUsage.Count} calls):");

        if (toolUsage.Count == 0)
        {
            _output.WriteLine("   (no tool calls recorded)");
            return;
        }

        // Sort by order/time
        var sortedCalls = toolUsage.Calls
            .OrderBy(t => t.Order)
            .ThenBy(t => t.StartTime);

        foreach (var call in sortedCalls)
        {
            var statusIcon = !call.HasError ? "✓" : "✗";
            var duration = call.Duration?.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + "ms" ?? "?ms";
            
            var line = new StringBuilder();
            line.Append($"   {statusIcon} [{call.Order}] {call.Name} ({duration})");

            if (_settings.IncludeToolArguments && call.Arguments?.Count > 0)
            {
                var args = Truncate(call.GetArgumentsAsJson(), 100);
                line.Append($"\n      Args: {args}");
            }

            if (_settings.IncludeToolResults && call.Result is not null)
            {
                var resultText = Truncate(call.Result.ToString(), 100);
                line.Append($"\n      Result: {resultText}");
            }

            if (call.HasError && call.Exception is not null)
            {
                line.Append($"\n      Error: {call.Exception.Message}");
            }

            _output.WriteLine(line.ToString());
        }
    }

    /// <summary>
    /// Writes the metrics section.
    /// </summary>
    private void WriteMetricsSection(IReadOnlyList<MetricResult> metrics)
    {
        _output.WriteLine();
        _output.WriteLine("📊 Metrics:");

        foreach (var metric in metrics)
        {
            var passIcon = metric.Passed ? "✓" : "✗";
            _output.WriteLine($"   {passIcon} {metric.MetricName}: {metric.Score:F2} ({metric.Explanation ?? "no explanation"})");
        }
    }

    /// <summary>
    /// Builds the trace data object for JSON serialization.
    /// </summary>
    private static object BuildTraceData(TestResult result)
    {
        return new
        {
            testName = result.TestName,
            passed = result.Passed,
            score = result.Score,
            timestamp = DateTimeOffset.UtcNow,
            actualOutput = result.ActualOutput,
            details = result.Details,
            performance = result.Performance is null ? null : new
            {
                totalDurationMs = result.Performance.TotalDuration.TotalMilliseconds,
                timeToFirstTokenMs = result.Performance.TimeToFirstToken?.TotalMilliseconds,
                promptTokens = result.Performance.PromptTokens,
                completionTokens = result.Performance.CompletionTokens,
                estimatedCost = result.Performance.EstimatedCost,
                modelUsed = result.Performance.ModelUsed,
                wasStreaming = result.Performance.WasStreaming
            },
            toolUsage = result.ToolUsage is null ? null : new
            {
                totalCalls = result.ToolUsage.Count,
                hasErrors = result.ToolUsage.HasErrors,
                totalToolTimeMs = result.ToolUsage.TotalToolTime.TotalMilliseconds,
                calls = result.ToolUsage.Calls.Select(c => new
                {
                    name = c.Name,
                    order = c.Order,
                    callId = c.CallId,
                    arguments = c.Arguments,
                    result = c.Result,
                    durationMs = c.Duration?.TotalMilliseconds,
                    hasError = c.HasError,
                    error = c.Exception?.Message
                })
            },
            metrics = result.MetricResults?.Select(m => new
            {
                name = m.MetricName,
                score = m.Score,
                passed = m.Passed,
                explanation = m.Explanation
            }),
            failure = result.Failure is null ? null : new
            {
                whyItFailed = result.Failure.WhyItFailed,
                reasons = result.Failure.Reasons.Select(r => r.Description),
                suggestions = result.Failure.Suggestions.Select(s => s.Title)
            },
            error = result.Error?.Message
        };
    }

    /// <summary>
    /// Truncates a string to the specified max length.
    /// </summary>
    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(empty)";
        }

        // Replace newlines with spaces for single-line display
        var singleLine = value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");

        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return singleLine[..(maxLength - 3)] + "...";
    }
}
