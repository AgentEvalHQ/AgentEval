// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/RedTeamAssertionsTests.cs
using AgentEval.RedTeam;

namespace AgentEval.Tests.RedTeam;

/// <summary>
/// Tests for RedTeamAssertions fluent API.
/// </summary>
public class RedTeamAssertionsTests
{
    // Helper to create minimal valid RedTeamResult
    private static RedTeamResult CreateResult(
        int totalProbes = 10,
        int resistedProbes = 10,
        int succeededProbes = 0,
        IReadOnlyList<AttackResult>? attackResults = null)
        => new()
        {
            AgentName = "TestAgent",
            TotalProbes = totalProbes,
            ResistedProbes = resistedProbes,
            SucceededProbes = succeededProbes,
            AttackResults = attackResults ?? []
        };

    // Helper to create minimal valid AttackResult
    private static AttackResult CreateAttack(
        string name = "TestAttack",
        string owaspId = "LLM01",
        int resistedCount = 0,
        int succeededCount = 0,
        Severity severity = Severity.Medium,
        IReadOnlyList<ProbeResult>? probeResults = null)
        => new()
        {
            AttackName = name,
            OwaspId = owaspId,
            ResistedCount = resistedCount,
            SucceededCount = succeededCount,
            Severity = severity,
            ProbeResults = probeResults ?? []
        };

    #region HavePassed Tests

    [Fact]
    public void HavePassed_WhenPassed_DoesNotThrow()
    {
        var result = CreateResult(totalProbes: 10, resistedProbes: 10, succeededProbes: 0);

        var act = () => result.Should().HavePassed();
        
        var ex = Record.Exception(act);
        Assert.Null(ex);
    }

    [Fact]
    public void HavePassed_WhenFailed_ThrowsWithDetails()
    {
        var result = CreateResult(
            totalProbes: 10,
            resistedProbes: 5,
            succeededProbes: 5,
            attackResults: [CreateAttack(name: "PromptInjection", succeededCount: 5)]);

        var ex = Assert.Throws<RedTeamAssertionException>(() => result.Should().HavePassed());
        
        Assert.Contains("vulnerabilities were found", ex.Message);
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public void HavePassed_WhenFailed_IncludesBecause()
    {
        var result = CreateResult(
            totalProbes: 10,
            resistedProbes: 5,
            succeededProbes: 5,
            attackResults: [CreateAttack(name: "Test", succeededCount: 5)]);

        var ex = Assert.Throws<RedTeamAssertionException>(() => 
            result.Should().HavePassed(because: "agent must be secure"));
        
        Assert.Contains("agent must be secure", ex.Message);
    }

    #endregion

    #region HaveMinimumScore Tests

    [Fact]
    public void HaveMinimumScore_AboveThreshold_DoesNotThrow()
    {
        var result = CreateResult(totalProbes: 10, resistedProbes: 9, succeededProbes: 1); // 90%

        var ex = Record.Exception(() => result.Should().HaveMinimumScore(80));
        Assert.Null(ex);
    }

    [Fact]
    public void HaveMinimumScore_BelowThreshold_Throws()
    {
        var result = CreateResult(totalProbes: 10, resistedProbes: 7, succeededProbes: 3); // 70%

        var ex = Assert.Throws<RedTeamAssertionException>(() => result.Should().HaveMinimumScore(80));
        
        Assert.Contains("80", ex.Message);
        Assert.Contains("70", ex.Message);
    }

    #endregion

    #region HaveASRBelow Tests

    [Fact]
    public void HaveASRBelow_BelowThreshold_DoesNotThrow()
    {
        var result = CreateResult(totalProbes: 100, resistedProbes: 97, succeededProbes: 3); // 3% ASR

        var ex = Record.Exception(() => result.Should().HaveASRBelow(0.05));
        Assert.Null(ex);
    }

    [Fact]
    public void HaveASRBelow_AboveThreshold_Throws()
    {
        var result = CreateResult(totalProbes: 100, resistedProbes: 90, succeededProbes: 10); // 10% ASR

        var ex = Assert.Throws<RedTeamAssertionException>(() => result.Should().HaveASRBelow(0.05));
        
        Assert.Contains("5", ex.Message); // 5% threshold
        Assert.Contains("10", ex.Message); // 10% actual
    }

    #endregion

    #region HaveResistedAttack Tests

    [Fact]
    public void HaveResistedAttack_WhenResisted_DoesNotThrow()
    {
        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "PromptInjection", resistedCount: 10, succeededCount: 0)
        ]);

        var ex = Record.Exception(() => result.Should().HaveResistedAttack("PromptInjection"));
        Assert.Null(ex);
    }

    [Fact]
    public void HaveResistedAttack_WhenCompromised_Throws()
    {
        var probes = new List<ProbeResult>
        {
            new() { ProbeId = "PI-001", Outcome = EvaluationOutcome.Succeeded, Prompt = "", Response = "", Reason = "" },
            new() { ProbeId = "PI-002", Outcome = EvaluationOutcome.Succeeded, Prompt = "", Response = "", Reason = "" }
        };
        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "PromptInjection", resistedCount: 8, succeededCount: 2, probeResults: probes)
        ]);

        var ex = Assert.Throws<RedTeamAssertionException>(() => 
            result.Should().HaveResistedAttack("PromptInjection"));
        
        Assert.Contains("2", ex.Message);
        Assert.Contains("PI-001", ex.Message);
    }

    [Fact]
    public void HaveResistedAttack_WhenAttackNotFound_Throws()
    {
        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "Jailbreak", succeededCount: 0)
        ]);

        var ex = Assert.Throws<RedTeamAssertionException>(() => 
            result.Should().HaveResistedAttack("PromptInjection"));
        
        Assert.Contains("not found", ex.Message);
        Assert.Contains("Jailbreak", ex.Message);
    }

    #endregion

    #region HaveNoHighSeverityCompromises Tests

    [Fact]
    public void HaveNoHighSeverityCompromises_WhenNone_DoesNotThrow()
    {
        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "High", severity: Severity.High, succeededCount: 0),
            CreateAttack(name: "Medium", severity: Severity.Medium, succeededCount: 2)
        ]);

        var ex = Record.Exception(() => result.Should().HaveNoHighSeverityCompromises());
        Assert.Null(ex);
    }

    [Fact]
    public void HaveNoHighSeverityCompromises_WhenPresent_Throws()
    {
        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "PromptInjection", severity: Severity.Critical, succeededCount: 1),
            CreateAttack(name: "Jailbreak", severity: Severity.High, succeededCount: 2)
        ]);

        var ex = Assert.Throws<RedTeamAssertionException>(() => 
            result.Should().HaveNoHighSeverityCompromises());
        
        Assert.Contains("Critical", ex.Message);
        Assert.Contains("PromptInjection", ex.Message);
    }

    #endregion

    #region HaveNoCompromisesFor Tests

    [Fact]
    public void HaveNoCompromisesFor_WhenNoCompromises_DoesNotThrow()
    {
        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "PromptInjection", owaspId: "LLM01", succeededCount: 0)
        ]);

        var ex = Record.Exception(() => result.Should().HaveNoCompromisesFor("LLM01"));
        Assert.Null(ex);
    }

    [Fact]
    public void HaveNoCompromisesFor_WhenCompromised_Throws()
    {
        var probes = new List<ProbeResult>
        {
            new() { ProbeId = "PI-001", Outcome = EvaluationOutcome.Succeeded, Prompt = "", Response = "", Reason = "" }
        };
        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "PromptInjection", owaspId: "LLM01", succeededCount: 2, probeResults: probes)
        ]);

        var ex = Assert.Throws<RedTeamAssertionException>(() => 
            result.Should().HaveNoCompromisesFor("LLM01"));
        
        Assert.Contains("LLM01", ex.Message);
        Assert.Contains("PI-001", ex.Message);
    }

    #endregion

    #region HaveAttackASRBelow Tests

    [Fact]
    public void HaveAttackASRBelow_BelowThreshold_DoesNotThrow()
    {
        var probes = Enumerable.Range(0, 100)
            .Select(i => new ProbeResult 
            { 
                ProbeId = $"P-{i}", 
                Outcome = i < 2 ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                Prompt = "", Response = "", Reason = ""
            }).ToList();

        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "PromptInjection", resistedCount: 98, succeededCount: 2, probeResults: probes)
        ]);

        var ex = Record.Exception(() => result.Should().HaveAttackASRBelow("PromptInjection", 0.05));
        Assert.Null(ex);
    }

    [Fact]
    public void HaveAttackASRBelow_AboveThreshold_Throws()
    {
        var probes = Enumerable.Range(0, 100)
            .Select(i => new ProbeResult 
            { 
                ProbeId = $"P-{i}", 
                Outcome = i < 20 ? EvaluationOutcome.Succeeded : EvaluationOutcome.Resisted,
                Prompt = "", Response = "", Reason = ""
            }).ToList();

        var result = CreateResult(attackResults:
        [
            CreateAttack(name: "PromptInjection", resistedCount: 80, succeededCount: 20, probeResults: probes)
        ]);

        var ex = Assert.Throws<RedTeamAssertionException>(() => 
            result.Should().HaveAttackASRBelow("PromptInjection", 0.05));
        
        Assert.Contains("PromptInjection", ex.Message);
        Assert.Contains("5", ex.Message); // Threshold
        Assert.Contains("20", ex.Message); // Actual
    }

    #endregion

    #region Chaining Tests

    [Fact]
    public void Chaining_WorksCorrectly()
    {
        var result = CreateResult(totalProbes: 10, resistedProbes: 10, succeededProbes: 0);

        var ex = Record.Exception(() => result.Should()
            .HavePassed()
            .And()
            .HaveMinimumScore(80)
            .And()
            .HaveNoHighSeverityCompromises());

        Assert.Null(ex);
    }

    [Fact]
    public void Chaining_FailsAtFirstFailure()
    {
        var result = CreateResult(
            totalProbes: 10,
            resistedProbes: 5,
            succeededProbes: 5,
            attackResults: [CreateAttack(name: "Test", succeededCount: 5)]);

        var ex = Assert.Throws<RedTeamAssertionException>(() => result.Should()
            .HavePassed()
            .And()
            .HaveMinimumScore(80));

        Assert.Contains("vulnerabilities were found", ex.Message);
    }

    #endregion

    #region Extension Method Tests

    [Fact]
    public void Should_ReturnsAssertions()
    {
        var result = CreateResult();

        var assertions = result.Should();

        Assert.NotNull(assertions);
        Assert.IsType<RedTeamAssertions>(assertions);
    }

    #endregion
}
