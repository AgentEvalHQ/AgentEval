// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Assertions;
using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Baseline;

namespace AgentEval.Tests.RedTeam;

/// <summary>
/// A red-team scan with no probe that reached a verdict measured nothing: its score is not "fully secure", the score
/// and ASR assertions cannot pass on it, and a baseline comparison against it is not comparable (T13-10).
/// </summary>
public class RedTeamNotMeasuredTests
{
    private static RedTeamResult ZeroProbeScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 0,
        AttackResults = [],
    };

    private static RedTeamResult AllInconclusiveScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 5,
        InconclusiveProbes = 5,
        AttackResults =
        [
            new AttackResult
            {
                AttackName = "PromptInjection",
                AttackDisplayName = "Prompt Injection",
                OwaspId = "LLM01",
                Severity = Severity.High,
                InconclusiveCount = 5,
                ProbeResults = Enumerable.Range(1, 5).Select(i => Probe($"PI-{i:D3}", EvaluationOutcome.Inconclusive)).ToList(),
            },
        ],
    };

    private static RedTeamResult ResistedScan() => new()
    {
        AgentName = "TestAgent",
        TotalProbes = 5,
        ResistedProbes = 5,
        AttackResults =
        [
            new AttackResult
            {
                AttackName = "PromptInjection",
                AttackDisplayName = "Prompt Injection",
                OwaspId = "LLM01",
                Severity = Severity.High,
                ResistedCount = 5,
                ProbeResults = Enumerable.Range(1, 5).Select(i => Probe($"PI-{i:D3}", EvaluationOutcome.Resisted)).ToList(),
            },
        ],
    };

    private static ProbeResult Probe(string id, EvaluationOutcome outcome) => new()
    {
        ProbeId = id,
        Prompt = "p",
        Response = "r",
        Outcome = outcome,
        Reason = outcome.ToString(),
        Severity = Severity.High,
    };

    [Fact]
    public void ZeroProbeScan_OverallScore_IsZero_NotAFullySecure100()
    {
        Assert.Equal(0.0, ZeroProbeScan().OverallScore);
        Assert.Equal(Verdict.Inconclusive, ZeroProbeScan().Verdict);
    }

    [Fact]
    public void HaveMinimumScore_OnAZeroProbeScan_RecordsInconclusive_NotAPass()
    {
        using var scope = AgentEvalScope.Collecting();
        ZeroProbeScan().Should().HaveMinimumScore(80);
        scope.Dispose();

        var row = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Inconclusive, row.Outcome);
    }

    [Fact]
    public void HaveMinimumScore_OnAZeroProbeScan_OutsideAScope_Throws()
    {
        // Nothing records an undecided result here, so the security check fails closed.
        var ex = Assert.Throws<RedTeamAssertionException>(() => ZeroProbeScan().Should().HaveMinimumScore(80));

        Assert.Contains("not measured", ex.Message);
        Assert.Contains("no probe ran", ex.Message);
    }

    [Fact]
    public void HaveMinimumScore_OnAConclusiveScan_StillPasses()
    {
        using var scope = AgentEvalScope.Collecting();
        ResistedScan().Should().HaveMinimumScore(80);
        scope.Dispose();

        var row = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Passed, row.Outcome);
    }

    [Fact]
    public void HaveASRBelow_WhenEveryProbeWasInconclusive_IsNotAPass()
    {
        var ex = Assert.Throws<RedTeamAssertionException>(() => AllInconclusiveScan().Should().HaveASRBelow(0.05));

        Assert.Contains("not measured", ex.Message);
    }

    [Fact]
    public void HaveASRBelow_WhenEveryProbeWasInconclusive_RecordsInconclusive()
    {
        using var scope = AgentEvalScope.Collecting();
        AllInconclusiveScan().Should().HaveASRBelow(0.05);
        scope.Dispose();

        var row = Assert.Single(scope.Results);
        Assert.Equal(AssertionOutcome.Inconclusive, row.Outcome);
    }

    [Fact]
    public void HaveAttackASRBelow_OnAnAttackWithNoConclusiveProbe_IsNotAPass()
    {
        var ex = Assert.Throws<RedTeamAssertionException>(() =>
            AllInconclusiveScan().Should().HaveAttackASRBelow("PromptInjection", 0.05));

        Assert.Contains("not measured", ex.Message);
    }

    [Fact]
    public void Baseline_FromResult_PersistsTheConclusiveCount()
    {
        Assert.Equal(5, RedTeamBaseline.FromResult(ResistedScan(), "v1").ConclusiveProbes);
        Assert.Equal(0, RedTeamBaseline.FromResult(ZeroProbeScan(), "v0").ConclusiveProbes);
    }

    [Fact]
    public void Comparison_AgainstAZeroProbeBaseline_IsNotComparable_AndRefused()
    {
        // Today the empty baseline's fabricated 100 met the current 100: Stable. Read as a Regression it said the agent
        // got worse. The owner's decision: refused, neither a regression nor a pass.
        var baseline = RedTeamBaseline.FromResult(ZeroProbeScan(), "v0");

        var comparison = new RedTeamBaselineComparer().Compare(ResistedScan(), baseline);

        Assert.False(comparison.IsComparable);
        Assert.Contains("baseline", comparison.NotComparableReason);
        Assert.Equal(RegressionStatus.Incomparable, comparison.Status);
        Assert.False(comparison.IsRegression);
        Assert.False(comparison.IsStable);
        Assert.False(comparison.IsImprovement);
    }

    [Fact]
    public void Comparison_OfAZeroProbeScan_AgainstAMeasuredBaseline_IsRefused_NotARegression()
    {
        var comparison = new RedTeamBaselineComparer().Compare(
            ZeroProbeScan(), RedTeamBaseline.FromResult(ResistedScan(), "v1"));

        Assert.Equal(RegressionStatus.Incomparable, comparison.Status);
        Assert.Contains("current run", comparison.NotComparableReason);
        Assert.False(comparison.IsRegression);
    }

    [Fact]
    public void BaselineAssertions_OnARefusedComparison_CannotPass()
    {
        // IsRegression is false for a refused comparison; NotBeRegression must not read that as "no regression".
        var comparison = new RedTeamBaselineComparer().Compare(
            ResistedScan(), RedTeamBaseline.FromResult(ZeroProbeScan(), "v0"));

        var ex = Assert.Throws<RedTeamRegressionException>(() => comparison.Should().NotBeRegression().ThrowIfFailed());
        Assert.Contains("not comparable", ex.Message);
        var score = Assert.Throws<RedTeamRegressionException>(
            () => comparison.Should().HaveOverallScoreNotDecreasedBy(5).ThrowIfFailed());
        Assert.Contains("not comparable", score.Message);
    }

    [Fact]
    public void Comparison_BetweenTwoMeasuredRuns_IsComparable()
    {
        var comparison = new RedTeamBaselineComparer().Compare(
            ResistedScan(), RedTeamBaseline.FromResult(ResistedScan(), "v1"));

        Assert.True(comparison.IsComparable);
        Assert.Null(comparison.NotComparableReason);
        Assert.Equal(RegressionStatus.Stable, comparison.Status);
    }

    [Fact]
    public void Comparison_AgainstAPreFieldBaseline_CountsItsPersistedAttackResults()
    {
        // A baseline saved before ConclusiveProbes existed still carries per-attack resisted counts.
        var baseline = RedTeamBaseline.FromResult(ResistedScan(), "v1") with { ConclusiveProbes = null };

        var comparison = new RedTeamBaselineComparer().Compare(ResistedScan(), baseline);

        Assert.True(comparison.IsComparable);
    }
}
