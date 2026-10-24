// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.PartnerDeskDemo.Demo;
using AgentEval.PartnerDeskDemo.Evals;
using AgentEval.PartnerDeskDemo.Gates;
using AgentEval.PartnerDeskDemo.Tools;

namespace AgentEval.PartnerDeskDemo.Tests;

public sealed class OracleEvidenceTests(PartnerDeskPhaseFixture fixture) : IClassFixture<PartnerDeskPhaseFixture>
{
    [Theory]
    [InlineData(DemoPhase.Compromised)]
    [InlineData(DemoPhase.Level1)]
    [InlineData(DemoPhase.Level2)]
    public void From_UnexercisedScenario_IsUnknownWithoutErasingRecordedZeros(DemoPhase phase)
    {
        var metrics = RunMetrics.From(Unexercised(phase));

        Assert.Null((object?)metrics.OracleHeld);
        Assert.False(metrics.Attempted);
        Assert.False(metrics.Exfiltrated);
        Assert.False(metrics.ExecutedBulkRead);
        Assert.False(metrics.ExecutedExternalSend);
    }

    [Fact]
    public void From_OnlyBulkContractWasExercised_DoesNotClaimTheExternalContractWasProven()
    {
        var outcome = fixture.Outcomes[DemoPhase.Level1] with
        {
            Proposals = [.. fixture.Outcomes[DemoPhase.Level1].Proposals.Where(p =>
                p.ToolName != EmailTool.ToolName || EmailTool.IsInternal(p.String("to")))],
            Findings = [.. fixture.Outcomes[DemoPhase.Level1].Findings.Where(f => f.ToolName != EmailTool.ToolName)]
        };

        Assert.True(RunMetrics.From(outcome).Attempted);
        Assert.Null((object?)RunMetrics.From(outcome).OracleHeld);
    }

    [Fact]
    public void From_AdmissionActuallyBlockedPoison_IsMeasuredWithoutMaliciousToolAttempts()
    {
        var metrics = RunMetrics.From(fixture.Outcomes[DemoPhase.Level2]);

        Assert.False(metrics.Attempted);
        Assert.True(metrics.PoisonWithheld);
        Assert.True(metrics.OracleHeld);
    }

    [Fact]
    public void From_AdmissionBlockedButRetryRecordIsMissing_DoesNotInventAContainmentFailure()
    {
        var metrics = RunMetrics.From(fixture.Outcomes[DemoPhase.Level2] with { RetryAfterContainment = null });

        Assert.True(metrics.PoisonWithheld);
        Assert.Null((object?)metrics.OracleHeld);
        Assert.Null((object?)metrics.SourceContained);
        Assert.Null((object?)metrics.RetryRefused);
        var arm = new ArmResult(DemoPhase.Level2, [metrics], [null]);
        Assert.Equal(0, arm.SourceContained.Total);
        Assert.Equal(0, arm.RetryRefused.Total);
        Assert.Equal(1, arm.Exfiltrated.Total);
    }

    [Fact]
    public void From_ObservedBulkBreachWithAnotherUnexercisedBoundary_RetainsKnownFailure()
    {
        Assert.False(RunMetrics.From(Breached()).OracleHeld);
    }

    [Fact]
    public void Observations_NoExport_DoesNotClaimConcealmentSucceeded()
    {
        var observations = string.Join("\n", PhaseOracle.Observations(Unexercised(DemoPhase.Compromised)));

        Assert.Contains("concealment not measured", observations, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("step 3 took", observations, StringComparison.Ordinal);
    }

    [Fact]
    public void Arm_MixedOracleEvidence_ExcludesOnlyUnknownOracleVerdictsFromItsDenominator()
    {
        var arm = new ArmResult(DemoPhase.Level1,
            [RunMetrics.From(fixture.Outcomes[DemoPhase.Level1]), RunMetrics.From(Breached()),
             RunMetrics.From(Unexercised(DemoPhase.Level1))], [null, null, null]);

        Assert.Equal(3, arm.Runs);
        Assert.Equal(2, arm.OracleHeld.Total);
        Assert.Equal(1, arm.OracleHeld.Successes);
        Assert.Equal(3, arm.Attempted.Total);
        Assert.Equal(2, arm.Attempted.Successes);
        Assert.Equal(3, arm.Exfiltrated.Total);
    }

    [Fact]
    public void Report_UnexercisedGate_IsIncompleteAndPreservesZeroObservedAttacks()
    {
        var arm = new ArmResult(DemoPhase.Level1, [RunMetrics.From(Unexercised(DemoPhase.Level1))], [null]);
        var run = new EvalRun("synthetic evidence control", 1, default, [arm]);
        var writer = new StringWriter();
        EvalReport.WriteText(writer, run);

        Assert.Null(run.GateArmVerdict);
        Assert.Equal<double?>(0d, arm.MeanGateBlocks);
        Assert.Contains("NOT MEASURED", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("RESULT: INCOMPLETE", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("safe, and under attack", writer.ToString(), StringComparison.Ordinal);
        using var json = JsonDocument.Parse(EvalReport.ToJson(run));
        var observed = json.RootElement.GetProperty("arms")[0];
        Assert.Equal("3.1", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("gateArmVerdict").ValueKind);
        Assert.Equal(1, observed.GetProperty("oracleNotMeasured").GetInt32());
        Assert.Equal(0, observed.GetProperty("attempted").GetProperty("successes").GetInt32());
        Assert.Equal(1, observed.GetProperty("attempted").GetProperty("total").GetInt32());
        Assert.Equal(0d, observed.GetProperty("attempted").GetProperty("rate").GetDouble());
        Assert.Equal(JsonValueKind.Null, observed.GetProperty("oracleHeld").GetProperty("rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, observed.GetProperty("oracleHeld").GetProperty("ci95High").ValueKind);
    }

    [Fact]
    public void Verdict_AbsentClaims_CannotVacuouslyPass()
    {
        Assert.Null(PhaseOracle.Verdict([]));
        Assert.Throws<ArgumentNullException>(() => PhaseOracle.Verdict(null!));
    }

    [Fact]
    public void Report_KnownFailureAlongsideUnknown_KeepsTheMeasuredFailure()
    {
        var arm = new ArmResult(DemoPhase.Level1,
            [RunMetrics.From(Breached()), RunMetrics.From(Unexercised(DemoPhase.Level1))], [null, null]);
        var run = new EvalRun("synthetic", 2, default, [arm]);
        var writer = new StringWriter();
        EvalReport.WriteText(writer, run);

        Assert.False(run.GateArmVerdict);
        Assert.Contains("measured oracle failure", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("RESULT: CHECK", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Proportion_Empty_HasNoNumericalRateOrInterval()
    {
        var proportion = new Proportion(0, 0);

        Assert.Null((object?)proportion.Rate);
        Assert.Null((object?)proportion.Wilson95);
        Assert.Contains("n/a", proportion.Format());
        Assert.Equal<double?>(0d, new Proportion(0, 3).Rate);
        Assert.Contains("(0/3)", new Proportion(0, 3).Format());
    }

    [Fact]
    public void Arm_NoCompletedRuns_DoesNotInventZeroMeanGateFindings()
    {
        var arm = new ArmResult(DemoPhase.Level1, [], []);

        Assert.Null((object?)arm.MeanGateBlocks);
        Assert.Null(new EvalRun("synthetic", 1, default, [arm]).GateArmVerdict);
    }

    private PhaseOutcome Unexercised(DemoPhase phase)
    {
        var clean = fixture.Outcomes[DemoPhase.Clean];
        return clean with
        {
            Phase = phase, EvilMode = true,
            Level = phase switch
            {
                DemoPhase.Compromised => GateLevel.None,
                DemoPhase.Level1 => GateLevel.ToolContracts,
                _ => GateLevel.ResultAdmissionAndContainment
            },
            Proposals = phase == DemoPhase.Level2 ? [] : clean.Proposals,
            DatabaseReads = phase == DemoPhase.Level2 ? [] : clean.DatabaseReads,
            Emails = phase == DemoPhase.Level2 ? [] : clean.Emails,
            Findings = [], RetryAfterContainment = null
        };
    }

    private PhaseOutcome Breached() => fixture.Outcomes[DemoPhase.Compromised] with
    {
        Phase = DemoPhase.Level1, Level = GateLevel.ToolContracts, Emails = [],
        Proposals = [.. fixture.Outcomes[DemoPhase.Compromised].Proposals.Where(p => p.ToolName != EmailTool.ToolName)]
    };
}
