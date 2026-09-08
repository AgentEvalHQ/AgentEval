// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Evals.Meta;

/// <summary>
/// ADR-030 Slice 1.3. A mean over 3 of 12 and a mean over 12 of 12 are different facts and must not
/// render identically. The rule has no enforcement mechanism today — the library ships HTML and PDF
/// renderers and no console renderer — so the denominator has to be attached to the number by the
/// type that knows it, or every consumer re-implements the rule and half forget.
/// </summary>
public class ObservationCensusTests
{
    [Fact]
    public void VoidAggregate_DoesNotRenderAsZero()
    {
        // The silent-{} shape: nothing measurable, so the aggregate is VOID — not perfect, not zero.
        // A 0.00 here is a verdict from an instrument that measured nothing.
        var census = new ObservationCensus(Measured: 0, NotApplicable: 3, NotMeasured: 9);

        Assert.True(census.Void);
        var rendered = census.RenderMean(0.0);

        Assert.DoesNotContain("0.00", rendered, StringComparison.Ordinal);
        Assert.Contains("VOID", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void VoidAggregate_IgnoresAnyMeanTheCallerSupplies()
    {
        // A caller cannot smuggle a placeholder past the void case by handing in a plausible number.
        var census = new ObservationCensus(0, 1, 0);

        Assert.Equal(census.RenderMean(0.0), census.RenderMean(1.0));
        Assert.Contains("VOID", census.RenderMean(0.97), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMean_PrintsItsDenominator()
    {
        var census = new ObservationCensus(Measured: 8, NotApplicable: 3, NotMeasured: 1);

        Assert.Equal("0.62 (8 of 12 measured, 3 n/a, 1 not measured)", census.RenderMean(0.62));
    }

    [Fact]
    public void CleanCensus_StillPrintsItsDenominator()
    {
        // "12 of 12 measured" is information, not noise: it is the difference between a mean nobody
        // has to caveat and one that has been quietly narrowed.
        var census = new ObservationCensus(12, 0, 0);

        Assert.Equal("0.75 (12 of 12 measured)", census.RenderMean(0.75));
    }

    [Fact]
    public void EmptyCensus_IsVoidToo_NotZero()
    {
        var census = new ObservationCensus(0, 0, 0);

        Assert.False(census.Void);   // Void is "measured nothing out of something"
        Assert.Equal(0, census.Total);
        Assert.Contains("VOID", census.RenderMean(0.0), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(12, 0, 0, true)]    // an inapplicability ledger that reads clean is usually one nothing writes to
    [InlineData(0, 12, 0, true)]    // everything n/a is the other extreme and just as suspicious
    [InlineData(8, 3, 1, false)]
    public void ExtremeAndUnexamined_FlagsBothDirections(int m, int na, int nm, bool expected)
    {
        Assert.Equal(expected, new ObservationCensus(m, na, nm).ExtremeAndUnexamined);
    }

    [Fact]
    public void ExtremeAndUnexamined_IsFalseForNoObservations()
    {
        // Nothing observed is not an extreme value; it is an absent instrument, and Total == 0 says so.
        Assert.False(new ObservationCensus(0, 0, 0).ExtremeAndUnexamined);
    }

    [Fact]
    public void RenderMean_IsCultureInvariant()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("0.62 (8 of 12 measured, 3 n/a, 1 not measured)",
                new ObservationCensus(8, 3, 1).RenderMean(0.62));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void CensusOverScores_MatchesTheHandCount()
    {
        var census = new[]
        {
            new EvalScore(0.9, null, "pass", true, null, "none", null),
            new EvalScore(0.1, null, "fail", false, null, "high", null),
            EvalScore.NotApplicable(),
            new EvalScore(0.0, null, "error", false, null, "none", null),
            new EvalScore(0.0, null, "skipped", false, null, "none", null),
        }.Census();

        Assert.Equal(new ObservationCensus(2, 1, 2), census);
        Assert.False(census.Void);
        Assert.Equal("0.50 (2 of 5 measured, 1 n/a, 2 not measured)", census.RenderMean(0.5));
    }
}
