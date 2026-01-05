// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for ToolEfficiencyMetric - a deterministic agentic metric.
/// </summary>
public class ToolEfficiencyMetricTests
{
    [Fact]
    public async Task EvaluateAsync_WithinLimits_Returns100()
    {
        // Arrange
        var metric = new ToolEfficiencyMetric(maxExpectedCalls: 5, maxExpectedDuration: TimeSpan.FromSeconds(10));
        
        var toolUsage = CreateToolUsage(3, TimeSpan.FromMilliseconds(500)); // 3 calls, 0.5s each
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("ToolEfficiency", result.MetricName);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_TooManyCalls_ReducesScore()
    {
        // Arrange
        var metric = new ToolEfficiencyMetric(maxExpectedCalls: 3, maxExpectedDuration: TimeSpan.FromSeconds(30));
        
        var toolUsage = CreateToolUsage(8, TimeSpan.FromMilliseconds(100)); // 8 calls, way over 3
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.True(result.Score < 100);
        Assert.Contains("calls", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_DurationTooLong_ReducesScore()
    {
        // Arrange
        var metric = new ToolEfficiencyMetric(maxExpectedCalls: 10, maxExpectedDuration: TimeSpan.FromSeconds(1));
        
        var toolUsage = CreateToolUsage(2, TimeSpan.FromSeconds(2)); // 2 calls, 2s each = 4s total
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.True(result.Score < 100);
    }
    
    [Fact]
    public async Task EvaluateAsync_DuplicateCalls_PenalizesScore()
    {
        // Arrange
        var metric = new ToolEfficiencyMetric(maxExpectedCalls: 10, maxExpectedDuration: TimeSpan.FromSeconds(30));
        
        // Create calls with duplicates
        var toolUsage = new ToolUsageReport();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            toolUsage.AddCall(new ToolCallRecord
            {
                Name = "SameTool", // Same tool called 5 times
                CallId = i.ToString(),
                Order = i + 1,
                StartTime = now,
                EndTime = now.AddMilliseconds(100)
            });
        }
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.True(result.Score < 100);
        Assert.NotNull(result.Details);
        Assert.Contains("duplicateCalls", result.Details.Keys);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoToolsCalled_Returns100()
    {
        // Arrange
        var metric = new ToolEfficiencyMetric();
        
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
        Assert.Contains("maximally efficient", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_EmptyToolUsage_Returns100()
    {
        // Arrange
        var metric = new ToolEfficiencyMetric();
        
        var context = new EvaluationContext
        {
            Input = "Hello",
            Output = "Hi!",
            ToolUsage = new ToolUsageReport() // Empty
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_DefaultLimits_AppliedCorrectly()
    {
        // Arrange - use default constructor
        var metric = new ToolEfficiencyMetric();
        
        var toolUsage = CreateToolUsage(3, TimeSpan.FromMilliseconds(500));
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - should use defaults from EvaluationDefaults
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var metric = new ToolEfficiencyMetric();
        
        // Assert
        Assert.Equal("ToolEfficiency", metric.Name);
        Assert.True(metric.RequiresToolUsage);
    }
    
    [Fact]
    public async Task EvaluateAsync_MetadataContainsExpectedKeys()
    {
        // Arrange
        var metric = new ToolEfficiencyMetric(maxExpectedCalls: 5, maxExpectedDuration: TimeSpan.FromSeconds(10));
        var toolUsage = CreateToolUsage(3, TimeSpan.FromMilliseconds(500));
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.NotNull(result.Details);
        Assert.Contains("callCount", result.Details.Keys);
        Assert.Contains("maxExpectedCalls", result.Details.Keys);
        Assert.Contains("totalDuration", result.Details.Keys);
        Assert.Contains("maxExpectedDuration", result.Details.Keys);
    }
    
    // Helper to create tool usage with specific count and duration
    private static ToolUsageReport CreateToolUsage(int callCount, TimeSpan durationPerCall)
    {
        var report = new ToolUsageReport();
        var now = DateTimeOffset.UtcNow;
        
        for (int i = 0; i < callCount; i++)
        {
            var startTime = now.Add(TimeSpan.FromMilliseconds(i * durationPerCall.TotalMilliseconds));
            report.AddCall(new ToolCallRecord
            {
                Name = $"Tool{i + 1}",
                CallId = i.ToString(),
                Order = i + 1,
                StartTime = startTime,
                EndTime = startTime.Add(durationPerCall)
            });
        }
        
        return report;
    }
}
