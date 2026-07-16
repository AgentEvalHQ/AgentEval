// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// PERMANENT env-gated LIVE calibration check (inert unless AGENTEVAL_RUN_SKILLCAL=1) — mirrors the
// CompositeEvalsHeldOutLiveCheck pattern. Drives the REAL IndirectInjectionJudge (a real Azure OpenAI
// call per case) through GateCalibrationHarness against SkillInjectionGoldSet, and reports whether the
// REUSED flagship rubric actually clears calibration on the NEW skill-description surface — the risk
// item the design doc itself flags (§6.1, §"Effort" note: "Reusing a rubric calibrated on
// retrieved-document injection against a NEW surface... may not converge"). This is the honest, live
// answer this session used to decide Posture A/B wiring for the sample and whether the skill-injection
// judge is documented as inline-ready or shadow-only.
using AgentEval.Cli.Commands;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Tests.Guardrails;

public class SkillInjectionGoldSetCalibrationLiveCheck(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task Live_Calibrate_IndirectInjectionRubric_OnSkillInjectionGoldSet()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_SKILLCAL") != "1")
        {
            return;   // inert unless explicitly opted in (live LLM cost — ~52 model calls)
        }

        var (judge, deployment, exit) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(judge is not null, $"no judge client from env (exit {exit})");

        var goldSet = SkillInjectionGoldSet.CalibrationGoldSet();
        var report = await IndirectInjectionJudge.CalibrateAsync(judge!, goldSet: goldSet);

        var lines = new List<string>
        {
            $"[SkillInjection] calibrating IndirectInjectionRubric on the skill-description gold set — judge={deployment}",
            $"  N={report.Total} (attacks={goldSet.AttackCount}, benign={goldSet.BenignCount})",
            $"  TP={report.TruePositives} TN={report.TrueNegatives} FP={report.FalsePositives} FN={report.FalseNegatives}",
            $"  DecisiveAccuracy={report.DecisiveAccuracy:P1} FalsePositiveRate={report.FalsePositiveRate:P1} Kappa={report.KappaVsGold:F3}",
            $"  BaselineAccuracy={report.BaselineAccuracy:P1} BeatsBaseline={report.BeatsBaseline}",
            $"  SufficientData={report.SufficientData} MeetsThresholds={report.MeetsThresholds} PromotionCriteriaConfigured={report.PromotionCriteriaConfigured}",
            $"  ==> IsInlineReady = {report.IsInlineReady}",
        };

        if (report.FalseNegatives > 0)
        {
            lines.Add("  MISSED ATTACKS (dangerous errors):");
            foreach (var c in report.Cases.Where(c => c.IsDangerousError))
            {
                lines.Add($"    - {c.Text}");
            }
        }

        if (report.FalsePositives > 0)
        {
            lines.Add("  FALSE ALARMS on benign skill content:");
            foreach (var c in report.Cases.Where(c => c.IsFalsePositive))
            {
                lines.Add($"    - {c.Text}");
            }
        }

        var text = string.Join("\n", lines);
        _output.WriteLine(text);
        Console.WriteLine(text);

        // This test's job is to REPORT the honest calibration result (used to decide the shadow-vs-inline
        // documentation), not to hard-fail the build on a non-convergent result — a non-convergent rubric
        // on a NEW surface is an expected, honestly-documented outcome per the design doc's own
        // contingency note, not a bug. It only fails if the harness could not even run (e.g. no client).
        Assert.NotNull(report);
    }
}
