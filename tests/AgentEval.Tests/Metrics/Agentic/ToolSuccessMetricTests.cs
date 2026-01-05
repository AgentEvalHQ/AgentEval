// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for ToolSuccessMetric - a deterministic agentic metric.
/// </summary>
public class ToolSuccessMetricTests
{
    [Fact]
    public async Task EvaluateAsync_AllToolsSucceeded_Returns100()
    {
        // Arrange
        var metric = new ToolSuccessMetric();
        var toolUsage = CreateToolUsage(
            ("GetWeather", null),
            ("SendEmail", null),
            ("SaveFile", null)
        );
        
        var context = new EvaluationContext
        {
            Input = "Do multiple things",
            Output = "All done!",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("ToolSuccess", result.MetricName);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_SomeToolsFailed_ReturnsProportionalScore()
    {
        // Arrange
        var metric = new ToolSuccessMetric();
        var toolUsage = CreateToolUsage(
            ("ToolA", null), // Success
            ("ToolB", new Exception("Connection failed")), // Failed
            ("ToolC", null), // Success
            ("ToolD", new Exception("Timeout")) // Failed
        );
        
        var context = new EvaluationContext
        {
            Input = "Do things",
            Output = "Partial results",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - 2/4 succeeded = 50%
        Assert.Equal(50, result.Score);
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_AllToolsFailed_Returns0()
    {
        // Arrange
        var metric = new ToolSuccessMetric();
        var toolUsage = CreateToolUsage(
            ("ToolA", new Exception("Error 1")),
            ("ToolB", new Exception("Error 2"))
        );
        
        var context = new EvaluationContext
        {
            Input = "Do things",
            Output = "Failed",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(0, result.Score);
        Assert.False(result.Passed);
        Assert.Contains("Error", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoToolsCalled_Returns100()
    {
        // Arrange
        var metric = new ToolSuccessMetric();
        
        var context = new EvaluationContext
        {
            Input = "Hello",
            Output = "Hi!",
            ToolUsage = null
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - No tools called = 100% success rate (vacuous truth)
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
        Assert.Contains("No tools called", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_EmptyToolUsage_Returns100()
    {
        // Arrange
        var metric = new ToolSuccessMetric();
        
        var context = new EvaluationContext
        {
            Input = "Hello",
            Output = "Hi!",
            ToolUsage = new ToolUsageReport()
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_IncludesFailedToolsInMetadata()
    {
        // Arrange
        var metric = new ToolSuccessMetric();
        var toolUsage = CreateToolUsage(
            ("SuccessTool", null),
            ("FailedTool", new InvalidOperationException("Something broke"))
        );
        
        var context = new EvaluationContext
        {
            Input = "Test",
            Output = "Result",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.NotNull(result.Details);
        Assert.Contains("totalCalls", result.Details.Keys);
        Assert.Contains("successfulCalls", result.Details.Keys);
        Assert.Contains("failedCalls", result.Details.Keys);
        Assert.Equal(2, result.Details["totalCalls"]);
        Assert.Equal(1, result.Details["successfulCalls"]);
    }
    
    [Fact]
    public async Task EvaluateAsync_OneToolOneFail_Returns50()
    {
        // Arrange
        var metric = new ToolSuccessMetric();
        var toolUsage = CreateToolUsage(
            ("OnlyTool", new Exception("Failed"))
        );
        
        var context = new EvaluationContext
        {
            Input = "Test",
            Output = "Result",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(0, result.Score); // 0/1 succeeded
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var metric = new ToolSuccessMetric();
        
        // Assert
        Assert.Equal("ToolSuccess", metric.Name);
        Assert.True(metric.RequiresToolUsage);
    }
    
    // Helper to create tool usage report with success/failure
    private static ToolUsageReport CreateToolUsage(params (string name, Exception? error)[] tools)
    {
        var report = new ToolUsageReport();
        var order = 1;
        foreach (var (name, error) in tools)
        {
            var record = new ToolCallRecord
            {
                Name = name,
                CallId = Guid.NewGuid().ToString(),
                Order = order++
            };
            record.Exception = error;
            report.AddCall(record);
        }
        return report;
    }
}
