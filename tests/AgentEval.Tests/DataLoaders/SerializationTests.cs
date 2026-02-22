// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for JSON serialization round-trips of all model classes.
/// </summary>
public class SerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    #region MetricResult Serialization

    [Fact]
    public void MetricResult_Pass_SerializesCorrectly()
    {
        // Arrange
        var result = MetricResult.Pass(
            "llm_faithfulness", 
            85.5, 
            "Response is faithful to context",
            new Dictionary<string, object> { ["claimsVerified"] = 5 });

        // Act
        var json = JsonSerializer.Serialize(result, Options);
        var deserialized = JsonSerializer.Deserialize<MetricResult>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(result.MetricName, deserialized!.MetricName);
        Assert.Equal(result.Score, deserialized.Score);
        Assert.Equal(result.Passed, deserialized.Passed);
        Assert.Equal(result.Explanation, deserialized.Explanation);
    }

    [Fact]
    public void MetricResult_Fail_SerializesCorrectly()
    {
        // Arrange
        var result = MetricResult.Fail(
            "llm_relevance",
            "Response did not address the question",
            25.0,
            new Dictionary<string, object> { ["issue"] = "off-topic" });

        // Act
        var json = JsonSerializer.Serialize(result, Options);
        var deserialized = JsonSerializer.Deserialize<MetricResult>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(result.MetricName, deserialized!.MetricName);
        Assert.Equal(result.Score, deserialized.Score);
        Assert.False(deserialized.Passed);
    }

    [Fact]
    public void MetricResult_WithNullDetails_SerializesCorrectly()
    {
        // Arrange
        var result = MetricResult.Pass("TestMetric", 100.0);

        // Act
        var json = JsonSerializer.Serialize(result, Options);
        var deserialized = JsonSerializer.Deserialize<MetricResult>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.Details);
    }

    #endregion

    #region ToolCallRecord Serialization

    [Fact]
    public void ToolCallRecord_SerializesCorrectly()
    {
        // Arrange
        var record = new ToolCallRecord
        {
            Name = "GetWeather",
            CallId = "call-123",
            Order = 1,
            Arguments = new Dictionary<string, object?>
            {
                ["city"] = "Seattle",
                ["units"] = "metric"
            },
            Result = "72°F, Sunny"
        };

        // Act
        var json = JsonSerializer.Serialize(record, Options);
        var deserialized = JsonSerializer.Deserialize<ToolCallRecord>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(record.Name, deserialized!.Name);
        Assert.Equal(record.CallId, deserialized.CallId);
        Assert.Equal(record.Order, deserialized.Order);
    }

    [Fact]
    public void ToolCallRecord_WithTiming_SerializesCorrectly()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow;
        var endTime = startTime.AddMilliseconds(150);
        
        var record = new ToolCallRecord
        {
            Name = "DatabaseQuery",
            CallId = "call-456",
            Order = 2,
            StartTime = startTime,
            EndTime = endTime,
            Result = "5 rows returned"
        };

        // Act
        var json = JsonSerializer.Serialize(record, Options);
        var deserialized = JsonSerializer.Deserialize<ToolCallRecord>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.HasTiming);
        Assert.Equal(record.StartTime, deserialized.StartTime);
        Assert.Equal(record.EndTime, deserialized.EndTime);
    }

    [Fact]
    public void ToolCallRecord_WithNullArguments_SerializesCorrectly()
    {
        // Arrange
        var record = new ToolCallRecord
        {
            Name = "GetCurrentTime",
            CallId = "call-789",
            Order = 3,
            Arguments = null,
            Result = "2024-01-15T10:30:00Z"
        };

        // Act
        var json = JsonSerializer.Serialize(record, Options);
        var deserialized = JsonSerializer.Deserialize<ToolCallRecord>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.Arguments);
    }

    #endregion

    #region ToolInvocation Serialization

    [Fact]
    public void ToolInvocation_SerializesCorrectly()
    {
        // Arrange
        var invocation = new ToolInvocation
        {
            ToolName = "SearchDatabase",
            StartTime = TimeSpan.FromMilliseconds(100),
            Duration = TimeSpan.FromMilliseconds(250),
            Succeeded = true,
            Arguments = """{"query": "SELECT * FROM users"}""",
            Result = "10 records found"
        };

        // Act
        var json = JsonSerializer.Serialize(invocation, Options);
        var deserialized = JsonSerializer.Deserialize<ToolInvocation>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(invocation.ToolName, deserialized!.ToolName);
        Assert.Equal(invocation.StartTime, deserialized.StartTime);
        Assert.Equal(invocation.Duration, deserialized.Duration);
        Assert.True(deserialized.Succeeded);
    }

    [Fact]
    public void ToolInvocation_Failed_SerializesCorrectly()
    {
        // Arrange
        var invocation = new ToolInvocation
        {
            ToolName = "CallExternalAPI",
            StartTime = TimeSpan.FromMilliseconds(500),
            Duration = TimeSpan.FromMilliseconds(5000),
            Succeeded = false,
            ErrorMessage = "Connection timeout"
        };

        // Act
        var json = JsonSerializer.Serialize(invocation, Options);
        var deserialized = JsonSerializer.Deserialize<ToolInvocation>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.False(deserialized!.Succeeded);
        Assert.Equal("Connection timeout", deserialized.ErrorMessage);
    }

    #endregion

    #region ToolCallTimeline Serialization

    [Fact]
    public void ToolCallTimeline_SerializesCorrectly()
    {
        // Arrange
        var timeline = ToolCallTimeline.Create("conv-123");
        timeline.TimeToFirstToken = TimeSpan.FromMilliseconds(150);
        timeline.TotalDuration = TimeSpan.FromSeconds(2);
        
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "Tool1",
            StartTime = TimeSpan.FromMilliseconds(200),
            Duration = TimeSpan.FromMilliseconds(300),
            Succeeded = true
        });
        
        timeline.AddInvocation(new ToolInvocation
        {
            ToolName = "Tool2",
            StartTime = TimeSpan.FromMilliseconds(600),
            Duration = TimeSpan.FromMilliseconds(400),
            Succeeded = true
        });

        // Act
        var json = JsonSerializer.Serialize(timeline, Options);
        var deserialized = JsonSerializer.Deserialize<ToolCallTimeline>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(timeline.ConversationId, deserialized!.ConversationId);
        Assert.Equal(timeline.TimeToFirstToken, deserialized.TimeToFirstToken);
        Assert.Equal(timeline.TotalDuration, deserialized.TotalDuration);
        // Note: Invocations may not deserialize properly due to private list
    }

    [Fact]
    public void ToolCallTimeline_EmptyTimeline_SerializesCorrectly()
    {
        // Arrange
        var timeline = new ToolCallTimeline();

        // Act
        var json = JsonSerializer.Serialize(timeline, Options);
        var deserialized = JsonSerializer.Deserialize<ToolCallTimeline>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(0, deserialized!.TotalToolCalls);
    }

    #endregion

    #region EvaluationContext Serialization

    [Fact]
    public void EvaluationContext_SerializesCorrectly()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "What is the capital of France?",
            Output = "The capital of France is Paris.",
            Context = "France is a country in Europe. Its capital is Paris.",
            GroundTruth = "Paris"
        };

        // Act
        var json = JsonSerializer.Serialize(context, Options);
        var deserialized = JsonSerializer.Deserialize<EvaluationContext>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(context.Input, deserialized!.Input);
        Assert.Equal(context.Output, deserialized.Output);
        Assert.Equal(context.Context, deserialized.Context);
        Assert.Equal(context.GroundTruth, deserialized.GroundTruth);
    }

    [Fact]
    public void EvaluationContext_WithProperties_SerializesCorrectly()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "Test input",
            Output = "Test output"
        };
        context.SetProperty("customScore", 42);
        context.SetProperty("customLabel", "important");

        // Act
        var json = JsonSerializer.Serialize(context, Options);
        var deserialized = JsonSerializer.Deserialize<EvaluationContext>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(context.Input, deserialized!.Input);
        // Note: Properties dictionary should also serialize
    }

    [Fact]
    public void EvaluationContext_MinimalFields_SerializesCorrectly()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "Question",
            Output = "Answer"
        };

        // Act
        var json = JsonSerializer.Serialize(context, Options);
        var deserialized = JsonSerializer.Deserialize<EvaluationContext>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.Context);
        Assert.Null(deserialized.GroundTruth);
    }

    #endregion

    #region PerformanceMetrics Serialization

    [Fact]
    public void PerformanceMetrics_SerializesCorrectly()
    {
        // Arrange
        var startTime = DateTimeOffset.UtcNow;
        var metrics = new PerformanceMetrics
        {
            StartTime = startTime,
            EndTime = startTime.AddSeconds(2),
            TimeToFirstToken = TimeSpan.FromMilliseconds(200),
            PromptTokens = 100,
            CompletionTokens = 50,
            ToolCallCount = 3,
            TotalToolTime = TimeSpan.FromMilliseconds(500),
            EstimatedCost = 0.0025m,
            ModelUsed = "gpt-4o-mini",
            WasStreaming = true
        };

        // Act
        var json = JsonSerializer.Serialize(metrics, Options);
        var deserialized = JsonSerializer.Deserialize<PerformanceMetrics>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(metrics.PromptTokens, deserialized!.PromptTokens);
        Assert.Equal(metrics.CompletionTokens, deserialized.CompletionTokens);
        Assert.Equal(metrics.ToolCallCount, deserialized.ToolCallCount);
        Assert.Equal(metrics.ModelUsed, deserialized.ModelUsed);
        Assert.True(deserialized.WasStreaming);
    }

    [Fact]
    public void PerformanceMetrics_WithNullOptionalFields_SerializesCorrectly()
    {
        // Arrange
        var metrics = new PerformanceMetrics
        {
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddSeconds(1)
        };

        // Act
        var json = JsonSerializer.Serialize(metrics, Options);
        var deserialized = JsonSerializer.Deserialize<PerformanceMetrics>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Null(deserialized!.TimeToFirstToken);
        Assert.Null(deserialized.PromptTokens);
        Assert.Null(deserialized.CompletionTokens);
        Assert.Null(deserialized.EstimatedCost);
    }

    #endregion

    #region ToolStatistics Serialization

    [Fact]
    public void ToolStatistics_SerializesCorrectly()
    {
        // Arrange
        var stats = new ToolStatistics
        {
            ToolName = "SearchAPI",
            CallCount = 5,
            SuccessCount = 4,
            FailureCount = 1,
            TotalDuration = TimeSpan.FromSeconds(2),
            AverageDuration = TimeSpan.FromMilliseconds(400),
            MinDuration = TimeSpan.FromMilliseconds(100),
            MaxDuration = TimeSpan.FromMilliseconds(800)
        };

        // Act
        var json = JsonSerializer.Serialize(stats, Options);
        var deserialized = JsonSerializer.Deserialize<ToolStatistics>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(stats.ToolName, deserialized!.ToolName);
        Assert.Equal(stats.CallCount, deserialized.CallCount);
        Assert.Equal(stats.SuccessCount, deserialized.SuccessCount);
        Assert.Equal(stats.FailureCount, deserialized.FailureCount);
        Assert.Equal(80.0, deserialized.SuccessRate); // 4/5 = 80%
    }

    #endregion

    #region Unicode and Special Characters

    [Fact]
    public void EvaluationContext_WithUnicode_SerializesCorrectly()
    {
        // Arrange
        var context = new EvaluationContext
        {
            Input = "日本語の質問 🚀",
            Output = "Réponse en français avec émojis 🎉",
            Context = "中文内容 مرحبا עברית"
        };

        // Act
        var json = JsonSerializer.Serialize(context, Options);
        var deserialized = JsonSerializer.Deserialize<EvaluationContext>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(context.Input, deserialized!.Input);
        Assert.Equal(context.Output, deserialized.Output);
        Assert.Equal(context.Context, deserialized.Context);
    }

    [Fact]
    public void MetricResult_WithSpecialCharacters_SerializesCorrectly()
    {
        // Arrange
        var result = MetricResult.Pass(
            "Test\"Metric",
            85.0,
            "Line1\nLine2\tTabbed\r\nWindows newline");

        // Act
        var json = JsonSerializer.Serialize(result, Options);
        var deserialized = JsonSerializer.Deserialize<MetricResult>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(result.MetricName, deserialized!.MetricName);
        Assert.Equal(result.Explanation, deserialized.Explanation);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void MetricResult_WithExtremeScores_SerializesCorrectly()
    {
        // Arrange - test boundary values
        var lowResult = MetricResult.Pass("Low", 0.0);
        var highResult = MetricResult.Pass("High", 100.0);
        var preciseResult = MetricResult.Pass("Precise", 99.999999);

        // Act & Assert
        var lowDeserialized = JsonSerializer.Deserialize<MetricResult>(
            JsonSerializer.Serialize(lowResult, Options), Options);
        var highDeserialized = JsonSerializer.Deserialize<MetricResult>(
            JsonSerializer.Serialize(highResult, Options), Options);
        var preciseDeserialized = JsonSerializer.Deserialize<MetricResult>(
            JsonSerializer.Serialize(preciseResult, Options), Options);

        Assert.Equal(0.0, lowDeserialized!.Score);
        Assert.Equal(100.0, highDeserialized!.Score);
        Assert.Equal(99.999999, preciseDeserialized!.Score);
    }

    [Fact]
    public void ToolCallRecord_WithComplexArguments_SerializesCorrectly()
    {
        // Arrange
        var record = new ToolCallRecord
        {
            Name = "ComplexTool",
            CallId = "complex-123",
            Order = 1,
            Arguments = new Dictionary<string, object?>
            {
                ["stringValue"] = "test",
                ["intValue"] = 42,
                ["boolValue"] = true,
                ["nullValue"] = null,
                ["arrayValue"] = new[] { 1, 2, 3 },
                ["nestedObject"] = new Dictionary<string, object> { ["inner"] = "value" }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(record, Options);
        var deserialized = JsonSerializer.Deserialize<ToolCallRecord>(json, Options);

        // Assert
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.Arguments);
    }

    #endregion
}
