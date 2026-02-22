// SPDX-License-Identifier: MIT
using System;
using AgentEval.Models;
using Microsoft.Extensions.Logging;
using MELogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace AgentEval.Core;

/// <summary>
/// Adapter that wraps Microsoft.Extensions.Logging.ILogger to implement IAgentEvalLogger.
/// Enables AgentEval to integrate with the standard .NET logging infrastructure.
/// </summary>
public sealed class MicrosoftLoggingAdapter : IAgentEvalLogger
{
    private readonly ILogger _logger;

    public MicrosoftLoggingAdapter(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates an adapter from an ILoggerFactory.
    /// </summary>
    public static MicrosoftLoggingAdapter Create(ILoggerFactory loggerFactory, string categoryName = "AgentEval")
    {
        return new MicrosoftLoggingAdapter(loggerFactory.CreateLogger(categoryName));
    }

    /// <summary>
    /// Creates an adapter from a generic ILogger.
    /// </summary>
    public static MicrosoftLoggingAdapter Create<T>(ILogger<T> logger)
    {
        return new MicrosoftLoggingAdapter(logger);
    }

    public bool IsEnabled(LogLevel level) => _logger.IsEnabled(ToMicrosoftLogLevel(level));

    public void Log(LogLevel level, string message)
    {
        var meLevel = ToMicrosoftLogLevel(level);
        _logger.Log(meLevel, "{Message}", message);
    }

    public void Log(LogLevel level, Exception exception, string message)
    {
        var meLevel = ToMicrosoftLogLevel(level);
        _logger.Log(meLevel, exception, "{Message}", message);
    }

    public void Log(LogLevel level, string message, params (string Key, object? Value)[] properties)
    {
        var meLevel = ToMicrosoftLogLevel(level);

        // Build structured log state
        var state = new LogState(message, properties);
        _logger.Log(meLevel, default, state, null, (s, _) => s.ToString());
    }

    public void LogMetricResult(MetricResult result)
    {
        var level = result.Passed ? LogLevel.Information : LogLevel.Warning;
        Log(level, "Metric {MetricName} completed with score {Score}",
            ("MetricName", result.MetricName),
            ("Score", result.Score),
            ("Passed", result.Passed),
            ("Explanation", result.Explanation));
    }

    public void LogFailure(FailureReport report)
    {
        _logger.LogError(
            "Evaluation failed: {WhyItFailed} for metric {MetricName}. Reasons: {ReasonCount}",
            report.WhyItFailed,
            report.MetricName ?? "Unknown",
            report.Reasons.Count);

        // Log each reason
        foreach (var reason in report.Reasons)
        {
            var meLevel = reason.Severity switch
            {
                FailureSeverity.Critical => MELogLevel.Critical,
                FailureSeverity.Error => MELogLevel.Error,
                FailureSeverity.Warning => MELogLevel.Warning,
                _ => MELogLevel.Information
            };

            _logger.Log(meLevel, "  [{Category}] {Description}", reason.Category, reason.Description);
        }

        // Log suggestions at debug level
        if (_logger.IsEnabled(MELogLevel.Debug))
        {
            foreach (var suggestion in report.Suggestions)
            {
                _logger.LogDebug("  Suggestion: {Title} - {Description}", suggestion.Title, suggestion.Description);
            }
        }
    }

    public void LogTimeline(ToolCallTimeline timeline)
    {
        _logger.LogInformation(
            "Tool call timeline: {TotalCalls} calls, {TotalDuration}ms total, TTFT {TTFT}ms, {FailedCalls} failed",
            timeline.TotalToolCalls,
            timeline.TotalDuration.TotalMilliseconds,
            timeline.TimeToFirstToken.TotalMilliseconds,
            timeline.FailedToolCalls);

        // Log individual tool calls at debug level
        if (_logger.IsEnabled(MELogLevel.Debug))
        {
            foreach (var inv in timeline.Invocations)
            {
                var status = inv.Succeeded ? "succeeded" : "failed";
                _logger.LogDebug(
                    "  Tool {ToolName} {Status} in {Duration}ms",
                    inv.ToolName,
                    status,
                    inv.Duration.TotalMilliseconds);
            }
        }
    }

    public IDisposable BeginScope(string scopeName, params (string Key, object? Value)[] properties)
    {
        var state = new LogState(scopeName, properties);
        return _logger.BeginScope(state) ?? NullScope.Instance;
    }

    private static MELogLevel ToMicrosoftLogLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => MELogLevel.Trace,
        LogLevel.Debug => MELogLevel.Debug,
        LogLevel.Information => MELogLevel.Information,
        LogLevel.Warning => MELogLevel.Warning,
        LogLevel.Error => MELogLevel.Error,
        LogLevel.Critical => MELogLevel.Critical,
        LogLevel.None => MELogLevel.None,
        _ => MELogLevel.Information
    };

    /// <summary>
    /// Structured log state that supports key-value properties.
    /// </summary>
    private sealed class LogState
    {
        private readonly string _message;
        private readonly (string Key, object? Value)[] _properties;

        public LogState(string message, (string Key, object? Value)[] properties)
        {
            _message = message;
            _properties = properties;
        }

        public override string ToString()
        {
            if (_properties.Length == 0)
                return _message;

            var props = string.Join(", ", Array.ConvertAll(_properties, p => $"{p.Key}={p.Value}"));
            return $"{_message} {{ {props} }}";
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
