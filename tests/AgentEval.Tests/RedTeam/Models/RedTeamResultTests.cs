// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
// tests/AgentEval.Tests/RedTeam/Models/RedTeamResultTests.cs
using AgentEval.RedTeam;

namespace AgentEval.Tests.RedTeam.Models;

/// <summary>
/// Tests for the RedTeamResult class.
/// </summary>
public class RedTeamResultTests
{
    private static ProbeResult CreateProbeResult(EvaluationOutcome outcome, string probeId = "p1")
        => new()
        {
            ProbeId = probeId,
            Prompt = "test",
            Response = "response",
            Outcome = outcome,
            Reason = "test reason"
        };

    private static AttackResult CreateAttackResult(
        string name,
        int resisted,
        int succeeded,
        int inconclusive)
    {
        var probes = new List<ProbeResult>();
        for (int i = 0; i < resisted; i++)
            probes.Add(CreateProbeResult(EvaluationOutcome.Resisted, $"r{i}"));
        for (int i = 0; i < succeeded; i++)
            probes.Add(CreateProbeResult(EvaluationOutcome.Succeeded, $"s{i}"));
        for (int i = 0; i < inconclusive; i++)
            probes.Add(CreateProbeResult(EvaluationOutcome.Inconclusive, $"i{i}"));

        return new AttackResult
        {
            AttackName = name,
            OwaspId = "LLM01",
            ProbeResults = probes,
            ResistedCount = resisted,
            SucceededCount = succeeded,
            InconclusiveCount = inconclusive
        };
    }

    [Fact]
    public void RedTeamResult_AllResisted_HasPassVerdict()
    {
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            AttackResults = [CreateAttackResult("Attack1", 10, 0, 0)],
            TotalProbes = 10,
            ResistedProbes = 10,
            SucceededProbes = 0,
            InconclusiveProbes = 0
        };

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.True(result.Passed);
        Assert.Equal(100.0, result.OverallScore);
        Assert.Equal(0.0, result.AttackSuccessRate);
    }

    [Fact]
    public void RedTeamResult_AllSucceeded_HasFailVerdict()
    {
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            AttackResults = [CreateAttackResult("Attack1", 0, 10, 0)],
            TotalProbes = 10,
            ResistedProbes = 0,
            SucceededProbes = 10,
            InconclusiveProbes = 0
        };

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.False(result.Passed);
        Assert.Equal(0.0, result.OverallScore);
        Assert.Equal(1.0, result.AttackSuccessRate);
    }

    [Fact]
    public void RedTeamResult_HighResistance_HasPartialPassVerdict()
    {
        // 85% resisted = PartialPass (threshold is 80%)
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            AttackResults = [CreateAttackResult("Attack1", 85, 15, 0)],
            TotalProbes = 100,
            ResistedProbes = 85,
            SucceededProbes = 15,
            InconclusiveProbes = 0
        };

        Assert.Equal(Verdict.PartialPass, result.Verdict);
        Assert.False(result.Passed);
        Assert.Equal(85.0, result.OverallScore);
    }

    [Fact]
    public void RedTeamResult_LowResistance_HasFailVerdict()
    {
        // 50% resisted = Fail (below 80% threshold)
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            AttackResults = [CreateAttackResult("Attack1", 50, 50, 0)],
            TotalProbes = 100,
            ResistedProbes = 50,
            SucceededProbes = 50,
            InconclusiveProbes = 0
        };

        Assert.Equal(Verdict.Fail, result.Verdict);
        Assert.Equal(50.0, result.OverallScore);
    }

    [Fact]
    public void RedTeamResult_EmptyResults_HasPassVerdict()
    {
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            AttackResults = [],
            TotalProbes = 0,
            ResistedProbes = 0,
            SucceededProbes = 0,
            InconclusiveProbes = 0
        };

        Assert.Equal(Verdict.Pass, result.Verdict);
        Assert.Equal(100.0, result.OverallScore);
        Assert.Equal(0.0, result.AttackSuccessRate);
    }

    [Fact]
    public void RedTeamResult_FailedAttacks_ReturnsCorrectAttacks()
    {
        var attack1 = CreateAttackResult("Attack1", 10, 0, 0);  // Passed
        var attack2 = CreateAttackResult("Attack2", 5, 5, 0);   // Failed
        var attack3 = CreateAttackResult("Attack3", 0, 10, 0);  // Failed

        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            AttackResults = [attack1, attack2, attack3],
            TotalProbes = 30,
            ResistedProbes = 15,
            SucceededProbes = 15,
            InconclusiveProbes = 0
        };

        var failed = result.FailedAttacks.ToList();
        Assert.Equal(2, failed.Count);
        Assert.Contains(failed, a => a.AttackName == "Attack2");
        Assert.Contains(failed, a => a.AttackName == "Attack3");
    }

    [Fact]
    public void RedTeamResult_Summary_ContainsKeyInfo()
    {
        var result = new RedTeamResult
        {
            AgentName = "TestAgent",
            AttackResults = [CreateAttackResult("Attack1", 8, 2, 0)],
            TotalProbes = 10,
            ResistedProbes = 8,
            SucceededProbes = 2,
            InconclusiveProbes = 0
        };

        Assert.Contains("PartialPass", result.Summary);
        Assert.Contains("2/10", result.Summary);
        Assert.Contains("80", result.Summary);
    }
}

/// <summary>
/// Tests for the AttackResult class.
/// </summary>
public class AttackResultTests
{
    [Fact]
    public void AttackResult_Passed_WhenNoSucceededProbes()
    {
        var result = new AttackResult
        {
            AttackName = "PromptInjection",
            OwaspId = "LLM01",
            ProbeResults = [],
            ResistedCount = 10,
            SucceededCount = 0,
            InconclusiveCount = 0
        };

        Assert.True(result.Passed);
        Assert.Equal(0.0, result.AttackSuccessRate);
    }

    [Fact]
    public void AttackResult_Failed_WhenHasSucceededProbes()
    {
        var probes = new List<ProbeResult>();
        // Add 9 resisted probes
        for (int i = 0; i < 9; i++)
            probes.Add(new() { ProbeId = $"r{i}", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Resisted, Reason = "reason" });
        // Add 1 succeeded probe
        probes.Add(new() { ProbeId = "s0", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Succeeded, Reason = "reason" });

        var result = new AttackResult
        {
            AttackName = "PromptInjection",
            OwaspId = "LLM01",
            ProbeResults = probes,
            ResistedCount = 9,
            SucceededCount = 1,
            InconclusiveCount = 0
        };

        Assert.False(result.Passed);
        Assert.Equal(0.1, result.AttackSuccessRate, precision: 2);
    }

    [Fact]
    public void AttackResult_HighestSeverity_ReturnsMaxFromFailedProbes()
    {
        var result = new AttackResult
        {
            AttackName = "PIILeakage",
            OwaspId = "LLM02",
            Severity = Severity.High,
            ProbeResults = [
                new() { ProbeId = "1", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Succeeded, Reason = "r", Severity = Severity.Medium },
                new() { ProbeId = "2", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Succeeded, Reason = "r", Severity = Severity.Critical },
                new() { ProbeId = "3", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Resisted, Reason = "r", Severity = Severity.High }
            ],
            SucceededCount = 2,
            ResistedCount = 1,
            InconclusiveCount = 0
        };

        Assert.Equal(Severity.Critical, result.HighestSeverity);
    }

    [Fact]
    public void AttackResult_HighestSeverity_ReturnsInformational_WhenAllResisted()
    {
        var result = new AttackResult
        {
            AttackName = "Test",
            OwaspId = "LLM01",
            ProbeResults = [
                new() { ProbeId = "1", Prompt = "p", Response = "r", Outcome = EvaluationOutcome.Resisted, Reason = "r", Severity = Severity.Critical }
            ],
            ResistedCount = 1,
            SucceededCount = 0,
            InconclusiveCount = 0
        };

        Assert.Equal(Severity.Informational, result.HighestSeverity);
    }
}
