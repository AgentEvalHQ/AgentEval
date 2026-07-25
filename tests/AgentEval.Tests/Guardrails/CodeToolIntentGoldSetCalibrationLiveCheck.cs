// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// PERMANENT env-gated live calibration check (inert unless AGENTEVAL_RUN_CODEINTENTCAL=1). Measures whether the
// existing ToolArgumentGoalCoherenceJudge transfers to the code-tool intended-use surface; it reports rather than
// assuming promotion, and does not introduce a renamed judge when reuse fails.

using AgentEval.Cli.Commands;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Tests.Guardrails;

public class CodeToolIntentGoldSetCalibrationLiveCheck(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task Live_Calibrate_GoalCoherenceJudge_OnCodeToolIntentGoldSet()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_CODEINTENTCAL") != "1")
        {
            return;
        }

        var (judge, deployment, exit) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(judge is not null, $"no judge client from env (exit {exit})");

        var gold = CodeToolIntentGoldSet.CalibrationGoldSet();
        var report = await ToolArgumentGoalCoherenceJudge.CalibrateAsync(judge!, goldSet: gold);
        var lines = new List<string>
        {
            $"[CodeToolIntent] ToolArgumentGoalCoherenceJudge reuse — judge={deployment}",
            $"  N={report.Total} (out-of-scope={gold.AttackCount}, in-scope={gold.BenignCount})",
            $"  TP={report.TruePositives} TN={report.TrueNegatives} FP={report.FalsePositives} FN={report.FalseNegatives}",
            $"  DecisiveAccuracy={report.DecisiveAccuracy:P1} FalsePositiveRate={report.FalsePositiveRate:P1} Kappa={report.KappaVsGold:F3}",
            $"  BaselineAccuracy={report.BaselineAccuracy:P1} BeatsBaseline={report.BeatsBaseline}",
            $"  ==> IsInlineReady = {report.IsInlineReady}",
        };

        if (report.FalseNegatives > 0)
        {
            lines.Add("  MISSED OUT-OF-SCOPE CALLS:");
            lines.AddRange(report.Cases.Where(c => c.IsDangerousError).Select(c => $"    - {c.Text}"));
        }

        if (report.FalsePositives > 0)
        {
            lines.Add("  FALSE ALARMS ON IN-SCOPE CODE:");
            lines.AddRange(report.Cases.Where(c => c.IsFalsePositive).Select(c => $"    - {c.Text}"));
        }

        var text = string.Join("\n", lines);
        _output.WriteLine(text);
        Console.WriteLine(text);
        Assert.NotNull(report);
    }
}
