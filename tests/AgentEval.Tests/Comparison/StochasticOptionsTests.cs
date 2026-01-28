// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class StochasticOptionsTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var options = StochasticOptions.Default;
        
        Assert.Equal(10, options.Runs);
        Assert.Equal(0.8, options.SuccessRateThreshold);
        Assert.Null(options.Seed);
        Assert.Equal(1, options.MaxParallelism);
        Assert.Null(options.DelayBetweenRuns);
        Assert.True(options.EnableStatisticalAnalysis);
        Assert.Equal(0.95, options.ConfidenceLevel);
    }
    
    [Fact]
    public void Quick_HasFewerRuns()
    {
        var options = StochasticOptions.Quick;
        
        Assert.Equal(5, options.Runs);
        Assert.Equal(0.7, options.SuccessRateThreshold);
    }
    
    [Fact]
    public void Thorough_HasMoreRuns()
    {
        var options = StochasticOptions.Thorough;
        
        Assert.Equal(30, options.Runs);
        Assert.Equal(0.9, options.SuccessRateThreshold);
    }
    
    [Fact]
    public void CI_HasParallelism()
    {
        var options = StochasticOptions.CI;
        
        Assert.Equal(20, options.Runs);
        Assert.Equal(0.85, options.SuccessRateThreshold);
        Assert.Equal(3, options.MaxParallelism);
    }
    
    [Fact]
    public void Validate_RunsTooLow_Throws()
    {
        var options = new StochasticOptions(Runs: 2);
        
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }
    
    [Fact]
    public void Validate_SuccessRateOutOfRange_Throws()
    {
        var options1 = new StochasticOptions(SuccessRateThreshold: -0.1);
        var options2 = new StochasticOptions(SuccessRateThreshold: 1.1);
        
        Assert.Throws<ArgumentOutOfRangeException>(() => options1.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => options2.Validate());
    }
    
    [Fact]
    public void Validate_MaxParallelismZero_Throws()
    {
        var options = new StochasticOptions(MaxParallelism: 0);
        
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }
    
    [Fact]
    public void Validate_ConfidenceLevelInvalid_Throws()
    {
        var options1 = new StochasticOptions(ConfidenceLevel: 0);
        var options2 = new StochasticOptions(ConfidenceLevel: 1);
        
        Assert.Throws<ArgumentOutOfRangeException>(() => options1.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => options2.Validate());
    }
    
    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var options = new StochasticOptions(
            Runs: 10,
            SuccessRateThreshold: 0.8,
            Seed: 42,
            MaxParallelism: 4,
            DelayBetweenRuns: TimeSpan.FromMilliseconds(100),
            EnableStatisticalAnalysis: true,
            ConfidenceLevel: 0.95);
        
        var exception = Record.Exception(() => options.Validate());
        
        Assert.Null(exception);
    }
    
    [Fact]
    public void OnProgress_CanBeSet()
    {
        var progressReports = new List<StochasticProgress>();
        
        var options = new StochasticOptions(
            Runs: 5,
            OnProgress: progress => progressReports.Add(progress));
        
        Assert.NotNull(options.OnProgress);
    }
    
    [Fact]
    public void OnProgress_DefaultIsNull()
    {
        var options = StochasticOptions.Default;
        
        Assert.Null(options.OnProgress);
    }
}
