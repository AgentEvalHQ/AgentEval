// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using System;
using System.IO;
using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

public class AgentEvalLoggerTests
{
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
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Information, useColors: false);
            logger.Log(LogLevel.Information, "Test message");

            var output = sw.ToString();
            Assert.Contains("Test message", output);
            Assert.Contains("[Information]", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ConsoleLogger_LogWithException_IncludesExceptionInfo()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Debug, useColors: false);
            var exception = new InvalidOperationException("Test exception");
            logger.Log(LogLevel.Error, exception, "An error occurred");

            var output = sw.ToString();
            Assert.Contains("An error occurred", output);
            Assert.Contains("InvalidOperationException", output);
            Assert.Contains("Test exception", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ConsoleLogger_LogWithProperties_IncludesProperties()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Information, useColors: false);
            logger.Log(LogLevel.Information, "Test message", ("Key1", "Value1"), ("Key2", 42));

            var output = sw.ToString();
            Assert.Contains("Test message", output);
            Assert.Contains("Key1=Value1", output);
            Assert.Contains("Key2=42", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ConsoleLogger_LogMetricResult_FormatsCorrectly()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Information, useColors: false);
            var result = MetricResult.Pass("TestMetric", 0.85, "Good result");

            logger.LogMetricResult(result);

            var output = sw.ToString();
            Assert.Contains("TestMetric", output);
            Assert.Contains("0.85", output);
            Assert.Contains("✓", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ConsoleLogger_LogMetricResult_FailedMetric()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Information, useColors: false);
            var result = MetricResult.Fail("TestMetric", "Score below threshold", 0.3);

            logger.LogMetricResult(result);

            var output = sw.ToString();
            Assert.Contains("TestMetric", output);
            Assert.Contains("✗", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ConsoleLogger_BeginScope_ReturnsDisposable()
    {
        var logger = new ConsoleAgentEvalLogger(LogLevel.Debug, useColors: false);

        using var scope = logger.BeginScope("TestScope", ("Id", "123"));

        Assert.NotNull(scope);
    }

    [Fact]
    public void ExtensionMethods_Work()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Trace, useColors: false);

            logger.LogTrace("Trace message");
            logger.LogDebug("Debug message");
            logger.LogInformation("Info message");
            logger.LogWarning("Warning message");
            logger.LogError("Error message");
            logger.LogCritical("Critical message");

            var output = sw.ToString();
            Assert.Contains("Trace message", output);
            Assert.Contains("Debug message", output);
            Assert.Contains("Info message", output);
            Assert.Contains("Warning message", output);
            Assert.Contains("Error message", output);
            Assert.Contains("Critical message", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void LogMetricStart_AtDebugLevel()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Debug, useColors: false);
            logger.LogMetricStart("TestMetric");

            var output = sw.ToString();
            Assert.Contains("Starting metric evaluation", output);
            Assert.Contains("TestMetric", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void LogMetricComplete_FormatsCorrectly()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Information, useColors: false);
            logger.LogMetricComplete("TestMetric", 0.95, TimeSpan.FromMilliseconds(150));

            var output = sw.ToString();
            Assert.Contains("TestMetric", output);
            Assert.Contains("0.95", output);
            Assert.Contains("150ms", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void LogToolCall_Success()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Debug, useColors: false);
            logger.LogToolCall("search", success: true, TimeSpan.FromMilliseconds(200));

            var output = sw.ToString();
            Assert.Contains("search", output);
            Assert.Contains("succeeded", output);
            Assert.Contains("200ms", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void LogToolCall_Failure()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Debug, useColors: false);
            logger.LogToolCall("calculator", success: false, TimeSpan.FromMilliseconds(50), "Division by zero");

            var output = sw.ToString();
            Assert.Contains("calculator", output);
            Assert.Contains("failed", output);
            Assert.Contains("Division by zero", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ConsoleLogger_LevelBelowMinimum_DoesNotLog()
    {
        var originalOut = Console.Out;
        try
        {
            using var sw = new StringWriter();
            Console.SetOut(sw);

            var logger = new ConsoleAgentEvalLogger(LogLevel.Error, useColors: false);
            logger.Log(LogLevel.Information, "This should not appear");

            var output = sw.ToString();
            Assert.Empty(output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }
}
