// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

/// <summary>
/// Log level for AgentEval logging abstraction.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None
}

/// <summary>
/// Logging abstraction for AgentEval that supports console, Microsoft.Extensions.Logging,
/// and OpenTelemetry integration. Enables structured logging without hard dependency
/// on any specific logging framework.
/// </summary>
public interface IAgentEvalLogger
{
    /// <summary>
    /// Logs a message at the specified level.
    /// </summary>
    void Log(LogLevel level, string message);

    /// <summary>
    /// Logs a message with exception at the specified level.
    /// </summary>
    void Log(LogLevel level, Exception exception, string message);

    /// <summary>
    /// Logs structured data with a message.
    /// </summary>
    void Log(LogLevel level, string message, params (string Key, object? Value)[] properties);

    /// <summary>
    /// Logs a metric result (success or failure).
    /// </summary>
    void LogMetricResult(MetricResult result);

    /// <summary>
    /// Logs a failure report with full context.
    /// </summary>
    void LogFailure(FailureReport report);

    /// <summary>
    /// Logs a tool call timeline.
    /// </summary>
    void LogTimeline(ToolCallTimeline timeline);

    /// <summary>
    /// Checks if the specified log level is enabled.
    /// </summary>
    bool IsEnabled(LogLevel level);

    /// <summary>
    /// Creates a scoped logger with additional context.
    /// </summary>
    IDisposable BeginScope(string scopeName, params (string Key, object? Value)[] properties);
}

/// <summary>
/// Extension methods for convenient logging.
/// </summary>
public static class AgentEvalLoggerExtensions
{
    public static void LogTrace(this IAgentEvalLogger logger, string message)
        => logger.Log(LogLevel.Trace, message);

    public static void LogDebug(this IAgentEvalLogger logger, string message)
        => logger.Log(LogLevel.Debug, message);

    public static void LogInformation(this IAgentEvalLogger logger, string message)
        => logger.Log(LogLevel.Information, message);

    public static void LogWarning(this IAgentEvalLogger logger, string message)
        => logger.Log(LogLevel.Warning, message);

    public static void LogError(this IAgentEvalLogger logger, string message)
        => logger.Log(LogLevel.Error, message);

    public static void LogError(this IAgentEvalLogger logger, Exception exception, string message)
        => logger.Log(LogLevel.Error, exception, message);

    public static void LogCritical(this IAgentEvalLogger logger, string message)
        => logger.Log(LogLevel.Critical, message);

    public static void LogCritical(this IAgentEvalLogger logger, Exception exception, string message)
        => logger.Log(LogLevel.Critical, exception, message);

    /// <summary>
    /// Logs a metric evaluation start.
    /// </summary>
    public static void LogMetricStart(this IAgentEvalLogger logger, string metricName)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.Log(LogLevel.Debug, $"Starting metric evaluation: {metricName}");
        }
    }

    /// <summary>
    /// Logs a metric evaluation completion.
    /// </summary>
    public static void LogMetricComplete(this IAgentEvalLogger logger, string metricName, double score, TimeSpan duration)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.Log(LogLevel.Information, $"Metric '{metricName}' completed: Score={score:F2}, Duration={duration.TotalMilliseconds:F0}ms");
        }
    }

    /// <summary>
    /// Logs a tool call.
    /// </summary>
    public static void LogToolCall(this IAgentEvalLogger logger, string toolName, bool success, TimeSpan duration, string? error = null)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            var status = success ? "succeeded" : "failed";
            var message = $"Tool '{toolName}' {status} in {duration.TotalMilliseconds:F0}ms";
            if (!success && !string.IsNullOrEmpty(error))
            {
                message += $": {error}";
            }
            logger.Log(success ? LogLevel.Debug : LogLevel.Warning, message);
        }
    }
}
