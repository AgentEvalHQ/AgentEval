// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class ModelComparisonOptionsTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var options = ModelComparisonOptions.Default;
        
        Assert.Equal(5, options.RunsPerModel);
        Assert.Null(options.ScoringWeights);
        Assert.True(options.EnableCostAnalysis);
        Assert.True(options.EnableStatistics);
        Assert.Equal(0.95, options.ConfidenceLevel);
        Assert.Equal(1, options.MaxParallelism);
        Assert.Null(options.DelayBetweenRuns);
    }
    
    [Fact]
    public void Quick_HasFewerRuns()
    {
        var options = ModelComparisonOptions.Quick;
        Assert.Equal(3, options.RunsPerModel);
    }
    
    [Fact]
    public void Thorough_HasMoreRuns()
    {
        var options = ModelComparisonOptions.Thorough;
        Assert.Equal(10, options.RunsPerModel);
    }
    
    [Fact]
    public void Validate_RunsPerModelZero_Throws()
    {
        var options = new ModelComparisonOptions(RunsPerModel: 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }
    
    [Fact]
    public void Validate_MaxParallelismZero_Throws()
    {
        var options = new ModelComparisonOptions(MaxParallelism: 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }
    
    [Fact]
    public void EffectiveScoringWeights_WhenNull_ReturnsDefault()
    {
        var options = new ModelComparisonOptions(ScoringWeights: null);
        var weights = options.EffectiveScoringWeights;
        
        Assert.Equal(ScoringWeights.Default, weights);
    }
    
    [Fact]
    public void EffectiveScoringWeights_WhenSet_ReturnsSetValue()
    {
        var customWeights = new ScoringWeights(0.5, 0.2, 0.2, 0.1);
        var options = new ModelComparisonOptions(ScoringWeights: customWeights);
        
        Assert.Equal(customWeights, options.EffectiveScoringWeights);
    }
}

public class ScoringWeightsTests
{
    [Fact]
    public void Default_SumsToOne()
    {
        var weights = ScoringWeights.Default;
        Assert.Equal(1.0, weights.TotalWeight, precision: 3);
    }
    
    [Fact]
    public void QualityFocused_HasHigherQualityWeight()
    {
        var weights = ScoringWeights.QualityFocused;
        
        Assert.Equal(0.6, weights.Quality);
        Assert.Equal(1.0, weights.TotalWeight, precision: 3);
    }
    
    [Fact]
    public void SpeedFocused_HasHigherSpeedWeight()
    {
        var weights = ScoringWeights.SpeedFocused;
        
        Assert.Equal(0.5, weights.Speed);
        Assert.Equal(1.0, weights.TotalWeight, precision: 3);
    }
    
    [Fact]
    public void CostFocused_HasHigherCostWeight()
    {
        var weights = ScoringWeights.CostFocused;
        
        Assert.Equal(0.5, weights.Cost);
        Assert.Equal(1.0, weights.TotalWeight, precision: 3);
    }
    
    [Fact]
    public void ReliabilityFocused_HasHigherReliabilityWeight()
    {
        var weights = ScoringWeights.ReliabilityFocused;
        
        Assert.Equal(0.4, weights.Reliability);
        Assert.Equal(1.0, weights.TotalWeight, precision: 3);
    }
    
    [Fact]
    public void Validate_WeightsSumToOne_DoesNotThrow()
    {
        var weights = new ScoringWeights(0.25, 0.25, 0.25, 0.25);
        var exception = Record.Exception(() => weights.Validate());
        Assert.Null(exception);
    }
    
    [Fact]
    public void Validate_WeightsNotSumToOne_Throws()
    {
        var weights = new ScoringWeights(0.5, 0.5, 0.5, 0.5);
        Assert.Throws<ArgumentException>(() => weights.Validate());
    }
    
    [Fact]
    public void Validate_NegativeWeight_Throws()
    {
        var weights = new ScoringWeights(-0.1, 0.4, 0.4, 0.3);
        Assert.Throws<ArgumentException>(() => weights.Validate());
    }
}
