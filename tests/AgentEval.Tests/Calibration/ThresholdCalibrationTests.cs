// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using Xunit;

namespace AgentEval.Tests.Calibration;

/// <summary>
/// <see cref="ThresholdCalibration"/>: AUROC, the cut choice, and accuracy at a cut chosen on one split and measured
/// on another.
/// </summary>
public class ThresholdCalibrationTests
{
    private static ScoredCase P(double score) => new(score, Positive: true);
    private static ScoredCase N(double score) => new(score, Positive: false);

    [Fact]
    public void Auroc_CountsATieAsHalf()
    {
        // Positives 0.9, 0.8, 0.4 against negatives 0.7, 0.3, 0.4: of the 9 pairs, 7 are ordered right and 1 is a tie.
        var cases = new[] { P(0.9), P(0.8), P(0.4), N(0.7), N(0.3), N(0.4) };

        Assert.Equal(7.5 / 9, ThresholdCalibration.Auroc(cases), precision: 12);
    }

    [Fact]
    public void Auroc_PerfectReversedAndUninformative()
    {
        Assert.Equal(1.0, ThresholdCalibration.Auroc([P(2), P(3), N(0), N(1)]));
        Assert.Equal(0.0, ThresholdCalibration.Auroc([P(0), P(1), N(2), N(3)]));
        Assert.Equal(0.5, ThresholdCalibration.Auroc([P(5), P(5), N(5), N(5)]));
    }

    [Fact]
    public void Auroc_WithOneClassOnly_IsUndefined_NotChance()
    {
        Assert.True(double.IsNaN(ThresholdCalibration.Auroc([P(1), P(2)])));
        Assert.True(double.IsNaN(ThresholdCalibration.Auroc([N(1)])));
        Assert.True(double.IsNaN(ThresholdCalibration.Auroc([])));
    }

    [Fact]
    public void ChooseCut_TakesTheMidpointBetweenTheClasses()
    {
        var cases = new[] { P(70), P(80), P(90), N(10), N(20), N(60) };

        Assert.Equal(65, ThresholdCalibration.ChooseCut(cases));
    }

    [Fact]
    public void ChooseCut_OnAnImbalancedSet_AccuracyCallsEverythingTheMajority_BalancedAccuracyDoesNot()
    {
        // Nine negatives scored 1..9 and one positive scored 5. Calling every case negative is 90% accurate, and no
        // cut beats it; balanced accuracy puts the cut just below the positive instead.
        var cases = Enumerable.Range(1, 9).Select(i => N(i)).Append(P(5)).ToList();

        Assert.Equal(double.PositiveInfinity, ThresholdCalibration.ChooseCut(cases, CutObjective.Accuracy));
        Assert.Equal(4.5, ThresholdCalibration.ChooseCut(cases, CutObjective.BalancedAccuracy));
    }

    [Fact]
    public void ChooseCut_WhenCutsTie_TakesTheMiddleOne()
    {
        // Positives at 10, 30, 50 and negatives at 20, 40. Accuracy by cut: -∞ 3/5, 15 2/5, 25 3/5, 35 2/5, 45 3/5,
        // +∞ 2/5. Three cuts tie at 3/5, and the middle one, 25, is taken rather than either extreme.
        var cases = new[] { P(10), N(20), P(30), N(40), P(50) };

        Assert.Equal(25, ThresholdCalibration.ChooseCut(cases));
    }

    [Fact]
    public void EvaluateHeldOut_ChoosesTheCutOnTraining_AndMeasuresItUnchangedOnHeldOut()
    {
        var training = new[] { P(70), P(80), P(90), N(10), N(20), N(60) };
        var heldOut = new[] { P(66), N(64), P(50) };   // the cut of 65 gets the first two right and the third wrong

        var result = ThresholdCalibration.EvaluateHeldOut(training, heldOut);

        Assert.Equal(65, result.Cut);
        Assert.Equal(6, result.TrainingCount);
        Assert.Equal(1.0, result.TrainingAccuracy);   // optimistic by construction: the cut was chosen on these cases
        Assert.Equal(1.0, result.TrainingAuroc);
        Assert.Equal(2, result.HeldOutAccuracy.Successes);
        Assert.Equal(3, result.HeldOutAccuracy.Total);
        Assert.True(result.HeldOutAccuracy.Lower < 2.0 / 3 && 2.0 / 3 < result.HeldOutAccuracy.Upper);
        Assert.Equal(0.5, result.HeldOutAuroc);   // 66 > 64, 50 < 64
    }

    [Fact]
    public void EvaluateHeldOut_WithNoHeldOutCases_IsNotMeasured()
    {
        var result = ThresholdCalibration.EvaluateHeldOut([P(70), N(10)], []);

        Assert.False(result.HeldOutAccuracy.IsMeasured);
        Assert.True(double.IsNaN(result.HeldOutAuroc));
    }

    [Fact]
    public void Split_IsStratified_Disjoint_AndReproducibleForASeed()
    {
        var cases = Enumerable.Range(0, 10).Select(i => P(i))
            .Concat(Enumerable.Range(0, 20).Select(i => N(100 + i)))
            .ToList();

        var (training, heldOut) = ThresholdCalibration.Split(cases, heldOutFraction: 0.3, seed: 42);
        var again = ThresholdCalibration.Split(cases, heldOutFraction: 0.3, seed: 42);

        Assert.Equal(3, heldOut.Count(c => c.Positive));
        Assert.Equal(6, heldOut.Count(c => !c.Positive));
        Assert.Equal(cases.Count, training.Count + heldOut.Count);
        Assert.Equal(cases.OrderBy(c => c.Score), training.Concat(heldOut).OrderBy(c => c.Score));
        Assert.Equal(heldOut, again.HeldOut);
        Assert.Equal(training, again.Training);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(double.NaN)]
    public void Split_RejectsAFractionOutsideTheOpenInterval(double fraction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThresholdCalibration.Split([P(1), N(0)], fraction, seed: 1));
    }

    [Fact]
    public void ANonFiniteScore_IsRejected_AndNoCasesHaveNoCut()
    {
        Assert.Throws<ArgumentException>(() => ThresholdCalibration.Auroc([P(double.NaN), N(0)]));
        Assert.Throws<ArgumentException>(() => ThresholdCalibration.ChooseCut([P(double.PositiveInfinity)]));
        Assert.Throws<ArgumentException>(() => ThresholdCalibration.ChooseCut([]));
    }
}
