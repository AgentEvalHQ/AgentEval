// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using static AgentEval.Memory.Tests.LongMemEvalStructuredJudgeTests;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Tests for <see cref="JudgeAgreementHarness"/>: it must detect a judge contradicting itself, and must
/// not claim agreement it did not observe.
/// </summary>
public class JudgeAgreementHarnessTests
{
    [Fact]
    public async Task DetectsADeliberatelyInjectedDisagreement()
    {
        // The judge is rigged to flip its verdict on the second look at the SAME inputs. Nothing about
        // the response or the question changed, so any score movement here is judge noise by construction.
        var judge = CreateJudge(Response("yes"), Response("no"));
        var harness = new JudgeAgreementHarness(judge);

        var report = await harness.MeasureAsync([Case("q-flip")], repeats: 2);

        Assert.Equal(1, report.CaseCount);
        Assert.Equal(1, report.DisagreementCount);
        Assert.Equal(1.0, report.DisagreementRate);
        Assert.False(report.CaseReports[0].Agreed);
        Assert.Equal(
            [JudgeOutcomeStatus.Yes, JudgeOutcomeStatus.No],
            report.CaseReports[0].Statuses);
    }

    [Fact]
    public async Task AStableJudgeReportsNoDisagreement()
    {
        var judge = CreateJudge(Response("yes"), Response("yes"));
        var harness = new JudgeAgreementHarness(judge);

        var report = await harness.MeasureAsync([Case("q-stable")], repeats: 2);

        Assert.Equal(0, report.DisagreementCount);
        Assert.Equal(0.0, report.DisagreementRate);
        Assert.True(report.CaseReports[0].Agreed);
    }

    [Fact]
    public async Task SeparatesJudgeNoiseFromSystemChange_AcrossAMixedSet()
    {
        // Two questions, one stable and one flipping: the rate is 1 in 2, not "the system got worse".
        var judge = CreateJudge(
            Response("yes"), Response("yes"),      // q-stable
            Response("yes"), Response("no"));      // q-flip
        var harness = new JudgeAgreementHarness(judge);

        var report = await harness.MeasureAsync([Case("q-stable"), Case("q-flip")], repeats: 2);

        Assert.Equal(2, report.CaseCount);
        Assert.Equal(1, report.DisagreementCount);
        Assert.Equal(0.5, report.DisagreementRate);
    }

    [Fact]
    public async Task DisagreementAmongInconclusiveOutcomesIsStillDisagreement()
    {
        // Empty and Invalid are different failures; collapsing them would hide a real behaviour change.
        // MaxJudgeRetries = 0 so each repeat is exactly one provider call — with the default of 1, the
        // first repeat would retry and swallow the second scripted response.
        var judge = CreateJudge(Response("   "), Response("maybe"));
        var harness = new JudgeAgreementHarness(judge);

        var report = await harness.MeasureAsync(
            [Case("q-incon")],
            new ExternalBenchmarkOptions { MaxJudgeRetries = 0 },
            repeats: 2);

        Assert.Equal(
            [JudgeOutcomeStatus.Empty, JudgeOutcomeStatus.Invalid],
            report.CaseReports[0].Statuses);
        Assert.Equal(1, report.DisagreementCount);
    }

    [Fact]
    public async Task ThreeRepeats_DetectAFlipThatTwoWouldHaveMissed()
    {
        var judge = CreateJudge(Response("yes"), Response("yes"), Response("no"));
        var harness = new JudgeAgreementHarness(judge);

        var report = await harness.MeasureAsync([Case("q-late-flip")], repeats: 3);

        Assert.Equal(3, report.Repeats);
        Assert.Equal(1, report.DisagreementCount);
    }

    [Fact]
    public async Task ReportsTheProviderCallsItSpent()
    {
        var judge = CreateJudge(Response("yes"), Response("yes"), Response("yes"), Response("yes"));
        var harness = new JudgeAgreementHarness(judge);

        var report = await harness.MeasureAsync([Case("a"), Case("b")], repeats: 2);

        // 2 cases x 2 repeats, so enabling this doubles judge spend over the set it is run on.
        Assert.Equal(4, report.TotalJudgeProviderCalls);
    }

    [Fact]
    public async Task RecordsTheConfigurationTheMeasurementWasTakenUnder()
    {
        var judge = CreateJudge(
            Response("""{"verdict": "yes", "reasoning": "ok"}"""),
            Response("""{"verdict": "yes", "reasoning": "ok"}"""));
        var harness = new JudgeAgreementHarness(judge);

        var report = await harness.MeasureAsync(
            [Case("q")],
            new ExternalBenchmarkOptions
            {
                JudgeTemperature = 0,
                JudgeVerdictProtocol = JudgeVerdictProtocol.StructuredJson,
                MaxJudgeRetries = 0
            },
            repeats: 2);

        // A disagreement rate is uninterpretable without the temperature and protocol it was taken at.
        Assert.Equal(0d, report.JudgeTemperature);
        Assert.Equal(JudgeVerdictProtocol.StructuredJson, report.JudgeVerdictProtocol);
    }

    [Fact]
    public async Task EmptyCaseSet_HasNoRateRatherThanAZeroOne()
    {
        var harness = new JudgeAgreementHarness(CreateJudge());

        var report = await harness.MeasureAsync([]);

        // Reporting 0.0 here would claim perfect agreement that was never observed.
        Assert.Equal(0, report.CaseCount);
        Assert.Null(report.DisagreementRate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task FewerThanTwoRepeats_IsRejectedBecauseItCannotRevealDisagreement(int repeats)
    {
        var harness = new JudgeAgreementHarness(CreateJudge(Response("yes")));

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => harness.MeasureAsync([Case("q")], repeats: repeats));

        Assert.Equal("repeats", error.ParamName);
    }

    [Fact]
    public void NullJudge_IsRejected()
        => Assert.Throws<ArgumentNullException>(() => new JudgeAgreementHarness(null!));

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var harness = new JudgeAgreementHarness(CreateJudge(Response("yes"), Response("yes")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.MeasureAsync([Case("q")], repeats: 2, ct: cts.Token));
    }

    private static JudgeAgreementCase Case(string questionId) => new()
    {
        AgentResponse = "the agent response, held constant across repeats",
        Question = new ExternalBenchmarkQuestion
        {
            QuestionId = questionId,
            QuestionType = "single-session-user",
            Question = "What did the user say?",
            GoldAnswer = "The expected answer"
        }
    };
}
