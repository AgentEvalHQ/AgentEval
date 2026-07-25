// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Trust;
using Xunit;

namespace AgentEval.Tests.Trust;

/// <summary>Phase 3, P3-10 — the per-signal trust breakdown, band classification, and renderer.</summary>
public class TrustScoreBreakdownTests
{
    [Theory]
    [InlineData(95.0, TrustBand.High)]
    [InlineData(85.0, TrustBand.High)]
    [InlineData(70.0, TrustBand.Moderate)]
    [InlineData(45.0, TrustBand.Low)]
    [InlineData(10.0, TrustBand.Critical)]
    public void ClassifyBand_MapsScoreToBand(double score, TrustBand expected)
        => Assert.Equal(expected, TrustScoreBreakdownCalculator.ClassifyBand(score));

    [Fact]
    public void ClassifyBand_NullScore_IsUnknown()
        => Assert.Equal(TrustBand.Unknown, TrustScoreBreakdownCalculator.ClassifyBand(null));

    [Fact]
    public void ComputeBreakdown_ContributionsSumToComposite_ExcludedMarked()
    {
        var signals = new[]
        {
            new TrustSignal("gate:a", 1.0, 1.0),
            new TrustSignal("gate:b", 0.0, 1.0),
            new TrustSignal("eval:c", 0.5, 1.0, Label: "skipped"),   // excluded — must not drag the score
        };

        var breakdown = TrustScoreBreakdownCalculator.ComputeBreakdown(signals);

        Assert.Equal(50.0, breakdown.Result.Score);       // (1*1 + 0*1)/2 * 100
        Assert.Equal(TrustBand.Low, breakdown.Band);

        var included = breakdown.Contributions.Where(c => c.Included).ToList();
        Assert.Equal(2, included.Count);
        Assert.Equal(50.0, included.Sum(c => c.WeightedContribution), 3);   // included points reconstruct the composite

        var skipped = breakdown.Contributions.Single(c => c.Name == "eval:c");
        Assert.False(skipped.Included);
        Assert.Equal("skipped", skipped.ExclusionReason);
        Assert.Equal(0.0, skipped.WeightedContribution);
    }

    [Fact]
    public void Render_ShowsBand_ScoreAndPerSignalLines()
    {
        var breakdown = TrustScoreBreakdownCalculator.ComputeBreakdown(new[]
        {
            new TrustSignal("gate:allow-all", 1.0, 2.0),
            new TrustSignal("eval:c", 0.5, 1.0, Label: "error"),
        });

        var text = TrustScoreRenderer.Render(breakdown);

        Assert.Contains("Trust: 100/100  [High]", text);
        Assert.Contains("gate:allow-all", text);
        Assert.Contains("+100.0 pts", text);
        Assert.Contains("eval:c", text);
        Assert.Contains("excluded: error", text);
    }
}
