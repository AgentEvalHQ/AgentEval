// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class StochasticProgressTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        // Arrange
        var testResult = new TestResult
        {
            TestName = "Test",
            Passed = true,
            Score = 100
        };
        var elapsed = TimeSpan.FromSeconds(5);
        var remaining = TimeSpan.FromSeconds(10);
        
        // Act
        var progress = new StochasticProgress(
            CurrentRun: 3,
            TotalRuns: 10,
            LastResult: testResult,
            Elapsed: elapsed,
            EstimatedRemaining: remaining);
        
        // Assert
        Assert.Equal(3, progress.CurrentRun);
        Assert.Equal(10, progress.TotalRuns);
        Assert.Same(testResult, progress.LastResult);
        Assert.Equal(elapsed, progress.Elapsed);
        Assert.Equal(remaining, progress.EstimatedRemaining);
    }
    
    [Fact]
    public void LastResult_CanBeNull()
    {
        // Act
        var progress = new StochasticProgress(
            CurrentRun: 1,
            TotalRuns: 5,
            LastResult: null,
            Elapsed: TimeSpan.Zero,
            EstimatedRemaining: null);
        
        // Assert
        Assert.Null(progress.LastResult);
    }
    
    [Fact]
    public void EstimatedRemaining_CanBeNull()
    {
        // Act
        var progress = new StochasticProgress(
            CurrentRun: 1,
            TotalRuns: 5,
            LastResult: null,
            Elapsed: TimeSpan.Zero,
            EstimatedRemaining: null);
        
        // Assert
        Assert.Null(progress.EstimatedRemaining);
    }
    
    [Fact]
    public void RecordEquality_WorksCorrectly()
    {
        // Arrange
        var progress1 = new StochasticProgress(
            CurrentRun: 2,
            TotalRuns: 5,
            LastResult: null,
            Elapsed: TimeSpan.FromSeconds(3),
            EstimatedRemaining: TimeSpan.FromSeconds(7));
        
        var progress2 = new StochasticProgress(
            CurrentRun: 2,
            TotalRuns: 5,
            LastResult: null,
            Elapsed: TimeSpan.FromSeconds(3),
            EstimatedRemaining: TimeSpan.FromSeconds(7));
        
        // Assert
        Assert.Equal(progress1, progress2);
    }
    
    [Fact]
    public void ToString_ContainsRelevantInfo()
    {
        // Arrange
        var progress = new StochasticProgress(
            CurrentRun: 2,
            TotalRuns: 5,
            LastResult: null,
            Elapsed: TimeSpan.FromSeconds(3),
            EstimatedRemaining: TimeSpan.FromSeconds(7));
        
        // Act
        var str = progress.ToString();
        
        // Assert
        Assert.Contains("2", str);
        Assert.Contains("5", str);
    }
}
