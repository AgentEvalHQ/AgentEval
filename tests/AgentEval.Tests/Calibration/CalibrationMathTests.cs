// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using Xunit;

namespace AgentEval.Tests.Calibration;

/// <summary>
/// Tests for the shared <see cref="CalibrationMath"/> (ARC-04) — the single statistics implementation now
/// used by both CalibratedJudge and CalibratedEvaluator.
/// </summary>
public class CalibrationMathTests
{
    [Theory]
    [InlineData(new[] { 1.0, 2.0, 3.0 }, 2.0)]       // odd count → middle
    [InlineData(new[] { 1.0, 2.0, 3.0, 4.0 }, 2.5)]  // even count → mean of middle two
    [InlineData(new[] { 3.0, 1.0, 2.0 }, 2.0)]       // unsorted input
    public void Median_Works(double[] scores, double expected)
        => Assert.Equal(expected, CalibrationMath.Median(scores), 6);

    [Fact]
    public void Median_Empty_IsZero() => Assert.Equal(0, CalibrationMath.Median([]));

    [Fact]
    public void StandardDeviation_SampleNMinus1()
    {
        // values {2,4,6}: mean 4, sumSq = 4+0+4 = 8, /(n-1=2) = 4, sqrt = 2.
        Assert.Equal(2.0, CalibrationMath.StandardDeviation([2.0, 4.0, 6.0]), 6);
        Assert.Equal(0.0, CalibrationMath.StandardDeviation([5.0])); // <2 → 0
    }

    [Fact]
    public void Agreement_PerfectAgreement_Is100()
    {
        Assert.Equal(100, CalibrationMath.Agreement([7.0, 7.0, 7.0], stdDev: 0), 6);
        Assert.Equal(100, CalibrationMath.Agreement([5.0], stdDev: 0)); // <2 → 100
    }

    [Fact]
    public void WeightedScore_UsesWeights_ElseMean()
    {
        var scores = new Dictionary<string, double> { ["a"] = 10, ["b"] = 20 };
        Assert.Equal(15, CalibrationMath.WeightedScore(scores, null), 6);            // no weights → mean
        var weights = new Dictionary<string, double> { ["a"] = 3, ["b"] = 1 };
        Assert.Equal((10 * 3 + 20 * 1) / 4.0, CalibrationMath.WeightedScore(scores, weights), 6);
    }

    [Fact]
    public void WithinConsensus_RespectsTolerance()
    {
        Assert.True(CalibrationMath.WithinConsensus([10.0, 12.0], consensusTolerance: 5));
        Assert.False(CalibrationMath.WithinConsensus([10.0, 20.0], consensusTolerance: 5));
        Assert.True(CalibrationMath.WithinConsensus([10.0], consensusTolerance: 0)); // <2 → true
    }

    [Theory]
    [InlineData(1, 0.95, 12.706)]
    [InlineData(10, 0.99, 3.169)]
    [InlineData(1, 0.90, 6.314)]
    [InlineData(5, 0.50, 1.5)] // below 0.90 → coarse fallback
    public void GetTValue_MatchesTable(int df, double conf, double expected)
        => Assert.Equal(expected, CalibrationMath.GetTValue(df, conf), 6);
}
