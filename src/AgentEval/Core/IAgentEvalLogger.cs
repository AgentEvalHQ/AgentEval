// SPDX-License-Identifier: MIT
using System;
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

/// <summary>
/// Default console-based logger implementation.
/// </summary>
public sealed class ConsoleAgentEvalLogger : IAgentEvalLogger
{
    private readonly LogLevel _minimumLevel;
    private readonly bool _useColors;
    private readonly object _lock = new();

    public ConsoleAgentEvalLogger(LogLevel minimumLevel = LogLevel.Information, bool useColors = true)
    {
        _minimumLevel = minimumLevel;
        _useColors = useColors && !Console.IsOutputRedirected;
    }

    public bool IsEnabled(LogLevel level) => level >= _minimumLevel;

    public void Log(LogLevel level, string message)
    {
        if (!IsEnabled(level)) return;

        lock (_lock)
        {
            WriteWithColor(level, $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }
    }

    public void Log(LogLevel level, Exception exception, string message)
    {
        if (!IsEnabled(level)) return;

        lock (_lock)
        {
            WriteWithColor(level, $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
            WriteWithColor(LogLevel.Debug, $"  Exception: {exception.GetType().Name}: {exception.Message}");
            if (IsEnabled(LogLevel.Trace))
            {
                WriteWithColor(LogLevel.Trace, exception.StackTrace ?? string.Empty);
            }
        }
    }

    public void Log(LogLevel level, string message, params (string Key, object? Value)[] properties)
    {
        if (!IsEnabled(level)) return;

        var propsString = string.Join(", ", Array.ConvertAll(properties, p => $"{p.Key}={p.Value}"));
        Log(level, $"{message} {{ {propsString} }}");
    }

    public void LogMetricResult(MetricResult result)
    {
        if (!IsEnabled(LogLevel.Information)) return;

        lock (_lock)
        {
            var level = result.Passed ? LogLevel.Information : LogLevel.Warning;
            var icon = result.Passed ? "✓" : "✗";
            WriteWithColor(level, $"[{DateTime.Now:HH:mm:ss}] {icon} {result.MetricName}: {result.Score:F2} - {result.Explanation}");
        }
    }

    public void LogFailure(FailureReport report)
    {
        if (!IsEnabled(LogLevel.Warning)) return;

        lock (_lock)
        {
            Console.WriteLine();
            WriteWithColor(LogLevel.Error, report.ToFormattedReport());
            Console.WriteLine();
        }
    }

    public void LogTimeline(ToolCallTimeline timeline)
    {
        if (!IsEnabled(LogLevel.Information)) return;

        lock (_lock)
        {
            Console.WriteLine();
            Console.WriteLine(timeline.ToAsciiDiagram());
            Console.WriteLine();
        }
    }

    public IDisposable BeginScope(string scopeName, params (string Key, object? Value)[] properties)
    {
        var propsString = properties.Length > 0
            ? " { " + string.Join(", ", Array.ConvertAll(properties, p => $"{p.Key}={p.Value}")) + " }"
            : "";

        Log(LogLevel.Debug, $"→ Entering scope: {scopeName}{propsString}");
        return new LogScope(this, scopeName);
    }

    private void WriteWithColor(LogLevel level, string message)
    {
        if (_useColors)
        {
            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = GetColorForLevel(level);
            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }
        else
        {
            Console.WriteLine(message);
        }
    }

    private static ConsoleColor GetColorForLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => ConsoleColor.DarkGray,
        LogLevel.Debug => ConsoleColor.Gray,
        LogLevel.Information => ConsoleColor.White,
        LogLevel.Warning => ConsoleColor.Yellow,
        LogLevel.Error => ConsoleColor.Red,
        LogLevel.Critical => ConsoleColor.DarkRed,
        _ => ConsoleColor.White
    };

    private sealed class LogScope : IDisposable
    {
        private readonly ConsoleAgentEvalLogger _logger;
        private readonly string _scopeName;

        public LogScope(ConsoleAgentEvalLogger logger, string scopeName)
        {
            _logger = logger;
            _scopeName = scopeName;
        }

        public void Dispose()
        {
            _logger.Log(LogLevel.Debug, $"← Exiting scope: {_scopeName}");
        }
    }
}

/// <summary>
/// Null logger that discards all log messages. Useful for testing.
/// </summary>
public sealed class NullAgentEvalLogger : IAgentEvalLogger
{
    public static readonly NullAgentEvalLogger Instance = new();

    private NullAgentEvalLogger() { }

    public bool IsEnabled(LogLevel level) => false;
    public void Log(LogLevel level, string message) { }
    public void Log(LogLevel level, Exception exception, string message) { }
    public void Log(LogLevel level, string message, params (string Key, object? Value)[] properties) { }
    public void LogMetricResult(MetricResult result) { }
    public void LogFailure(FailureReport report) { }
    public void LogTimeline(ToolCallTimeline timeline) { }
    public IDisposable BeginScope(string scopeName, params (string Key, object? Value)[] properties) => NullScope.Instance;

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
