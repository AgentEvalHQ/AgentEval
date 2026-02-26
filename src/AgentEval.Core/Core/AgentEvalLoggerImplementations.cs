// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models;

namespace AgentEval.Core;

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
