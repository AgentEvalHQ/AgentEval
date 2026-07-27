// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// Permanent env-gated live calibration check. Inert unless AGENTEVAL_RUN_GATEKEEPER_CAL=1.

using AgentEval.Cli.Commands;
using AgentEval.Guardrails.Judges;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Tests.Guardrails;

public sealed class InterAgentBoundaryOutboundLiveCalibrationCheck(
    ITestOutputHelper output)
{
    [Fact]
    public async Task Live_Calibrate_OutboundInterAgentGoalDrift()
    {
        if (Environment.GetEnvironmentVariable("AGENTEVAL_RUN_GATEKEEPER_CAL") != "1")
        {
            return;
        }

        var (judge, deployment, exitCode) =
            AzureChatAgentFactory.TryBuildChatClientFromEnv();
        Assert.True(
            judge is not null,
            $"no judge client from env (exit {exitCode})");

        var report =
            await InterAgentBoundaryInjectionGate.CalibrateOutboundAsync(judge!);

        var summary =
            $"[InterAgentOutboundGoalDrift] live calibration — judge={deployment ?? "?"}\n" +
            $"N={report.Total} TP={report.TruePositives} TN={report.TrueNegatives} " +
            $"FP={report.FalsePositives} FN={report.FalseNegatives}\n" +
            $"Accuracy={report.DecisiveAccuracy:P1} FP-rate={report.FalsePositiveRate:P1} " +
            $"Kappa={report.KappaVsGold:F3} Baseline={report.BaselineAccuracy:P1} " +
            $"BeatsBaseline={report.BeatsBaseline} IsInlineReady={report.IsInlineReady}";
        output.WriteLine(summary);
        Console.WriteLine(summary);

        Assert.NotNull(report);
    }
}
