// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.MAF.Skills;
using AgentEval.Metrics.Agentic;
using AgentEval.Skills;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Agent Skills — Skill Security Index.
///
/// Joins the Compliance axis (a static <c>MafSkillScanner</c> scan) and the Efficiency axis (a real
/// agent run scored by <c>SkillDisclosureEfficiencyMetric</c>) into ONE composite 0-100
/// <c>SkillSecurityIndex</c> score. The Security axis (red-team probe outcome) is deliberately omitted
/// here — this on-ramp sample doesn't run <c>SkillInjectionAttack</c> — and the index honestly reports
/// only <b>2/3</b> axes measured rather than faking a perfect security score. See
/// <c>samples/AgentEval.AgentSkillsEval</c> (Run 5-7) for the full 3-axis composition including the
/// red-team pass and hash-pin drift detection.
///
/// 🔑 Requires Azure OpenAI credentials.
/// ⏱️ Time to understand: 2 minutes
/// </summary>
public static class AgentSkillsSecurityIndex
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        var skillPath = AgentSkillsSampleHelpers.ResolveExpenseReportSkillPath();
        if (skillPath is null) return;

        var chatClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        // Efficiency axis: a real agent run against the shared skill.
        var agent = AgentSkillsSampleHelpers.BuildExpenseReportAgent(chatClient, skillPath);
        const string userMessage = "What is our company's policy on dinner expenses? I need to know the exact per-meal cap.";
        Console.WriteLine($"  Prompt: \"{userMessage}\"\n");

        var response = await agent.RunAsync(userMessage);
        var toolUsage = AgentSkillsSampleHelpers.ExtractToolUsage(response);
        AgentSkillsSampleHelpers.PrintTrace(toolUsage);

        var efficiencyResult = await new SkillDisclosureEfficiencyMetric().EvaluateAsync(
            new EvaluationContext { Input = userMessage, Output = response.Text, ToolUsage = toolUsage });

        // Compliance axis: the same static scan as the Compliance Scanner sample.
        var scanAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions { Name = "ScannerAgent" });
        var complianceReport = await MafSkillScanner.ScanFileSkillsAsync(skillPath, scanAgent);

        var index = SkillSecurityIndex.Compute(new SkillSecurityIndexInputs(complianceReport, efficiencyResult, Security: null));

        Console.WriteLine($"\n  Skill Security Index: {index.Score:F0}/100 ({index.AxesMeasured}/3 axes measured)");
        Console.WriteLine($"    Compliance: {index.ComplianceComponent:F0}  " +
            $"Efficiency: {index.EfficiencyComponent:F0}  " +
            $"Security: {(index.SecurityComponent is { } s ? s.ToString("F0") : "n/a (not run this sample)")}");
        Console.WriteLine($"    {index.Explanation}");

        Console.WriteLine("\n=== Agent Skills Security Index Complete ===");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🧩 AGENT SKILLS — SKILL SECURITY INDEX                                       ║
║   Compliance + Efficiency joined into one score — a missing axis is never fake ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
