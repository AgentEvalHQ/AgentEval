// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for ToolArgumentsMetric - a deterministic agentic metric.
/// </summary>
public class ToolArgumentsMetricTests
{
    [Fact]
    public async Task EvaluateAsync_AllRequiredArgumentsProvided_Returns100()
    {
        // Arrange
        var requiredArgs = new Dictionary<string, IEnumerable<string>>
        {
            ["GetWeather"] = new[] { "city", "unit" },
            ["SendEmail"] = new[] { "to", "subject", "body" }
        };
        var metric = new ToolArgumentsMetric(requiredArgs);
        
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(new ToolCallRecord
        {
            Name = "GetWeather",
            CallId = "1",
            Arguments = new Dictionary<string, object?> { ["city"] = "Paris", ["unit"] = "celsius" }
        });
        toolUsage.AddCall(new ToolCallRecord
        {
            Name = "SendEmail",
            CallId = "2",
            Arguments = new Dictionary<string, object?> { ["to"] = "test@test.com", ["subject"] = "Hello", ["body"] = "Hi!" }
        });
        
        var context = new EvaluationContext
        {
            Input = "Check weather and send email",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("ToolArguments", result.MetricName);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_MissingArguments_ReturnsLowerScore()
    {
        // Arrange
        var requiredArgs = new Dictionary<string, IEnumerable<string>>
        {
            ["SendEmail"] = new[] { "to", "subject", "body" }
        };
        var metric = new ToolArgumentsMetric(requiredArgs);
        
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(new ToolCallRecord
        {
            Name = "SendEmail",
            CallId = "1",
            Arguments = new Dictionary<string, object?> { ["to"] = "test@test.com" } // Missing subject and body
        });
        
        var context = new EvaluationContext
        {
            Input = "Send email",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - 1/3 = 33.33%
        Assert.True(result.Score < 70);
        Assert.False(result.Passed);
        Assert.Contains("subject", result.Explanation);
        Assert.Contains("body", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoToolsCalled_Returns100()
    {
        // Arrange
        var requiredArgs = new Dictionary<string, IEnumerable<string>>
        {
            ["SendEmail"] = new[] { "to" }
        };
        var metric = new ToolArgumentsMetric(requiredArgs);
        
        var context = new EvaluationContext
        {
            Input = "Hello",
            Output = "Hi!",
            ToolUsage = null
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
        Assert.Contains("No tools called", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_ToolNotInRequirements_Ignored()
    {
        // Arrange
        var requiredArgs = new Dictionary<string, IEnumerable<string>>
        {
            ["SendEmail"] = new[] { "to" }
        };
        var metric = new ToolArgumentsMetric(requiredArgs);
        
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(new ToolCallRecord
        {
            Name = "OtherTool", // Not in requirements
            CallId = "1",
            Arguments = new Dictionary<string, object?> { ["param"] = "value" }
        });
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_CaseInsensitiveMatching()
    {
        // Arrange
        var requiredArgs = new Dictionary<string, IEnumerable<string>>
        {
            ["SendEmail"] = new[] { "To", "Subject" }
        };
        var metric = new ToolArgumentsMetric(requiredArgs);
        
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(new ToolCallRecord
        {
            Name = "sendemail", // Different case
            CallId = "1",
            Arguments = new Dictionary<string, object?> { ["to"] = "test@test.com", ["SUBJECT"] = "Hi" }
        });
        
        var context = new EvaluationContext
        {
            Input = "Send email",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_NullArguments_HandleGracefully()
    {
        // Arrange
        var requiredArgs = new Dictionary<string, IEnumerable<string>>
        {
            ["SendEmail"] = new[] { "to" }
        };
        var metric = new ToolArgumentsMetric(requiredArgs);
        
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(new ToolCallRecord
        {
            Name = "SendEmail",
            CallId = "1",
            Arguments = null // Null arguments
        });
        
        var context = new EvaluationContext
        {
            Input = "Send email",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("missing", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }
    
    [Fact]
    public void Constructor_NullRequiredArguments_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new ToolArgumentsMetric(null!));
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var metric = new ToolArgumentsMetric(new Dictionary<string, IEnumerable<string>>());
        
        // Assert
        Assert.Equal("ToolArguments", metric.Name);
        Assert.True(metric.RequiresToolUsage);
    }
    
    [Fact]
    public async Task EvaluateAsync_PartialMatch_ReturnsProportionalScore()
    {
        // Arrange
        var requiredArgs = new Dictionary<string, IEnumerable<string>>
        {
            ["Tool"] = new[] { "arg1", "arg2", "arg3", "arg4" }
        };
        var metric = new ToolArgumentsMetric(requiredArgs);
        
        var toolUsage = new ToolUsageReport();
        toolUsage.AddCall(new ToolCallRecord
        {
            Name = "Tool",
            CallId = "1",
            Arguments = new Dictionary<string, object?> { ["arg1"] = "v1", ["arg2"] = "v2" } // 2/4
        });
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - 2/4 = 50%
        Assert.Equal(50, result.Score);
        Assert.False(result.Passed);
    }
}
