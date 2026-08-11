// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.RedTeam.Reporting;
using Xunit;

namespace AgentEval.Tests.RedTeam.Reporting;

/// <summary>
/// S3 — Wilson score intervals for per-class recall reporting.
/// </summary>
/// <remarks>
/// The oracles below are NOT computed by us. They are the values published by an independent implementation
/// in <c>microsoft/agent-governance-toolkit</c>'s prompt-injection benchmark artifact
/// (<c>benchmarks/prompt-injection/artifacts/rules-baseline-smoke-metrics.json</c>, read 2026-08-08), which
/// reports <c>benign_fp_rate_wilson_95</c> per benign subclass. Validating against a third party's numbers
/// rather than our own arithmetic is the point: a self-checked statistic proves only that we are consistent.
/// </remarks>
public sealed class WilsonIntervalTests
{
    // Tolerance is well inside the published precision; these should agree to ~1e-12 if the formula matches.
    private const double Tolerance = 1e-9;

    [Theory]
    // successes, total, expected lower, expected upper — all from the AGT artifact.
    [InlineData(0, 10, 0.0, 0.2775327998628892)]     // benign_obfuscation_control, benign_tool_use
    [InlineData(2, 10, 0.05668215145437522, 0.5098375284633582)]  // benign_security_discussion (20% FP)
    [InlineData(0, 30, 0.0, 0.11351339317396876)]    // benign_compact_obfuscation_control
    public void Compute_MatchesAnIndependentImplementation(int successes, int total, double lower, double upper)
    {
        var interval = WilsonInterval.Compute(successes, total);

        Assert.Equal((double)successes / total, interval.Estimate, Tolerance);
        Assert.Equal(lower, interval.Lower, Tolerance);
        Assert.Equal(upper, interval.Upper, Tolerance);
    }

    [Fact]
    public void ZeroSuccesses_StillCarriesAnUpperBound_SoSmallSamplesCannotReadAsCertainty()
    {
        // The honesty case. "0/8 homoglyph attacks got through" must not render as a flat 0% — the true rate
        // could be a third. This is the whole reason per-class recall is reported with an interval.
        var interval = WilsonInterval.Compute(0, 8);

        Assert.Equal(0d, interval.Estimate);
        Assert.True(interval.Upper > 0.3, $"expected a wide upper bound for 0/8, got {interval.Upper}");
        Assert.True(interval.IsMeasured);
    }

    [Fact]
    public void EmptySample_IsNotMeasured_RatherThanZeroPercent()
    {
        // A class that was never exercised is a COVERAGE GAP. Reporting it as 0% would be the
        // fabricated-pass failure mode this project exists to prevent.
        var interval = WilsonInterval.Compute(0, 0);

        Assert.False(interval.IsMeasured);
        Assert.Equal("not measured (0 probes)", interval.ToString());
    }

    [Fact]
    public void FullSample_IsBoundedAtOne()
    {
        var interval = WilsonInterval.Compute(10, 10);

        Assert.Equal(1d, interval.Estimate);
        // Mathematically the upper bound at p=1 is exactly 1; in double precision it lands at 1 - 1.1e-16,
        // so this is asserted with tolerance rather than by exact equality.
        Assert.Equal(1d, interval.Upper, Tolerance);
        Assert.True(interval.Lower < 1d, "a 10/10 result should still carry a lower bound below 1");
    }

    [Fact]
    public void SuccessesAboveTotal_IsRejected()
    {
        // Fail closed on impossible input rather than emitting a nonsense interval.
        Assert.Throws<ArgumentOutOfRangeException>(() => WilsonInterval.Compute(5, 3));
    }
}
