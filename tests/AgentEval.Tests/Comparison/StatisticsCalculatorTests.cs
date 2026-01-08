// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class StatisticsCalculatorTests
{
    [Fact]
    public void Mean_EmptyList_ReturnsZero()
    {
        var result = StatisticsCalculator.Mean(new List<double>());
        Assert.Equal(0, result);
    }
    
    [Fact]
    public void Mean_SingleValue_ReturnsThatValue()
    {
        var result = StatisticsCalculator.Mean(new List<double> { 42.0 });
        Assert.Equal(42.0, result);
    }
    
    [Fact]
    public void Mean_MultipleValues_ReturnsCorrectMean()
    {
        var result = StatisticsCalculator.Mean(new List<double> { 10, 20, 30 });
        Assert.Equal(20.0, result);
    }
    
    [Fact]
    public void Median_OddCount_ReturnsMiddleValue()
    {
        var result = StatisticsCalculator.Median(new List<double> { 1, 3, 2 });
        Assert.Equal(2.0, result);
    }
    
    [Fact]
    public void Median_EvenCount_ReturnsAverageOfMiddleTwo()
    {
        var result = StatisticsCalculator.Median(new List<double> { 1, 2, 3, 4 });
        Assert.Equal(2.5, result);
    }
    
    [Fact]
    public void Median_EmptyList_ReturnsZero()
    {
        var result = StatisticsCalculator.Median(new List<double>());
        Assert.Equal(0, result);
    }
    
    [Fact]
    public void StandardDeviation_SingleValue_ReturnsZero()
    {
        var result = StatisticsCalculator.StandardDeviation(new List<double> { 42 });
        Assert.Equal(0, result);
    }
    
    [Fact]
    public void StandardDeviation_IdenticalValues_ReturnsZero()
    {
        var result = StatisticsCalculator.StandardDeviation(new List<double> { 5, 5, 5, 5 });
        Assert.Equal(0, result);
    }
    
    [Fact]
    public void StandardDeviation_VariedValues_ReturnsPositive()
    {
        // Values: 2, 4, 4, 4, 5, 5, 7, 9
        // Mean: 5, Sample Std Dev: ~2.14
        var values = new List<double> { 2, 4, 4, 4, 5, 5, 7, 9 };
        var result = StatisticsCalculator.StandardDeviation(values);
        Assert.True(result > 2.0 && result < 2.2);
    }
    
    [Fact]
    public void Percentile_0_ReturnsMin()
    {
        var values = new List<double> { 10, 20, 30, 40, 50 };
        var result = StatisticsCalculator.Percentile(values, 0);
        Assert.Equal(10, result);
    }
    
    [Fact]
    public void Percentile_100_ReturnsMax()
    {
        var values = new List<double> { 10, 20, 30, 40, 50 };
        var result = StatisticsCalculator.Percentile(values, 100);
        Assert.Equal(50, result);
    }
    
    [Fact]
    public void Percentile_50_ReturnsMedian()
    {
        var values = new List<double> { 10, 20, 30, 40, 50 };
        var result = StatisticsCalculator.Percentile(values, 50);
        Assert.Equal(30, result);
    }
    
    [Fact]
    public void Percentile_InvalidValue_Throws()
    {
        var values = new List<double> { 1, 2, 3 };
        
        Assert.Throws<ArgumentOutOfRangeException>(() => StatisticsCalculator.Percentile(values, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => StatisticsCalculator.Percentile(values, 101));
    }
    
    [Fact]
    public void CalculatePassRate_AllPassed_ReturnsOne()
    {
        var results = new List<bool> { true, true, true };
        var result = StatisticsCalculator.CalculatePassRate(results);
        Assert.Equal(1.0, result);
    }
    
    [Fact]
    public void CalculatePassRate_NonePassed_ReturnsZero()
    {
        var results = new List<bool> { false, false, false };
        var result = StatisticsCalculator.CalculatePassRate(results);
        Assert.Equal(0.0, result);
    }
    
    [Fact]
    public void CalculatePassRate_Mixed_ReturnsCorrectRate()
    {
        var results = new List<bool> { true, true, false, false };
        var result = StatisticsCalculator.CalculatePassRate(results);
        Assert.Equal(0.5, result);
    }
    
    [Fact]
    public void CalculateConfidenceInterval_ReturnsValidInterval()
    {
        var values = new List<double> { 80, 85, 90, 82, 88 };
        var result = StatisticsCalculator.CalculateConfidenceInterval(values, 0.95);
        
        Assert.True(result.Lower < result.Upper);
        Assert.Equal(0.95, result.Level);
        Assert.True(result.Lower < StatisticsCalculator.Mean(values));
        Assert.True(result.Upper > StatisticsCalculator.Mean(values));
    }
    
    [Fact]
    public void CreateStatistics_ReturnsCompleteStatistics()
    {
        var scores = new List<int> { 70, 80, 90, 85, 75 };
        var passResults = new List<bool> { true, true, true, true, false };
        
        var stats = StatisticsCalculator.CreateStatistics(scores, passResults);
        
        Assert.Equal(0.8, stats.PassRate);
        Assert.Equal(80, stats.MeanScore);
        Assert.Equal(80, stats.MedianScore);
        Assert.Equal(70, stats.MinScore);
        Assert.Equal(90, stats.MaxScore);
        Assert.Equal(5, stats.SampleSize);
        Assert.NotNull(stats.ConfidenceInterval);
    }
}
