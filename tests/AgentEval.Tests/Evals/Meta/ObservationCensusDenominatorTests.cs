// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals.Meta;

/// <summary>
/// Pins ADR-031 S2b's denominator rule: <c>applicableFraction</c> is
/// <c>Measured / Total</c>, and <b>never</b> <c>(Total − NotApplicable) / Total</c>.
/// </summary>
/// <remarks>
/// A separate file from <c>ObservationCensusTests</c> on purpose: these are new members and no
/// existing assertion in that file is touched.
/// </remarks>
public class ObservationCensusDenominatorTests
{
    [Fact]
    public void MeasuredFraction_CountsOnlyRealMeasurements()
    {
        var census = new ObservationCensus(Measured: 8, NotApplicable: 3, NotMeasured: 1);
        Assert.Equal(8 / 12.0, census.MeasuredFraction, 12);
    }

    [Fact]
    public void MeasuredFraction_IsNotThePooledForm_WhenAnInstrumentFailed()
    {
        // THE WHOLE POINT. 3 cases could not test the thing (a CORPUS finding) and 1 run did not
        // happen (an OPERATIONAL one). The pooled form counts the failed run as applicable.
        var census = new ObservationCensus(Measured: 8, NotApplicable: 3, NotMeasured: 1);

        Assert.Equal(8 / 12.0, census.MeasuredFraction, 12);
        Assert.Equal(9 / 12.0, census.PooledFractionDoNotReport, 12);
        Assert.NotEqual(census.MeasuredFraction, census.PooledFractionDoNotReport);

        // And the direction is the flattering one: pooling reports MORE coverage than there is.
        Assert.True(census.PooledFractionDoNotReport > census.MeasuredFraction);
    }

    [Fact]
    public void TheTwoFormsCoincide_WhenNothingFailedToRun()
    {
        // Why a consumer that has never had an instrument fail cannot tell it picked the wrong
        // denominator: with NotMeasured == 0 the two are identical, so a test built only on a
        // healthy run certifies both.
        var healthy = new ObservationCensus(Measured: 9, NotApplicable: 3, NotMeasured: 0);
        Assert.Equal(healthy.MeasuredFraction, healthy.PooledFractionDoNotReport, 12);
    }

    [Fact]
    public void MeasuredFraction_NoObservations_IsNaN_NotZero()
    {
        // A fraction of zero says "nothing was measurable"; NaN says "there was nothing to
        // measure". Rendering the second as the first is the silent-{} shape.
        Assert.True(double.IsNaN(new ObservationCensus(0, 0, 0).MeasuredFraction));
        Assert.True(double.IsNaN(new ObservationCensus(0, 0, 0).PooledFractionDoNotReport));
    }

    [Fact]
    public void MeasuredFraction_EverythingFailedToRun_IsZero()
    {
        var census = new ObservationCensus(Measured: 0, NotApplicable: 0, NotMeasured: 5);
        Assert.Equal(0.0, census.MeasuredFraction, 12);

        // The pooled form calls a run in which the instrument never ran 100 % applicable.
        Assert.Equal(1.0, census.PooledFractionDoNotReport, 12);
        Assert.True(census.Void);
    }

    [Theory]
    [InlineData(8, 3, 1, 8, true)]
    [InlineData(8, 3, 1, 9, false)]     // 9 > Measured, and (Total − NotApplicable) == 9 would pass
    [InlineData(8, 3, 1, 0, true)]
    public void MeetsMinimumApplicable_CountsMeasured_NotTheApplicablePool(
        int measured, int notApplicable, int notMeasured, int minimum, bool expected)
        => Assert.Equal(expected,
            new ObservationCensus(measured, notApplicable, notMeasured).MeetsMinimumApplicable(minimum));

    [Fact]
    public void MeetsMinimumApplicable_NegativeFloor_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new ObservationCensus(1, 0, 0).MeetsMinimumApplicable(-1));
}
