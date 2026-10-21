// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.RedTeam;
using AgentEval.RedTeam.Baseline;
using Xunit;

namespace AgentEval.Tests.RedTeam.Baseline;

public class BaselineNotMeasuredTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void BaselineAttack_NoConclusiveProbes_HasNoRate(int inconclusive)
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(0, 0, inconclusive)), "v1");
        Assert.Null(Assert.Single(baseline.AttackResults).Rate);
    }

    [Fact]
    public void BaselineAttack_PartlyConclusive_RateUsesConclusiveDenominator()
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(6, 2, 2)), "v1");
        Assert.Equal<double?>(0.75, Assert.Single(baseline.AttackResults).Rate);
    }

    [Fact]
    public void BaselineAttack_AllFailed_IsMeasuredZero()
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(0, 3, 0)), "v1");
        Assert.Equal<double?>(0, Assert.Single(baseline.AttackResults).Rate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compare_AttackMissingFromBaseline_HasNoBaselineRate(bool inconclusive)
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(5, 0, 0)), "v1");
        var current = Scan(Attack(5, 0, 0), Attack(inconclusive ? 0 : 5, 0, inconclusive ? 5 : 0, "Jailbreak"));
        var comparison = new RedTeamBaselineComparer().Compare(current, baseline);
        var attack = Assert.Single(comparison.AttackComparisons, a => a.AttackName == "Jailbreak");
        Assert.Null(attack.BaselineRate);
        Assert.True(attack.IsNewAttack);
        Assert.Equal("the attack is not in the baseline", attack.RateNotComparableReason);
        Assert.Null(attack.RateDelta);
        Assert.Equal("❓", attack.StatusEmoji);
        if (inconclusive) Assert.Null(attack.CurrentRate);
    }

    [Fact]
    public void Compare_ExistingAttackNowInconclusive_HasNoCurrentRate()
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(5, 0, 0), Attack(5, 0, 0, "Jailbreak")), "v1");
        var comparison = new RedTeamBaselineComparer().Compare(Scan(Attack(0, 0, 5), Attack(5, 0, 0, "Jailbreak")), baseline);
        var attack = Assert.Single(comparison.AttackComparisons, a => a.AttackName == "PromptInjection");
        Assert.Null(attack.CurrentRate);
        Assert.Null(attack.RateDelta);
        Assert.Equal("❓", attack.StatusEmoji);
    }

    [Fact]
    public void Compare_PartlyConclusiveAttacks_UsesConclusiveRates()
    {
        var result = Scan(Attack(6, 2, 2));
        var comparison = new RedTeamBaselineComparer().Compare(result, RedTeamBaseline.FromResult(result, "v1"));
        var attack = Assert.Single(comparison.AttackComparisons);
        Assert.Equal<double?>(0.75, attack.BaselineRate);
        Assert.Equal<double?>(0.75, attack.CurrentRate);
    }

    [Fact]
    public void BaselineCoverage_NoProbeCounts_IsAbsentAndNotARegression()
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(10, 0, 0)), "v1") with { AttackResults = [], TotalProbes = 0 };
        var comparison = new RedTeamBaselineComparer().Compare(Scan(Attack(10, 0, 10)), baseline);
        Assert.Null(comparison.BaselineCoverage);
        Assert.Null(comparison.CoverageDrop);
        Assert.Equal(RegressionStatus.Incomparable, comparison.Status);
        Assert.False(comparison.IsRegression);
    }

    [Fact]
    public void BaselineCoverage_OnlyTopLevelCounts_PreservesMeasuredCoverage()
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(7, 0, 3)), "v1") with { AttackResults = [] };
        var comparison = new RedTeamBaselineComparer().Compare(Scan(Attack(7, 0, 3)), baseline);
        Assert.Equal<double?>(0.7, comparison.BaselineCoverage);
        Assert.Equal<double?>(0, comparison.CoverageDrop);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compare_KnownVulnerabilityWithoutNewVerdict_IsNotResolved(bool missing)
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(19, 1, 0)), "v1");
        var attack = Attack(19, 0, missing ? 0 : 1, lastProbeId: missing ? null : "PromptInjection-S0");
        var comparison = new RedTeamBaselineComparer().Compare(Scan(attack), baseline);
        Assert.Empty(comparison.ResolvedVulnerabilities);
        Assert.Contains("PromptInjection-S0", comparison.UnverifiedVulnerabilities);
        Assert.Empty(Assert.Single(comparison.AttackComparisons).Resolved);
        Assert.Equal(RegressionStatus.Incomparable, comparison.Status);
        Assert.False(comparison.IsImprovement);
        Assert.Contains("unverified", comparison.NotComparableReason);
    }

    [Fact]
    public void Compare_KnownVulnerabilityNowResisted_IsResolved()
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(19, 1, 0)), "v1");
        var attack = Attack(20, 0, 0, lastProbeId: "PromptInjection-S0");
        var comparison = new RedTeamBaselineComparer().Compare(Scan(attack), baseline);
        Assert.Contains("PromptInjection-S0", comparison.ResolvedVulnerabilities);
        Assert.Equal(RegressionStatus.Improved, comparison.Status);
    }

    [Fact]
    public void Compare_SameProbeIdInAnotherAttack_DoesNotProveResolution()
    {
        var baseline = RedTeamBaseline.FromResult(Scan(Attack(19, 1, 0), Attack(1, 0, 0, "Jailbreak")), "v1");
        var current = Scan(Attack(19, 0, 1, lastProbeId: "PromptInjection-S0"),
            Attack(1, 0, 0, "Jailbreak", lastProbeId: "PromptInjection-S0"));
        var comparison = new RedTeamBaselineComparer().Compare(current, baseline);
        Assert.Empty(comparison.ResolvedVulnerabilities);
        Assert.Contains("PromptInjection-S0", comparison.UnverifiedVulnerabilities);
        Assert.Equal(RegressionStatus.Incomparable, comparison.Status);
    }

    internal static RedTeamResult Scan(params AttackResult[] attacks) => new()
    {
        AgentName = "offline", AttackResults = attacks, TotalProbes = attacks.Sum(a => a.TotalCount),
        ResistedProbes = attacks.Sum(a => a.ResistedCount), SucceededProbes = attacks.Sum(a => a.SucceededCount),
        InconclusiveProbes = attacks.Sum(a => a.InconclusiveCount),
    };

    internal static AttackResult Attack(int resisted, int succeeded, int inconclusive, string name = "PromptInjection", string? lastProbeId = null) => new()
    {
        AttackName = name, AttackDisplayName = name == "PromptInjection" ? "Prompt Injection" : name,
        OwaspId = "LLM01", Severity = Severity.Medium, MitreAtlasIds = [],
        ResistedCount = resisted, SucceededCount = succeeded, InconclusiveCount = inconclusive,
        ProbeResults = ((ProbeResult[])[
            .. Enumerable.Range(0, resisted).Select(i => Probe($"{name}-R{i}", EvaluationOutcome.Resisted)),
            .. Enumerable.Range(0, succeeded).Select(i => Probe($"{name}-S{i}", EvaluationOutcome.Succeeded)),
            .. Enumerable.Range(0, inconclusive).Select(i => Probe($"{name}-I{i}", EvaluationOutcome.Inconclusive)),
        ]).Select((probe, index) => lastProbeId is not null && index == resisted + succeeded + inconclusive - 1
            ? probe with { ProbeId = lastProbeId } : probe).ToArray(),
    };

    private static ProbeResult Probe(string id, EvaluationOutcome outcome) => new()
    {
        ProbeId = id, Prompt = "p", Response = "a", Outcome = outcome, Reason = outcome.ToString(), Severity = Severity.Medium,
    };
}
