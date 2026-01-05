// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;
using AgentEval.Models;
using AgentEval.Assertions;

namespace AgentEval.Tests;

/// <summary>
/// Unit tests for PerformanceMetrics and PerformanceAssertions
/// </summary>
public class PerformanceMetricsTests
{
    [Fact]
    public void TotalDuration_CalculatesCorrectly()
    {
        var start = DateTimeOffset.UtcNow;
        var metrics = new PerformanceMetrics
        {
            StartTime = start,
            EndTime = start.AddSeconds(5)
        };
        
        Assert.Equal(5, metrics.TotalDuration.TotalSeconds, precision: 1);
    }
    
    [Fact]
    public void TotalTokens_SumsPromptAndCompletion()
    {
        var metrics = new PerformanceMetrics
        {
            PromptTokens = 100,
            CompletionTokens = 50
        };
        
        Assert.Equal(150, metrics.TotalTokens);
    }
    
    [Fact]
    public void AverageToolTime_CalculatesCorrectly()
    {
        var metrics = new PerformanceMetrics
        {
            ToolCallCount = 2,
            TotalToolTime = TimeSpan.FromMilliseconds(1000)
        };
        
        Assert.Equal(500, metrics.AverageToolTime.TotalMilliseconds, precision: 1);
    }
    
    [Fact]
    public void AverageToolTime_WithNoTools_ReturnsZero()
    {
        var metrics = new PerformanceMetrics
        {
            ToolCallCount = 0,
            TotalToolTime = TimeSpan.Zero
        };
        
        Assert.Equal(TimeSpan.Zero, metrics.AverageToolTime);
    }
    
    [Fact]
    public void ToString_IncludesDuration()
    {
        var start = DateTimeOffset.UtcNow;
        var metrics = new PerformanceMetrics
        {
            StartTime = start,
            EndTime = start.AddMilliseconds(1500)
        };
        
        var str = metrics.ToString();
        
        Assert.Contains("Duration", str);
        Assert.Contains("1500", str);
    }
    
    [Fact]
    public void ToString_IncludesTTFT_WhenAvailable()
    {
        var start = DateTimeOffset.UtcNow;
        var metrics = new PerformanceMetrics
        {
            StartTime = start,
            EndTime = start.AddSeconds(2),
            TimeToFirstToken = TimeSpan.FromMilliseconds(250)
        };
        
        var str = metrics.ToString();
        
        Assert.Contains("TTFT", str);
        Assert.Contains("250", str);
    }
}

/// <summary>
/// Unit tests for PerformanceAssertions fluent API
/// </summary>
public class PerformanceAssertionsTests
{
    [Fact]
    public void HaveTotalDurationUnder_WhenUnder_DoesNotThrow()
    {
        var start = DateTimeOffset.UtcNow;
        var metrics = new PerformanceMetrics
        {
            StartTime = start,
            EndTime = start.AddSeconds(1)
        };
        
        var exception = Record.Exception(() => 
            metrics.Should().HaveTotalDurationUnder(TimeSpan.FromSeconds(5)));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveTotalDurationUnder_WhenOver_ThrowsException()
    {
        var start = DateTimeOffset.UtcNow;
        var metrics = new PerformanceMetrics
        {
            StartTime = start,
            EndTime = start.AddSeconds(10)
        };
        
        var exception = Assert.Throws<PerformanceAssertionException>(() => 
            metrics.Should().HaveTotalDurationUnder(TimeSpan.FromSeconds(5)));
        
        Assert.Contains("5000", exception.Message); // 5 seconds in ms
    }
    
    [Fact]
    public void HaveTimeToFirstTokenUnder_WhenUnder_DoesNotThrow()
    {
        var metrics = new PerformanceMetrics
        {
            TimeToFirstToken = TimeSpan.FromMilliseconds(100)
        };
        
        var exception = Record.Exception(() => 
            metrics.Should().HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500)));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveTimeToFirstTokenUnder_WhenNotAvailable_ThrowsException()
    {
        var metrics = new PerformanceMetrics();
        
        var exception = Assert.Throws<PerformanceAssertionException>(() => 
            metrics.Should().HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500)));
        
        Assert.Contains("streaming", exception.Message.ToLower());
    }
    
    [Fact]
    public void HaveTokenCountUnder_WhenUnder_DoesNotThrow()
    {
        var metrics = new PerformanceMetrics
        {
            PromptTokens = 100,
            CompletionTokens = 50
        };
        
        var exception = Record.Exception(() => 
            metrics.Should().HaveTokenCountUnder(500));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveTokenCountUnder_WhenOver_ThrowsException()
    {
        var metrics = new PerformanceMetrics
        {
            PromptTokens = 1000,
            CompletionTokens = 500
        };
        
        var exception = Assert.Throws<PerformanceAssertionException>(() => 
            metrics.Should().HaveTokenCountUnder(500));
        
        Assert.Contains("500", exception.Message);
    }
    
    [Fact]
    public void HaveEstimatedCostUnder_WhenUnder_DoesNotThrow()
    {
        var metrics = new PerformanceMetrics
        {
            EstimatedCost = 0.05m
        };
        
        var exception = Record.Exception(() => 
            metrics.Should().HaveEstimatedCostUnder(0.10m));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void HaveEstimatedCostUnder_WhenOver_ThrowsException()
    {
        var metrics = new PerformanceMetrics
        {
            EstimatedCost = 0.50m
        };
        
        var exception = Assert.Throws<PerformanceAssertionException>(() => 
            metrics.Should().HaveEstimatedCostUnder(0.10m));
        
        Assert.Contains("$0.10", exception.Message);
    }
    
    [Fact]
    public void HaveAverageToolTimeUnder_WhenUnder_DoesNotThrow()
    {
        var metrics = new PerformanceMetrics
        {
            ToolCallCount = 2,
            TotalToolTime = TimeSpan.FromMilliseconds(200)
        };
        
        var exception = Record.Exception(() => 
            metrics.Should().HaveAverageToolTimeUnder(TimeSpan.FromMilliseconds(500)));
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void FluentChaining_WorksCorrectly()
    {
        var start = DateTimeOffset.UtcNow;
        var metrics = new PerformanceMetrics
        {
            StartTime = start,
            EndTime = start.AddSeconds(2),
            TimeToFirstToken = TimeSpan.FromMilliseconds(100),
            PromptTokens = 100,
            CompletionTokens = 50,
            EstimatedCost = 0.01m
        };
        
        var exception = Record.Exception(() => 
            metrics.Should()
                .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))
                .HaveTimeToFirstTokenUnder(TimeSpan.FromMilliseconds(500))
                .HaveTokenCountUnder(500)
                .HaveEstimatedCostUnder(0.10m));
        
        Assert.Null(exception);
    }
}
