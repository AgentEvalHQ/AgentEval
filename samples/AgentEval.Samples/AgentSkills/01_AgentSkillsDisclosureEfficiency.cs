// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Agent Skills — Disclosure Efficiency.
///
/// Scores the observable <c>load_skill -> read_skill_resource -> run_skill_script</c> funnel with
/// <c>SkillDisclosureEfficiencyMetric</c> — a free, structural (no LLM call) metric that validates
/// disclosure ORDER and penalizes redundant loads / "load storms" — against a real agent answering a
/// real policy question using the shared expense-report skill fixture.
///
/// 🔑 Requires Azure OpenAI credentials.
/// ⏱️ Time to understand: 2 minutes
/// </summary>
public static class AgentSkillsDisclosureEfficiency
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

        var agent = AgentSkillsSampleHelpers.BuildExpenseReportAgent(chatClient, skillPath);

        const string userMessage = "What is our company's policy on dinner expenses? I need to know the exact per-meal cap.";
        Console.WriteLine($"  Model: {AIConfig.ModelDeployment}");
        Console.WriteLine($"  Prompt: \"{userMessage}\"\n");

        var response = await agent.RunAsync(userMessage);
        var toolUsage = AgentSkillsSampleHelpers.ExtractToolUsage(response);
        AgentSkillsSampleHelpers.PrintTrace(toolUsage);

        AgentSkillsSampleHelpers.Check(
            () => toolUsage.Should().HaveLoadedSkill(AgentSkillsSampleHelpers.ExpenseSkillName),
            "HaveLoadedSkill(\"expense-report\")");
        AgentSkillsSampleHelpers.Check(
            () => toolUsage.Should().HaveDisclosedProgressively(),
            "HaveDisclosedProgressively()");

        var metric = new SkillDisclosureEfficiencyMetric();
        var context = new EvaluationContext { Input = userMessage, Output = response.Text, ToolUsage = toolUsage };
        var result = await metric.EvaluateAsync(context);

        Console.WriteLine($"\n  {metric.Name}: {result.Score:F0}/100 ({(result.Passed ? "pass" : "below threshold")})");
        Console.WriteLine($"    {result.Explanation}");

        Console.WriteLine($"\n  Agent said: {AgentSkillsSampleHelpers.Truncate(response.Text, 300)}");
        Console.WriteLine("\n  -> Next: the Compliance Scanner checks the SKILL.md AUTHORING itself, offline.");
        Console.WriteLine("\n=== Agent Skills Disclosure Efficiency Complete ===");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🧩 AGENT SKILLS — DISCLOSURE EFFICIENCY                                      ║
║   Score the load -> read -> run funnel (free, structural, no LLM call)         ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
