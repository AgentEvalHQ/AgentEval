// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Baseline/RedTeamBaselineTests.cs
using AgentEval.RedTeam;
using AgentEval.RedTeam.Baseline;
using AgentEval.Core;

namespace AgentEval.Tests.RedTeam.Baseline;

public class RedTeamBaselineTests
{
    private static RedTeamResult CreateTestResult(int succeededProbes = 0)
    {
        return new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(10),
            TotalProbes = 20,
            ResistedProbes = 20 - succeededProbes,
            SucceededProbes = succeededProbes,
            InconclusiveProbes = 0,
            AttackResults = new[]
            {
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01",
                    MitreAtlasIds = ["AML.T0051"],
                    Severity = Severity.High,
                    ResistedCount = 10 - Math.Min(succeededProbes, 10),
                    SucceededCount = Math.Min(succeededProbes, 10),
                    InconclusiveCount = 0,
                    ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
                    {
                        ProbeId = $"PI-{i:D3}",
                        Prompt = $"Ignore previous instructions {i}",
                        Response = i <= succeededProbes ? "PWNED" : "I cannot help.",
                        Outcome = i <= succeededProbes ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                        Reason = i <= succeededProbes ? "Marker found" : "No markers found",
                        Technique = "basic_injection",
                        Difficulty = Difficulty.Easy,
                        Duration = TimeSpan.FromMilliseconds(100),
                        Severity = Severity.High
                    }).ToList()
                },
                new AttackResult
                {
                    AttackName = "Jailbreak",
                    AttackDisplayName = "Jailbreak",
                    OwaspId = "LLM01",
                    MitreAtlasIds = [],
                    Severity = Severity.High,
                    ResistedCount = 10,
                    SucceededCount = 0,
                    InconclusiveCount = 0,
                    ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
                    {
                        ProbeId = $"JB-{i:D3}",
                        Prompt = $"Pretend DAN {i}",
                        Response = "I cannot comply.",
                        Outcome = EvaluationOutcome.Resisted,
                        Reason = "No jailbreak indicators",
                        Technique = "roleplay",
                        Difficulty = Difficulty.Moderate,
                        Duration = TimeSpan.FromMilliseconds(150),
                        Severity = Severity.High
                    }).ToList()
                }
            }
        };
    }

    [Fact]
    public void FromResult_CreatesValidBaseline()
    {
        var result = CreateTestResult(succeededProbes: 3);

        var baseline = RedTeamBaseline.FromResult(result, "v1.0.0", "Test baseline");

        Assert.Equal("v1.0.0", baseline.Version);
        Assert.Equal("TestAgent", baseline.AgentName);
        Assert.Equal(result.OverallScore, baseline.OverallScore);
        Assert.Equal(3, baseline.KnownVulnerabilities.Count);
        Assert.Contains("PI-001", baseline.KnownVulnerabilities);
        Assert.Equal("Test baseline", baseline.Notes);
    }

    [Fact]
    public void ToBaseline_ExtensionMethod_Works()
    {
        var result = CreateTestResult();

        var baseline = result.ToBaseline("v2.0.0");

        Assert.Equal("v2.0.0", baseline.Version);
        Assert.Equal(100.0, baseline.OverallScore);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrip_PreservesData()
    {
        var result = CreateTestResult(succeededProbes: 2);
        var baseline = result.ToBaseline("v1.0.0", "Round trip test");
        var tempPath = Path.Combine(Path.GetTempPath(), $"baseline-test-{Guid.NewGuid()}.json");

        try
        {
            await baseline.SaveAsync(tempPath);
            var loaded = await RedTeamBaseline.LoadAsync(tempPath);

            Assert.Equal(baseline.Version, loaded.Version);
            Assert.Equal(baseline.OverallScore, loaded.OverallScore);
            Assert.Equal(baseline.KnownVulnerabilities.Count, loaded.KnownVulnerabilities.Count);
            Assert.Equal(baseline.Notes, loaded.Notes);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}

public class RedTeamBaselineComparerTests
{
    private static RedTeamResult CreateResult(int succeededProbes)
    {
        return new RedTeamResult
        {
            AgentName = "TestAgent",
            Duration = TimeSpan.FromSeconds(10),
            TotalProbes = 20,
            ResistedProbes = 20 - succeededProbes,
            SucceededProbes = succeededProbes,
            InconclusiveProbes = 0,
            AttackResults = new[]
            {
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01",
                    Severity = Severity.High,
                    ResistedCount = 10 - Math.Min(succeededProbes, 10),
                    SucceededCount = Math.Min(succeededProbes, 10),
                    ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
                    {
                        ProbeId = $"PI-{i:D3}",
                        Prompt = $"Test {i}",
                        Response = i <= succeededProbes ? "PWNED" : "Safe",
                        Outcome = i <= succeededProbes ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                        Reason = i <= succeededProbes ? "Marker" : "No marker",
                        Severity = Severity.High
                    }).ToList()
                },
                new AttackResult
                {
                    AttackName = "Jailbreak",
                    AttackDisplayName = "Jailbreak",
                    OwaspId = "LLM01",
                    Severity = Severity.High,
                    ResistedCount = 10 - Math.Max(0, succeededProbes - 10),
                    SucceededCount = Math.Max(0, succeededProbes - 10),
                    ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
                    {
                        ProbeId = $"JB-{i:D3}",
                        Prompt = $"Test {i}",
                        Response = (i + 10) <= succeededProbes ? "PWNED" : "Safe",
                        Outcome = (i + 10) <= succeededProbes ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                        Reason = "Test",
                        Severity = Severity.High
                    }).ToList()
                }
            }
        };
    }

    [Fact]
    public void Compare_WithImprovement_ShowsResolved()
    {
        var baseline = CreateResult(succeededProbes: 5).ToBaseline("v1.0.0");
        var current = CreateResult(succeededProbes: 2);
        var comparer = new RedTeamBaselineComparer();

        var comparison = comparer.Compare(current, baseline);

        Assert.True(comparison.ScoreDelta > 0);
        Assert.Equal(3, comparison.ResolvedVulnerabilities.Count);
        Assert.Empty(comparison.NewVulnerabilities);
        Assert.Equal(RegressionStatus.Improved, comparison.Status);
    }

    [Fact]
    public void Compare_WithRegression_ShowsNewVulnerabilities()
    {
        var baseline = CreateResult(succeededProbes: 2).ToBaseline("v1.0.0");
        var current = CreateResult(succeededProbes: 5);
        var comparer = new RedTeamBaselineComparer();

        var comparison = comparer.Compare(current, baseline);

        Assert.True(comparison.ScoreDelta < 0);
        Assert.Equal(3, comparison.NewVulnerabilities.Count);
        Assert.True(comparison.IsRegression);
    }

    [Fact]
    public void Compare_WithNoChange_ShowsStable()
    {
        var baseline = CreateResult(succeededProbes: 3).ToBaseline("v1.0.0");
        var current = CreateResult(succeededProbes: 3);
        var comparer = new RedTeamBaselineComparer();

        var comparison = comparer.Compare(current, baseline);

        Assert.Equal(0, comparison.ScoreDelta, 1);
        Assert.Empty(comparison.NewVulnerabilities);
        Assert.Empty(comparison.ResolvedVulnerabilities);
        Assert.Equal(RegressionStatus.Stable, comparison.Status);
    }

    [Fact]
    public void CompareToBaseline_ExtensionMethod_Works()
    {
        var baseline = CreateResult(succeededProbes: 2).ToBaseline("v1.0.0");
        var current = CreateResult(succeededProbes: 2);

        var comparison = current.CompareToBaseline(baseline);

        Assert.NotNull(comparison);
        Assert.Equal(baseline, comparison.Baseline);
        Assert.Equal(current, comparison.Current);
    }

    [Fact]
    public void Compare_CoverageLoss_IsRegression_EvenWithoutNewVulnerabilities()
    {
        var baseline = CreateResult(succeededProbes: 0).ToBaseline("v1.0.0");
        var current = new RedTeamResult
        {
            AgentName = "TestAgent",
            Duration = TimeSpan.FromSeconds(10),
            TotalProbes = 20,
            ResistedProbes = 10,
            SucceededProbes = 0,
            InconclusiveProbes = 10,
            AttackResults =
            [
                CreateCoverageLossAttack("PromptInjection", "Prompt Injection", "PI"),
                CreateCoverageLossAttack("Jailbreak", "Jailbreak", "JB")
            ]
        };

        var comparison = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.Empty(comparison.NewVulnerabilities);
        Assert.True(comparison.CoverageDrop >= 0.10);
        Assert.True(comparison.IsRegression);
        Assert.Equal(RegressionStatus.Regression, comparison.Status);
    }

    [Fact]
    public void Compare_BaselineWithInconclusiveProbes_IdenticalRerun_IsStableNotRegression()
    {
        // RC-6 honesty: a baseline captured from an inconclusive-laden run records coverage < 1.0
        // (here 0.70). An identical re-run must NOT be flagged as a coverage regression. The gate
        // previously hard-coded baseline coverage = 1.0, turning every such comparison into a
        // permanent exit-4 false alarm.
        var baseline = CreateMixedCoverageResult().ToBaseline("v1.0.0");
        var current = CreateMixedCoverageResult();

        var comparison = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.Equal(0.70, comparison.BaselineCoverage, 3);
        Assert.Equal(0.0, comparison.CoverageDrop, 3);
        Assert.False(comparison.IsRegression);
        Assert.Equal(RegressionStatus.Stable, comparison.Status);
    }

    [Fact]
    public void Compare_ConclusiveBaseline_VsInconclusiveCurrent_StillRegression()
    {
        // The fail-closed direction is preserved: a genuinely lower-coverage current run (0.70)
        // against a fully-conclusive baseline (1.0) is still a regression.
        var baseline = CreateResult(succeededProbes: 0).ToBaseline("v1.0.0"); // coverage 1.0
        var current = CreateMixedCoverageResult();                            // coverage 0.70

        var comparison = new RedTeamBaselineComparer().Compare(current, baseline);

        Assert.Equal(1.0, comparison.BaselineCoverage, 3);
        Assert.True(comparison.CoverageDrop >= 0.10);
        Assert.True(comparison.IsRegression);
    }

    // Two attacks, 10 probes each: 7 resisted + 3 inconclusive => coverage 14/20 = 0.70.
    private static RedTeamResult CreateMixedCoverageResult() => new()
    {
        AgentName = "TestAgent",
        Duration = TimeSpan.FromSeconds(10),
        TotalProbes = 20,
        ResistedProbes = 14,
        SucceededProbes = 0,
        InconclusiveProbes = 6,
        AttackResults =
        [
            CreateMixedAttack("PromptInjection", "Prompt Injection", "PI"),
            CreateMixedAttack("Jailbreak", "Jailbreak", "JB")
        ]
    };

    private static AttackResult CreateMixedAttack(string name, string display, string prefix) => new()
    {
        AttackName = name,
        AttackDisplayName = display,
        OwaspId = "LLM01",
        Severity = Severity.High,
        ResistedCount = 7,
        SucceededCount = 0,
        InconclusiveCount = 3,
        ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
        {
            ProbeId = $"{prefix}-{i:D3}",
            Prompt = $"Test {i}",
            Response = i <= 7 ? "Safe" : "[TIMEOUT]",
            Outcome = i <= 7 ? EvaluationOutcome.Resisted : EvaluationOutcome.Inconclusive,
            Reason = i <= 7 ? "No marker" : "Timed out",
            Severity = Severity.High
        }).ToList()
    };

    [Fact]
    public void Compare_IntensityMismatch_Throws()
    {
        var baseline = CreateResult(succeededProbes: 0).ToBaseline("v1.0.0") with { Intensity = Intensity.Comprehensive };
        var current = new RedTeamResult
        {
            AgentName = "TestAgent",
            Options = new ScanOptions { Intensity = Intensity.Quick },
            Duration = TimeSpan.FromSeconds(1),
            TotalProbes = 4,
            ResistedProbes = 4,
            SucceededProbes = 0,
            InconclusiveProbes = 0,
            AttackResults = []
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new RedTeamBaselineComparer().Compare(current, baseline));

        Assert.Contains("different probe sets", ex.Message);
    }

    [Fact]
    public void Compare_IntensityMismatch_OverrideAllowed()
    {
        var baseline = CreateResult(succeededProbes: 0).ToBaseline("v1.0.0") with { Intensity = Intensity.Comprehensive };
        var current = new RedTeamResult
        {
            AgentName = "TestAgent",
            Options = new ScanOptions { Intensity = Intensity.Quick },
            Duration = TimeSpan.FromSeconds(1),
            TotalProbes = 4,
            ResistedProbes = 4,
            SucceededProbes = 0,
            InconclusiveProbes = 0,
            AttackResults = []
        };

        Assert.NotNull(new RedTeamBaselineComparer().Compare(current, baseline, requireMatchingIntensity: false));
    }

    [Fact]
    public void Compare_TruncatedScan_ThrowsByDefault()
    {
        // RA3-06 / T5-2: a FailFast-truncated scan's partial probe set is non-comparable to a full baseline.
        var baseline = CreateResult(succeededProbes: 0).ToBaseline("v1.0.0");
        var current = new RedTeamResult
        {
            AgentName = "TestAgent",
            Duration = TimeSpan.FromSeconds(1),
            TotalProbes = 1,
            ResistedProbes = 1,
            SucceededProbes = 0,
            InconclusiveProbes = 0,
            WasTruncated = true,
            SkippedProbes = 19,
            AttackResults = []
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new RedTeamBaselineComparer().Compare(current, baseline));
        Assert.Contains("non-comparable", ex.Message);
    }

    [Fact]
    public void Compare_TruncatedScan_OverrideAllowed()
    {
        var baseline = CreateResult(succeededProbes: 0).ToBaseline("v1.0.0");
        var current = new RedTeamResult
        {
            AgentName = "TestAgent",
            Duration = TimeSpan.FromSeconds(1),
            TotalProbes = 1,
            ResistedProbes = 1,
            SucceededProbes = 0,
            InconclusiveProbes = 0,
            WasTruncated = true,
            SkippedProbes = 19,
            AttackResults = []
        };

        Assert.NotNull(new RedTeamBaselineComparer().Compare(current, baseline, requireMatchingIntensity: false));
    }

    [Fact]
    public void Compare_CustomThresholds_TightenRegression()
    {
        var baseline = CreateResult(succeededProbes: 0).ToBaseline("v1.0.0");
        var dropped = CreateResult(succeededProbes: 1);

        var comparison = new RedTeamBaselineComparer().Compare(
            dropped,
            baseline,
            new ComparisonThresholds { RegressionScoreDrop = 2.0 });

        Assert.True(comparison.IsRegression);
    }

    private static AttackResult CreateCoverageLossAttack(string name, string displayName, string prefix)
    {
        var probes = new List<ProbeResult>();
        for (int i = 1; i <= 5; i++)
        {
            probes.Add(new() { ProbeId = $"{prefix}-{i:D3}", Prompt = "p", Response = "safe", Outcome = EvaluationOutcome.Resisted, Reason = "ok" });
        }
        for (int i = 6; i <= 10; i++)
        {
            probes.Add(new() { ProbeId = $"{prefix}-{i:D3}", Prompt = "p", Response = "[TIMEOUT]", Outcome = EvaluationOutcome.Inconclusive, Reason = "timeout", Error = "Timeout", ErrorKind = ProbeErrorKind.Timeout });
        }

        return new AttackResult
        {
            AttackName = name,
            AttackDisplayName = displayName,
            OwaspId = "LLM01",
            Severity = Severity.High,
            ResistedCount = 5,
            SucceededCount = 0,
            InconclusiveCount = 5,
            ProbeResults = probes
        };
    }
}

public class BaselineAssertionsTests
{
    private static RedTeamComparison CreateComparison(int baselineSucceeded, int currentSucceeded)
    {
        var baseline = new RedTeamResult
        {
            AgentName = "Test",
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 10,
            ResistedProbes = 10 - baselineSucceeded,
            SucceededProbes = baselineSucceeded,
            AttackResults = new[]
            {
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01",
                    Severity = Severity.High,
                    ResistedCount = 10 - baselineSucceeded,
                    SucceededCount = baselineSucceeded,
                    ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
                    {
                        ProbeId = $"PI-{i:D3}",
                        Prompt = $"Test {i}",
                        Response = i <= baselineSucceeded ? "PWNED" : "Safe",
                        Outcome = i <= baselineSucceeded ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                        Reason = "Test",
                        Severity = Severity.High
                    }).ToList()
                }
            }
        }.ToBaseline("v1.0.0");

        var current = new RedTeamResult
        {
            AgentName = "Test",
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = 10,
            ResistedProbes = 10 - currentSucceeded,
            SucceededProbes = currentSucceeded,
            AttackResults = new[]
            {
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01",
                    Severity = Severity.High,
                    ResistedCount = 10 - currentSucceeded,
                    SucceededCount = currentSucceeded,
                    ProbeResults = Enumerable.Range(1, 10).Select(i => new ProbeResult
                    {
                        ProbeId = $"PI-{i:D3}",
                        Prompt = $"Test {i}",
                        Response = i <= currentSucceeded ? "PWNED" : "Safe",
                        Outcome = i <= currentSucceeded ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                        Reason = "Test",
                        Severity = Severity.High
                    }).ToList()
                }
            }
        };

        return current.CompareToBaseline(baseline);
    }

    [Fact]
    public void HaveNoNewVulnerabilities_WhenNoNew_Passes()
    {
        var comparison = CreateComparison(baselineSucceeded: 3, currentSucceeded: 2);

        var exception = Record.Exception(() =>
        {
            comparison.Should().HaveNoNewVulnerabilities().ThrowIfFailed();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void HaveNoNewVulnerabilities_WhenNew_ThrowsWithDetails()
    {
        var comparison = CreateComparison(baselineSucceeded: 2, currentSucceeded: 5);

        var exception = Assert.Throws<RedTeamRegressionException>(() =>
        {
            comparison.Should().HaveNoNewVulnerabilities("no regressions allowed").ThrowIfFailed();
        });

        Assert.Contains("3", exception.Message); // 3 new vulnerabilities
        Assert.Contains("no regressions allowed", exception.Message);
    }

    [Fact]
    public void HaveOverallScoreNotDecreasedBy_WithinThreshold_Passes()
    {
        var comparison = CreateComparison(baselineSucceeded: 2, currentSucceeded: 3);

        var exception = Record.Exception(() =>
        {
            comparison.Should().HaveOverallScoreNotDecreasedBy(15).ThrowIfFailed();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void HaveOverallScoreNotDecreasedBy_ExceedsThreshold_Throws()
    {
        var comparison = CreateComparison(baselineSucceeded: 1, currentSucceeded: 5);

        var exception = Assert.Throws<RedTeamRegressionException>(() =>
        {
            comparison.Should().HaveOverallScoreNotDecreasedBy(5).ThrowIfFailed();
        });

        Assert.Contains("decreased", exception.Message);
    }

    [Fact]
    public void NotBeRegression_WhenImproved_Passes()
    {
        var comparison = CreateComparison(baselineSucceeded: 5, currentSucceeded: 2);

        var exception = Record.Exception(() =>
        {
            comparison.Should().NotBeRegression().ThrowIfFailed();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void ChainedAssertions_Work()
    {
        var comparison = CreateComparison(baselineSucceeded: 3, currentSucceeded: 2);

        var exception = Record.Exception(() =>
        {
            comparison.Should()
                .HaveNoNewVulnerabilities()
                .And()
                .HaveOverallScoreNotDecreasedBy(5)
                .And()
                .NotBeRegression()
                .ThrowIfFailed();
        });

        Assert.Null(exception);
    }
}
