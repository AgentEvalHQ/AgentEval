// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting;
using Xunit;

namespace AgentEval.Tests.RedTeam.Reporting;

/// <summary>
/// S3b — per-bypass-class recall slicing. The point of the feature is that a per-class blind spot cannot hide
/// inside an aggregate, so the tests are written around exactly that scenario.
/// </summary>
public sealed class BypassClassBreakdownTests
{
    private static ProbeResult Probe(string id, EvaluationOutcome outcome) => new()
    {
        ProbeId = id,
        Prompt = "p",
        Response = "r",
        Outcome = outcome,
        Reason = "reason",
    };

    [Fact]
    public void ClassIsReadFromTheTransformSuffix_IncludingChains()
    {
        var breakdown = BypassClassBreakdown.FromResults(
        [
            Probe("PI-001", EvaluationOutcome.Resisted),
            Probe("PI-001+base64", EvaluationOutcome.Resisted),
            Probe("PI-001+base64+rot13", EvaluationOutcome.Resisted),
        ]);

        var classes = breakdown.Select(b => b.BypassClass).ToList();
        Assert.Contains(BypassClassBreakdown.Untransformed, classes);
        Assert.Contains("base64", classes);
        Assert.Contains("base64>rot13", classes);   // chain rendered in apply order
    }

    [Fact]
    public void APerClassBlindSpot_IsVisible_WhereTheAggregateHidesIt()
    {
        // THE motivating case. Aggregate success is 2/12 ≈ 17% — unremarkable. But every homoglyph probe got
        // through, which is what a reader needs to see and what a blended number destroys.
        var results = new List<ProbeResult>();
        for (var i = 0; i < 10; i++)
        {
            results.Add(Probe($"PI-{i:D3}+base64", EvaluationOutcome.Resisted));
        }
        results.Add(Probe("PI-100+homoglyph", EvaluationOutcome.Succeeded));
        results.Add(Probe("PI-101+homoglyph", EvaluationOutcome.Succeeded));

        var breakdown = BypassClassBreakdown.FromResults(results);

        var homoglyph = Assert.Single(breakdown, b => b.BypassClass == "homoglyph");
        var base64 = Assert.Single(breakdown, b => b.BypassClass == "base64");

        Assert.Equal(1d, homoglyph.SuccessRate.Estimate);   // 2/2 got through
        Assert.Equal(0d, base64.SuccessRate.Estimate);      // 0/10 got through
        // Worst class sorts first so the blind spot leads the table.
        Assert.Equal("homoglyph", breakdown[0].BypassClass);
    }

    [Fact]
    public void InconclusiveProbes_AreExcludedFromTheRate_NotCountedAsResisted()
    {
        // Conclusive-only scoring. Counting the 3 inconclusive probes as resisted would report 1/4 = 25%
        // instead of the honest 1/1 = 100% over what was actually scored.
        var breakdown = BypassClassBreakdown.FromResults(
        [
            Probe("PI-001+rot13", EvaluationOutcome.Succeeded),
            Probe("PI-002+rot13", EvaluationOutcome.Inconclusive),
            Probe("PI-003+rot13", EvaluationOutcome.Inconclusive),
            Probe("PI-004+rot13", EvaluationOutcome.Inconclusive),
        ]);

        var rot13 = Assert.Single(breakdown);
        Assert.Equal(1, rot13.Succeeded);
        Assert.Equal(1, rot13.Conclusive);
        Assert.Equal(3, rot13.Inconclusive);
        Assert.Equal(1d, rot13.SuccessRate.Estimate);
    }

    [Fact]
    public void AClassWithNoConclusiveProbes_IsNotMeasured_RatherThanZeroPercent()
    {
        // A class that ran but was never scored is a coverage gap. Reporting 0% would claim a clean result
        // from zero evidence — the fabricated-pass failure mode.
        var breakdown = BypassClassBreakdown.FromResults(
        [
            Probe("PI-001+diacritics", EvaluationOutcome.Inconclusive),
            Probe("PI-002+diacritics", EvaluationOutcome.Inconclusive),
        ]);

        var diacritics = Assert.Single(breakdown);
        Assert.False(diacritics.IsMeasured);
        Assert.Contains("not measured", diacritics.ToString());
    }

    [Fact]
    public void SmallCleanClasses_CarryAWideInterval_SoTheyCannotReadAsProven()
    {
        var breakdown = BypassClassBreakdown.FromResults(
        [
            Probe("PI-001+leet", EvaluationOutcome.Resisted),
            Probe("PI-002+leet", EvaluationOutcome.Resisted),
        ]);

        var leet = Assert.Single(breakdown);
        Assert.Equal(0d, leet.SuccessRate.Estimate);
        Assert.True(leet.SuccessRate.Upper > 0.5,
            $"0/2 must not read as proven-safe; upper bound was {leet.SuccessRate.Upper}");
    }
}
