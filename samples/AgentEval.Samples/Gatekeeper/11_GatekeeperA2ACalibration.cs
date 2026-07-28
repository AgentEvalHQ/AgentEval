// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Guardrails.Judges;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Calibrates both inter-agent boundary judges against the configured Azure OpenAI deployment without contacting
/// an A2A endpoint. The calibration corpora are sent only after the caller explicitly opts in with
/// <c>AGENTEVAL_A2A_I_UNDERSTAND_CALIBRATION_PAYLOADS=true</c>.
/// </summary>
public static class GatekeeperA2ACalibration
{
    internal const string CalibrationConsentVariable =
        "AGENTEVAL_A2A_I_UNDERSTAND_CALIBRATION_PAYLOADS";

    public static async Task RunAsync()
    {
        Console.WriteLine("\n=== Gatekeeper — Live A2A Judge Calibration ===\n");

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        if (!HasExplicitConsent())
        {
            Console.WriteLine(
                $"   Set {CalibrationConsentVariable}=true to authorize sending the reviewed " +
                "calibration corpora to the configured Azure OpenAI deployment.");
            return;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var judge = CreateJudge();

        Console.WriteLine(
            $"① Calibrating the inbound and outbound axes with deployment '{AIConfig.ModelDeployment}'…");
        var (inbound, outbound) = await CalibrateAsync(judge, timeout.Token);

        PrintReport("inbound ", inbound);
        PrintReport("outbound", outbound);

        Console.WriteLine(
            inbound.IsInlineReady && outbound.IsInlineReady
                ? "\n✅ Both boundary judges are inline-ready for this exact deployment."
                : "\n⛔ At least one boundary judge is not inline-ready. Keep Phase 4 unpromoted.");
        Console.WriteLine("\n=== Gatekeeper — Live A2A Judge Calibration Complete ===");
    }

    internal static IChatClient CreateJudge() =>
        new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

    internal static async Task<(CalibrationReport Inbound, CalibrationReport Outbound)> CalibrateAsync(
        IChatClient judge,
        CancellationToken cancellationToken)
    {
        var inbound = await InterAgentBoundaryInjectionGate.CalibrateInboundAsync(
            judge,
            cancellationToken: cancellationToken);
        var outbound = await InterAgentBoundaryInjectionGate.CalibrateOutboundAsync(
            judge,
            cancellationToken: cancellationToken);
        return (inbound, outbound);
    }

    internal static void PrintReport(string direction, CalibrationReport report)
    {
        var baseline = report.BaselineAccuracy is { } value ? value.ToString("P1") : "n/a";
        Console.WriteLine(
            $"   {direction} → N={report.Total}, TP={report.TruePositives}, TN={report.TrueNegatives}, " +
            $"FP={report.FalsePositives}, FN={report.FalseNegatives}, accuracy={report.DecisiveAccuracy:P1}, " +
            $"FP-rate={report.FalsePositiveRate:P1}, κ={report.KappaVsGold:F3}, baseline={baseline}, " +
            $"beats-baseline={report.BeatsBaseline}, inline-ready={report.IsInlineReady}");
    }

    private static bool HasExplicitConsent() =>
        string.Equals(
            Environment.GetEnvironmentVariable(CalibrationConsentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);
}
