// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// PERMANENT env-gated LIVE calibration check (inert unless AGENTEVAL_RUN_GATEKEEPER_CAL=1) — mirrors the
// SkillInjectionGoldSetCalibrationLiveCheck / CompositeEvalsHeldOutLiveCheck pattern. Drives each of the
// four Gatekeeper Stage-3 flagship judges (IntentActionMismatch, GoalHijackDrift, UngroundedClaim,
// HallucinatedCitation) through GateCalibrationHarness against a REAL Azure OpenAI model, and reports the
// honest calibration result used to decide each judge's shadow-vs-inline documentation. This is the
// primary source of truth for "did this judge actually converge" — never assumed, always measured.
using AgentEval.Cli.Commands;
using AgentEval.Guardrails.Judges;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Tests.Guardrails;

public class GatekeeperStage3JudgesLiveCalibrationCheck(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static void Report(ITestOutputHelper output, string axis, AgentEval.Guardrails.Judges.CalibrationReport report, string deployment)
    {
        var lines = new List<string>
        {
            $"[{axis}] live calibration — judge={deployment}",
            $"  N={report.Total} TP={report.TruePositives} TN={report.TrueNegatives} FP={report.FalsePositives} FN={report.FalseNegatives}",
            $"  DecisiveAccuracy={report.DecisiveAccuracy:P1} FalsePositiveRate={report.FalsePositiveRate:P1} Kappa={report.KappaVsGold:F3}",
            $"  BaselineAccuracy={report.BaselineAccuracy:P1} BeatsBaseline={report.BeatsBaseline}",
            $"  ==> IsInlineReady = {report.IsInlineReady}",
        };
        if (report.FalseNegatives > 0)
        {
            lines.Add("  MISSED (dangerous errors):");
            foreach (var c in report.Cases.Where(c => c.IsDangerousError))
            {
                lines.Add($"    - {Truncate(c.Text)}");
            }
        }

        if (report.FalsePositives > 0)
        {
            lines.Add("  FALSE ALARMS:");
            foreach (var c in report.Cases.Where(c => c.IsFalsePositive))
            {
                lines.Add($"    - {Truncate(c.Text)}");
            }
        }

        var text = string.Join("\n", lines);
        output.WriteLine(text);
        Console.WriteLine(text);
    }

    private static string Truncate(string s) => s.Length <= 140 ? s : s[..140] + "...";

    [Fact]
    public async Task Live_Calibrate_IntentActionMismatchJudge()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_GATEKEEPER_CAL") != "1") return;
        var (judge, deployment, exit) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(judge is not null, $"no judge client from env (exit {exit})");
        var report = await IntentActionMismatchJudge.CalibrateAsync(judge!);
        Report(_output, "IntentActionMismatch", report, deployment ?? "?");
        Assert.NotNull(report);
    }

    [Fact]
    public async Task Live_Calibrate_GoalHijackDriftJudge()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_GATEKEEPER_CAL") != "1") return;
        var (judge, deployment, exit) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(judge is not null, $"no judge client from env (exit {exit})");
        var report = await GoalHijackDriftJudge.CalibrateAsync(judge!);
        Report(_output, "GoalHijackDrift", report, deployment ?? "?");
        Assert.NotNull(report);
    }

    [Fact]
    public async Task Live_Calibrate_UngroundedClaimJudge()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_GATEKEEPER_CAL") != "1") return;
        var (judge, deployment, exit) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(judge is not null, $"no judge client from env (exit {exit})");
        var report = await UngroundedClaimJudge.CalibrateAsync(judge!);
        Report(_output, "UngroundedClaim", report, deployment ?? "?");
        Assert.NotNull(report);
    }

    [Fact]
    public async Task Live_Calibrate_HallucinatedCitationJudge()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_GATEKEEPER_CAL") != "1") return;
        var (judge, deployment, exit) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(judge is not null, $"no judge client from env (exit {exit})");

        var gate = new HallucinatedCitationJudge(judge!);
        var goldSet = HallucinatedCitationJudge.CalibrationGoldSet();
        var report = await AgentEval.Guardrails.Judges.GateCalibrationHarness.EvaluateAsync(
            gate, goldSet,
            new AgentEval.Guardrails.Judges.CalibrationOptions { MaxDangerousErrors = 0, MinCasesPerDirection = 20 });
        Report(_output, "HallucinatedCitation", report, deployment ?? "?");
        Assert.NotNull(report);
    }
}
