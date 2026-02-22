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
