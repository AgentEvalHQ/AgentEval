// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Output;
using AgentEval.RedTeam;
using Xunit;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// AE-01 coverage for the red-team assertion family: a passing check records one pass, a failing
/// check records one failure named after the assertion. This is the regression guard for the
/// probe instrumentation in <c>RedTeamAssertions</c> — an assertion whose return was not routed
/// through <c>probe.Complete(...)</c> would record "could not decide" instead of the pass it earned.
/// </summary>
public class RedTeamAssertionRecordingTests
{
    private static RedTeamResult CleanResult(int resisted = 4, int succeeded = 0)
    {
        var probes = new List<ProbeResult>();
        for (var i = 0; i < resisted; i++)
        {
            probes.Add(new ProbeResult
            {
                ProbeId = $"R-{i:D3}",
                Prompt = "p",
                Response = "I cannot help with that.",
                Outcome = EvaluationOutcome.Resisted,
                Reason = "resisted",
                Difficulty = Difficulty.Easy,
                Severity = Severity.High
            });
        }

        for (var i = 0; i < succeeded; i++)
        {
            probes.Add(new ProbeResult
            {
                ProbeId = $"S-{i:D3}",
                Prompt = "p",
                Response = "PWNED",
                Outcome = EvaluationOutcome.Succeeded,
                Reason = "compromised",
                Difficulty = Difficulty.Easy,
                Severity = Severity.High
            });
        }

        return new RedTeamResult
        {
            AgentName = "TestAgent",
            StartedAt = DateTimeOffset.UnixEpoch,
            CompletedAt = DateTimeOffset.UnixEpoch.AddSeconds(5),
            Duration = TimeSpan.FromSeconds(5),
            TotalProbes = probes.Count,
            ResistedProbes = resisted,
            SucceededProbes = succeeded,
            InconclusiveProbes = 0,
            AttackResults =
            [
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    AttackDisplayName = "Prompt Injection",
                    OwaspId = "LLM01",
                    MitreAtlasIds = ["AML.T0051"],
                    Severity = Severity.High,
                    ResistedCount = resisted,
                    SucceededCount = succeeded,
                    InconclusiveCount = 0,
                    ProbeResults = probes
                }
            ]
        };
    }

    [Fact]
    public void PassingRedTeamAssertions_EachRecordExactlyOnePass()
    {
        var result = CleanResult();

        using var scope = AgentEvalScope.Collecting();
        result.Should().HavePassed();
        result.Should().HaveASRBelow(0.5);
        result.Should().HaveResistedAttack("PromptInjection");
        result.Should().HaveNoHighSeverityCompromises();
        result.Should().HaveNoCompromisesFor("LLM01");
        result.Should().HaveAttackASRBelow("PromptInjection", 0.5);
        result.Should().BeConclusive();
        result.Should().HaveNoExecutionErrors();
        scope.Dispose();

        Assert.Equal(8, scope.Results.Count);
        Assert.All(scope.Results, r => Assert.Equal(AssertionOutcome.Passed, r.Outcome));
    }

    [Fact]
    public void FailingRedTeamAssertion_RecordsAFailureNamedAfterTheAssertion()
    {
        var result = CleanResult(resisted: 2, succeeded: 2);

        using var scope = AgentEvalScope.Collecting();
        result.Should().HavePassed();
        scope.Dispose();

        var row = Assert.Single(scope.Results);
        Assert.Equal("HavePassed", row.Assertion);
        Assert.Equal(AssertionOutcome.Failed, row.Outcome);
    }
}
