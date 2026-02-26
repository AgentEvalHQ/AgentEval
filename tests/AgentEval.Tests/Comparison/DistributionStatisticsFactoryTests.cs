// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Comparison;
using Xunit;

namespace AgentEval.Tests.Comparison;

public class DistributionStatisticsFactoryTests
{
    #region Create(IReadOnlyList<double>)

    [Fact]
    public void Create_EmptyDoubleList_ReturnsZeroDistribution()
    {
        var result = DistributionStatisticsFactory.Create(Array.Empty<double>());

        Assert.Equal(0, result.Min);
        Assert.Equal(0, result.Max);
        Assert.Equal(0, result.Mean);
        Assert.Equal(0, result.Median);
        Assert.Equal(0, result.Percentile25);
        Assert.Equal(0, result.Percentile75);
        Assert.Equal(0, result.Percentile95);
        Assert.Equal(0, result.SampleSize);
    }

    [Fact]
    public void Create_SingleValue_ReturnsUniformDistribution()
    {
        var result = DistributionStatisticsFactory.Create(new[] { 42.0 });

        Assert.Equal(42.0, result.Min);
        Assert.Equal(42.0, result.Max);
        Assert.Equal(42.0, result.Mean);
        Assert.Equal(42.0, result.Median);
        Assert.Equal(1, result.SampleSize);
    }

    [Fact]
    public void Create_EvenCountValues_CalculatesCorrectMedian()
    {
        var result = DistributionStatisticsFactory.Create(new[] { 1.0, 2.0, 3.0, 4.0 });

        Assert.Equal(2.5, result.Median);
        Assert.Equal(1.0, result.Min);
        Assert.Equal(4.0, result.Max);
        Assert.Equal(2.5, result.Mean);
        Assert.Equal(4, result.SampleSize);
    }

    [Fact]
    public void Create_OddCountValues_CalculatesCorrectMedian()
    {
        var result = DistributionStatisticsFactory.Create(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 });

        Assert.Equal(3.0, result.Median);
        Assert.Equal(1.0, result.Min);
        Assert.Equal(5.0, result.Max);
        Assert.Equal(3.0, result.Mean);
        Assert.Equal(5, result.SampleSize);
    }

    [Fact]
    public void Create_UnsortedValues_ProducesCorrectStatistics()
    {
        var result = DistributionStatisticsFactory.Create(new[] { 5.0, 1.0, 3.0, 2.0, 4.0 });

        Assert.Equal(1.0, result.Min);
        Assert.Equal(5.0, result.Max);
        Assert.Equal(3.0, result.Mean);
        Assert.Equal(3.0, result.Median);
    }

    [Fact]
    public void Create_DoubleValues_CalculatesPercentiles()
    {
        // 10 values: 10, 20, 30, 40, 50, 60, 70, 80, 90, 100
        var values = Enumerable.Range(1, 10).Select(i => (double)(i * 10)).ToList();
        var result = DistributionStatisticsFactory.Create(values);

        Assert.Equal(10.0, result.Min);
        Assert.Equal(100.0, result.Max);
        Assert.Equal(55.0, result.Mean);
        Assert.Equal(10, result.SampleSize);
        // Percentiles use linear interpolation
        Assert.True(result.Percentile25 > 0);
        Assert.True(result.Percentile75 > result.Percentile25);
        Assert.True(result.Percentile95 > result.Percentile75);
    }

    #endregion

    #region Create(IReadOnlyList<TimeSpan>)

    [Fact]
    public void Create_TimeSpans_ConvertsToMilliseconds()
    {
        var values = new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200),
            TimeSpan.FromMilliseconds(300)
        };

        var result = DistributionStatisticsFactory.Create(values);

        Assert.Equal(100.0, result.Min);
        Assert.Equal(300.0, result.Max);
        Assert.Equal(200.0, result.Mean);
        Assert.Equal(3, result.SampleSize);
    }

    [Fact]
    public void Create_EmptyTimeSpanList_ReturnsZeroDistribution()
    {
        var result = DistributionStatisticsFactory.Create(Array.Empty<TimeSpan>());

        Assert.Equal(0, result.SampleSize);
        Assert.Equal(0, result.Mean);
    }

    #endregion

    #region Create(IReadOnlyList<int>)

    [Fact]
    public void Create_IntValues_ConvertsToDoubleCorrectly()
    {
        var result = DistributionStatisticsFactory.Create(new[] { 10, 20, 30 });

        Assert.Equal(10.0, result.Min);
        Assert.Equal(30.0, result.Max);
        Assert.Equal(20.0, result.Mean);
        Assert.Equal(3, result.SampleSize);
    }

    [Fact]
    public void Create_EmptyIntList_ReturnsZeroDistribution()
    {
        var result = DistributionStatisticsFactory.Create(Array.Empty<int>());

        Assert.Equal(0, result.SampleSize);
    }

    #endregion

    #region Create(IReadOnlyList<decimal>)

    [Fact]
    public void Create_DecimalValues_ConvertsToDoubleCorrectly()
    {
        var result = DistributionStatisticsFactory.Create(new[] { 1.5m, 2.5m, 3.5m });

        Assert.Equal(1.5, result.Min);
        Assert.Equal(3.5, result.Max);
        Assert.Equal(2.5, result.Mean);
        Assert.Equal(3, result.SampleSize);
    }

    [Fact]
    public void Create_EmptyDecimalList_ReturnsZeroDistribution()
    {
        var result = DistributionStatisticsFactory.Create(Array.Empty<decimal>());

        Assert.Equal(0, result.SampleSize);
    }

    #endregion

    #region Consistency with StatisticsCalculator

    [Fact]
    public void Create_ProducesSameResultsAsStatisticsCalculator()
    {
        var values = new[] { 10.0, 25.0, 50.0, 75.0, 90.0 };

        var factoryResult = DistributionStatisticsFactory.Create(values);
        var calcResult = StatisticsCalculator.CreateDistribution(values);

        Assert.Equal(calcResult.Min, factoryResult.Min);
        Assert.Equal(calcResult.Max, factoryResult.Max);
        Assert.Equal(calcResult.Mean, factoryResult.Mean);
        Assert.Equal(calcResult.Median, factoryResult.Median);
        Assert.Equal(calcResult.Percentile25, factoryResult.Percentile25);
        Assert.Equal(calcResult.Percentile75, factoryResult.Percentile75);
        Assert.Equal(calcResult.Percentile95, factoryResult.Percentile95);
        Assert.Equal(calcResult.SampleSize, factoryResult.SampleSize);
    }

    #endregion
}
