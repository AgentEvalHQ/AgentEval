// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests;

/// <summary>
/// Tests for ToolSelectionMetric - a deterministic agentic metric.
/// </summary>
public class ToolSelectionMetricTests
{
    [Fact]
    public async Task EvaluateAsync_AllExpectedToolsCalled_Returns100()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "GetWeather", "FormatResponse" });
        var toolUsage = CreateToolUsage("GetWeather", "FormatResponse");
        
        var context = new EvaluationContext
        {
            Input = "What's the weather?",
            Output = "It's sunny.",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal("ToolSelection", result.MetricName);
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoToolsCalled_WhenExpected_ReturnsFail()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "GetWeather" });
        
        var context = new EvaluationContext
        {
            Input = "What's the weather?",
            Output = "I don't know.",
            ToolUsage = null // No tools called
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
        Assert.Contains("GetWeather", result.Explanation);
        Assert.Contains("none were called", result.Explanation);
    }
    
    [Fact]
    public async Task EvaluateAsync_NoToolsExpected_NoToolsCalled_Returns100()
    {
        // Arrange
        var metric = new ToolSelectionMetric(Array.Empty<string>());
        
        var context = new EvaluationContext
        {
            Input = "Hello",
            Output = "Hi there!",
            ToolUsage = null
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.Equal(100, result.Score);
        Assert.True(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_PartialMatch_ReturnsProportionalScore()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "ToolA", "ToolB", "ToolC" });
        var toolUsage = CreateToolUsage("ToolA", "ToolB"); // Missing ToolC
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - 2/3 matched = ~66.67%
        Assert.True(result.Score < 70); // Should fail threshold
        Assert.False(result.Passed);
    }
    
    [Fact]
    public async Task EvaluateAsync_ExtraToolsCalled_ReducesScore()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "ToolA" });
        var toolUsage = CreateToolUsage("ToolA", "ExtraTool1", "ExtraTool2");
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert - 100% match but penalized for extra tools
        Assert.True(result.Score < 100);
    }
    
    [Fact]
    public async Task EvaluateAsync_CaseInsensitiveMatching()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "GetWeather", "SendEmail" });
        var toolUsage = CreateToolUsage("getweather", "SENDEMAIL"); // Different case
        
        var context = new EvaluationContext
        {
            Input = "Check weather and send email",
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
    public async Task EvaluateAsync_EmptyToolUsage_WhenExpected_ReturnsFail()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "RequiredTool" });
        var toolUsage = new ToolUsageReport(); // Empty, no calls
        
        var context = new EvaluationContext
        {
            Input = "Do something",
            Output = "Done",
            ToolUsage = toolUsage
        };
        
        // Act
        var result = await metric.EvaluateAsync(context);
        
        // Assert
        Assert.False(result.Passed);
    }
    
    [Fact]
    public void Constructor_NullExpectedTools_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new ToolSelectionMetric(null!));
    }
    
    [Fact]
    public async Task Properties_ReturnsCorrectMetadata()
    {
        // Arrange
        var metric = new ToolSelectionMetric(new[] { "Tool" });
        
        // Assert
        Assert.Equal("ToolSelection", metric.Name);
        Assert.True(metric.RequiresToolUsage);
    }
    
    // Helper to create tool usage report
    private static ToolUsageReport CreateToolUsage(params string[] toolNames)
    {
        var report = new ToolUsageReport();
        var order = 1;
        foreach (var name in toolNames)
        {
            report.AddCall(new ToolCallRecord
            {
                Name = name,
                CallId = Guid.NewGuid().ToString(),
                Order = order++
            });
        }
        return report;
    }
}
