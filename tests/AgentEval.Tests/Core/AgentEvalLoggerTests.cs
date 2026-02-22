// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using System.IO;
using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for AgentEvalLogger implementations.
/// Uses a collection to prevent parallel execution since tests modify Console.Out.
/// </summary>
[Collection("ConsoleTests")]
public class AgentEvalLoggerTests
{
    private static readonly object ConsoleLock = new();
    
    /// <summary>
    /// Helper to capture console output safely, ensuring proper lifecycle management.
    /// </summary>
    private static string CaptureConsoleOutput(Action<ConsoleAgentEvalLogger> action, LogLevel minimumLevel = LogLevel.Information)
    {
        lock (ConsoleLock)
        {
            var originalOut = Console.Out;
            var sw = new StringWriter();
            try
            {
                Console.SetOut(sw);
                var logger = new ConsoleAgentEvalLogger(minimumLevel, useColors: false);
                action(logger);
                sw.Flush();
                return sw.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
                sw.Dispose();
            }
        }
    }

    [Fact]
    public void ConsoleLogger_IsEnabled_RespectsMinimumLevel()
    {
        var logger = new ConsoleAgentEvalLogger(LogLevel.Warning);

        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        Assert.True(logger.IsEnabled(LogLevel.Error));
        Assert.True(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void NullLogger_IsEnabled_AlwaysFalse()
    {
        var logger = NullAgentEvalLogger.Instance;

        Assert.False(logger.IsEnabled(LogLevel.Trace));
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.False(logger.IsEnabled(LogLevel.Warning));
        Assert.False(logger.IsEnabled(LogLevel.Error));
        Assert.False(logger.IsEnabled(LogLevel.Critical));
    }

    [Fact]
    public void NullLogger_IsSingleton()
    {
        var logger1 = NullAgentEvalLogger.Instance;
        var logger2 = NullAgentEvalLogger.Instance;

        Assert.Same(logger1, logger2);
    }

    [Fact]
    public void ConsoleLogger_Log_WritesToConsole()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            logger.Log(LogLevel.Information, "Test message");
        });

        Assert.Contains("Test message", output);
        Assert.Contains("[Information]", output);
    }

    [Fact]
    public void ConsoleLogger_LogWithException_IncludesExceptionInfo()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            var exception = new InvalidOperationException("Test exception");
            logger.Log(LogLevel.Error, exception, "An error occurred");
        }, LogLevel.Debug);

        Assert.Contains("An error occurred", output);
        Assert.Contains("InvalidOperationException", output);
        Assert.Contains("Test exception", output);
    }

    [Fact]
    public void ConsoleLogger_LogWithProperties_IncludesProperties()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            logger.Log(LogLevel.Information, "Test message", ("Key1", "Value1"), ("Key2", 42));
        });

        Assert.Contains("Test message", output);
        Assert.Contains("Key1=Value1", output);
        Assert.Contains("Key2=42", output);
    }

    [Fact]
    public void ConsoleLogger_LogMetricResult_FormatsCorrectly()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            var result = MetricResult.Pass("TestMetric", 0.85, "Good result");
            logger.LogMetricResult(result);
        });

        Assert.Contains("TestMetric", output);
        Assert.Contains("0.85", output);
        Assert.Contains("✓", output);
    }

    [Fact]
    public void ConsoleLogger_LogMetricResult_FailedMetric()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            var result = MetricResult.Fail("TestMetric", "Score below threshold", 0.3);
            logger.LogMetricResult(result);
        });

        Assert.Contains("TestMetric", output);
        Assert.Contains("✗", output);
    }

    [Fact]
    public void ConsoleLogger_BeginScope_ReturnsDisposable()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            using (var scope = logger.BeginScope("TestScope", ("Id", "123")))
            {
                Assert.NotNull(scope);
            }
        }, LogLevel.Debug);

        // Verify scope enter/exit messages were logged
        Assert.Contains("TestScope", output);
    }

    [Fact]
    public void ExtensionMethods_Work()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            logger.LogTrace("Trace message");
            logger.LogDebug("Debug message");
            logger.LogInformation("Info message");
            logger.LogWarning("Warning message");
            logger.LogError("Error message");
            logger.LogCritical("Critical message");
        }, LogLevel.Trace);

        Assert.Contains("Trace message", output);
        Assert.Contains("Debug message", output);
        Assert.Contains("Info message", output);
        Assert.Contains("Warning message", output);
        Assert.Contains("Error message", output);
        Assert.Contains("Critical message", output);
    }

    [Fact]
    public void LogMetricStart_AtDebugLevel()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            logger.LogMetricStart("TestMetric");
        }, LogLevel.Debug);

        Assert.Contains("Starting metric evaluation", output);
        Assert.Contains("TestMetric", output);
    }

    [Fact]
    public void LogMetricComplete_FormatsCorrectly()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            logger.LogMetricComplete("TestMetric", 0.95, TimeSpan.FromMilliseconds(150));
        });

        Assert.Contains("TestMetric", output);
        Assert.Contains("0.95", output);
        Assert.Contains("150ms", output);
    }

    [Fact]
    public void LogToolCall_Success()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            logger.LogToolCall("search", success: true, TimeSpan.FromMilliseconds(200));
        }, LogLevel.Debug);

        Assert.Contains("search", output);
        Assert.Contains("succeeded", output);
        Assert.Contains("200ms", output);
    }

    [Fact]
    public void LogToolCall_Failure()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            logger.LogToolCall("calculator", success: false, TimeSpan.FromMilliseconds(50), "Division by zero");
        }, LogLevel.Debug);

        Assert.Contains("calculator", output);
        Assert.Contains("failed", output);
        Assert.Contains("Division by zero", output);
    }

    [Fact]
    public void ConsoleLogger_LevelBelowMinimum_DoesNotLog()
    {
        var output = CaptureConsoleOutput(logger =>
        {
            logger.Log(LogLevel.Information, "This should not appear");
        }, LogLevel.Error);

        Assert.Empty(output);
    }
}
