// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Models/ScanOptionsTests.cs
#pragma warning disable CS0618 // Obsolete member usage (ScanOptions.Parallelism)
using AgentEval.RedTeam;

namespace AgentEval.Tests.RedTeam.Models;

/// <summary>
/// Tests for the ScanOptions class.
/// </summary>
public class ScanOptionsTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var options = ScanOptions.Default;

        Assert.Null(options.AttackTypes);
        Assert.Equal(Intensity.Moderate, options.Intensity);
        Assert.Equal(0, options.MaxProbesPerAttack);
        Assert.Equal(TimeSpan.FromSeconds(30), options.TimeoutPerProbe);
        Assert.Equal(TimeSpan.Zero, options.DelayBetweenProbes);
        Assert.Equal(1, options.Parallelism);
        Assert.False(options.FailFast);
        Assert.True(options.IncludeEvidence);
        Assert.Null(options.JudgeClient);
    }

    [Fact]
    public void Quick_HasReducedTimeout()
    {
        var options = ScanOptions.Quick;

        Assert.Equal(Intensity.Quick, options.Intensity);
        Assert.Equal(TimeSpan.FromSeconds(15), options.TimeoutPerProbe);
    }

    [Fact]
    public void Comprehensive_HasIncreasedTimeout()
    {
        var options = ScanOptions.Comprehensive;

        Assert.Equal(Intensity.Comprehensive, options.Intensity);
        Assert.Equal(TimeSpan.FromSeconds(60), options.TimeoutPerProbe);
    }

    [Fact]
    public void CanOverrideIndividualProperties()
    {
        var options = new ScanOptions
        {
            Intensity = Intensity.Quick,
            MaxProbesPerAttack = 5,
            Parallelism = 4,
            FailFast = true,
            IncludeEvidence = false
        };

        Assert.Equal(Intensity.Quick, options.Intensity);
        Assert.Equal(5, options.MaxProbesPerAttack);
        Assert.Equal(4, options.Parallelism);
        Assert.True(options.FailFast);
        Assert.False(options.IncludeEvidence);
    }

    [Fact]
    public void TimeoutPerProbe_DefaultIsReasonable()
    {
        var options = new ScanOptions();

        // 30 seconds is long enough for most LLM calls but short enough to fail fast
        Assert.True(options.TimeoutPerProbe >= TimeSpan.FromSeconds(10));
        Assert.True(options.TimeoutPerProbe <= TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Parallelism_DefaultIsSequential()
    {
        // Sequential by default makes debugging easier
        var options = new ScanOptions();
        Assert.Equal(1, options.Parallelism);
    }

    [Fact]
    public void IncludeEvidence_DefaultIsTrue()
    {
        // Evidence is valuable for debugging failures
        var options = new ScanOptions();
        Assert.True(options.IncludeEvidence);
    }
}
