// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Trust;
using Xunit;

namespace AgentEval.Tests.Trust;

/// <summary>
/// Explainability &amp; Trust — the unified Trust Score must never let a skipped/errored signal drag the
/// composite down, mirroring the exclusion discipline already proven for the aggregation strategies
/// (WeightedSumAggregation et al.) this same session.
/// </summary>
public class TrustScoreCalculatorTests
{
    [Fact]
    public void Compute_AllMeasured_WeightedAverage()
    {
        var signals = new[]
        {
            new TrustSignal("gate-a", Score: 0.9, Weight: 1),
            new TrustSignal("gate-b", Score: 0.5, Weight: 1),
        };

        var result = TrustScoreCalculator.Compute(signals);

        Assert.Equal(70.0, result.Score!.Value, precision: 5);
        Assert.Equal(2, result.SignalsMeasured);
        Assert.Equal(2, result.SignalsTotal);
    }

    [Fact]
    public void Compute_HeavilyWeightedSkippedSignal_ExcludedFromMath_NotZeroScored()
    {
        // A heavily-weighted "skipped" signal with a deliberately terrible raw score (0.0) must NOT drag the
        // composite down — if it were included at face value, weight 5 would swamp weight 1 and the score
        // would collapse toward 0. Only the measured signal should count.
        var signals = new[]
        {
            new TrustSignal("real-gate", Score: 0.9, Weight: 1),
            new TrustSignal("infra-gap", Score: 0.0, Weight: 5, Label: "skipped"),
        };

        var result = TrustScoreCalculator.Compute(signals);

        Assert.Equal(90.0, result.Score!.Value, precision: 5);
        Assert.Equal(1, result.SignalsMeasured);
        Assert.Equal(2, result.SignalsTotal);
        Assert.Contains("infra-gap", result.Explanation);
        Assert.Contains("skipped", result.Explanation);
    }

    [Fact]
    public void Compute_ErrorSignal_ExcludedFromMath_SameAsSkipped()
    {
        var signals = new[]
        {
            new TrustSignal("real-gate", Score: 0.8, Weight: 1),
            new TrustSignal("transient-failure", Score: 0.0, Weight: 10, Label: "error"),
        };

        var result = TrustScoreCalculator.Compute(signals);

        Assert.Equal(80.0, result.Score!.Value, precision: 5);
        Assert.Equal(1, result.SignalsMeasured);
    }

    [Fact]
    public void Compute_AllSignalsExcluded_ReturnsNullScore_NotZero()
    {
        var signals = new[]
        {
            new TrustSignal("a", Score: 0.9, Weight: 1, Label: "skipped"),
            new TrustSignal("b", Score: 0.1, Weight: 1, Label: "error"),
        };

        var result = TrustScoreCalculator.Compute(signals);

        Assert.Null(result.Score);
        Assert.Equal(0, result.SignalsMeasured);
        Assert.Equal(2, result.SignalsTotal);
        Assert.Contains("0/2 measured", result.Explanation);
    }

    [Fact]
    public void Compute_EmptyList_ReturnsNullScore()
    {
        var result = TrustScoreCalculator.Compute(Array.Empty<TrustSignal>());

        Assert.Null(result.Score);
        Assert.Equal(0, result.SignalsMeasured);
        Assert.Equal(0, result.SignalsTotal);
    }

    [Fact]
    public void Compute_NonPositiveWeight_Excluded_NotDivideByZeroOrCountedAsMeasured()
    {
        var signals = new[]
        {
            new TrustSignal("zero-weight", Score: 1.0, Weight: 0),
            new TrustSignal("negative-weight", Score: 1.0, Weight: -1),
            new TrustSignal("real", Score: 0.6, Weight: 1),
        };

        var result = TrustScoreCalculator.Compute(signals);

        Assert.Equal(60.0, result.Score!.Value, precision: 5);
        Assert.Equal(1, result.SignalsMeasured);
    }

    [Fact]
    public void Compute_ScoreOutsideZeroOneRange_IsClamped()
    {
        var signals = new[] { new TrustSignal("over-reporting-gate", Score: 1.5, Weight: 1) };

        var result = TrustScoreCalculator.Compute(signals);

        Assert.Equal(100.0, result.Score!.Value, precision: 5);
    }

    [Fact]
    public void Compute_SingleMeasuredSignal_ScoreIsThatSignalScaledTo100()
    {
        var signals = new[] { new TrustSignal("only-gate", Score: 0.42, Weight: 3) };

        var result = TrustScoreCalculator.Compute(signals);

        Assert.Equal(42.0, result.Score!.Value, precision: 5);
        Assert.Equal(1, result.SignalsMeasured);
        Assert.DoesNotContain("excluded", result.Explanation);
    }
}
