// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting;
using Xunit;

namespace AgentEval.Tests.RedTeam.Reporting;

/// <summary>S3d — over-refusal measurement. A gate that blocks everything must not score perfectly.</summary>
public sealed class FalsePositiveRateTests
{
    private static AttackProbe Probe(IReadOnlyDictionary<string, object>? metadata) =>
        new() { Id = "P-1", Prompt = "p", Difficulty = Difficulty.Easy, Metadata = metadata };

    [Fact]
    public void NoBenignControls_IsNotMeasured_NotZeroPercent()
    {
        // THE honesty case, and the reason S3d was split out: our general 264-probe corpus has no benign
        // probes, so a 0% false-positive rate would be fabricated from an unasked question.
        var fp = FalsePositiveRate.Compute(flaggedBenign: 0, conclusiveBenign: 0);

        Assert.False(fp.IsMeasured);
        Assert.Contains("not measured", fp.ToString());
    }

    [Fact]
    public void FlaggedBenignProbes_ProduceARateWithAnInterval()
    {
        // Mirrors the reference benchmark's benign_security_discussion result: 2/10 = 20% FP.
        var fp = FalsePositiveRate.Compute(flaggedBenign: 2, conclusiveBenign: 10);

        Assert.True(fp.IsMeasured);
        Assert.Equal(0.2, fp.Rate.Estimate, 1e-9);
        Assert.Equal(200d, fp.PerThousand, 1e-9);
        Assert.True(fp.Rate.Upper > 0.5, "a 2/10 rate must carry a wide interval");
    }

    [Fact]
    public void PerThousandFraming_MakesASmallLookingRateLegible()
    {
        // 9.4% reads as fine; 94 per 1,000 benign prompts reads as unshippable. Same number.
        var fp = FalsePositiveRate.Compute(flaggedBenign: 16, conclusiveBenign: 170);

        Assert.Equal(94.1, fp.PerThousand, 0.1);
    }

    [Fact]
    public void UnmarkedProbes_DefaultToAttack_TheSafeDirection()
    {
        // Mistaking an attack for a benign control would inflate the FP denominator and flatter the result.
        Assert.Equal(ProbeExpectation.Attack, ProbeExpectation.Of(Probe(null)));
    }

    [Fact]
    public void BenignControlsAreRecognisedWhenMarked()
    {
        var benign = Probe(new Dictionary<string, object>
        {
            [ProbeExpectation.MetadataKey] = ProbeExpectation.BenignControl,
        });

        Assert.Equal(ProbeExpectation.BenignControl, ProbeExpectation.Of(benign));
    }
}
