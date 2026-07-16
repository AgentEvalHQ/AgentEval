// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.MAF.Skills;
using AgentEval.Skills;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Agent Skills — Compliance Scanner.
///
/// A static, offline scan of the shared expense-report skill's <c>SKILL.md</c> authoring and governance
/// flags (<c>MafSkillScanner</c> + <c>SkillComplianceValidator</c>) — no model call happens during the
/// scan itself; a minimal agent is only needed to satisfy MAF's <c>AgentSkillsSourceContext</c>. Renders
/// the same coverage report Phase 4a's Skill Security Index consumes as its compliance axis.
///
/// 🔑 Requires Azure OpenAI credentials (to construct the minimal scan-context agent; the scan itself is free).
/// ⏱️ Time to understand: 1 minute
/// </summary>
public static class AgentSkillsComplianceScanner
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

        // ScanFileSkillsAsync needs an AIAgent only to build the AgentSkillsSourceContext MAF's
        // GetSkillsAsync requires; no model call is made by the scan itself (static file inspection).
        var scanAgent = new ChatClientAgent(
            new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential).GetChatClient(AIConfig.ModelDeployment).AsIChatClient(),
            new ChatClientAgentOptions { Name = "ScannerAgent" });

        Console.WriteLine($"  Scanning: {skillPath}\n");
        var report = await MafSkillScanner.ScanFileSkillsAsync(skillPath, scanAgent);

        Console.WriteLine(SkillComplianceReportRenderer.RenderConsole(report));

        var highCount = report.Findings.Count(f => f.Severity == Severity.High);
        Console.WriteLine($"\n  [{(report.IsCompliant ? "PASS" : "FAIL")}] IsCompliant == true " +
            $"({report.Findings.Count} finding(s), {highCount} High)");

        Console.WriteLine("\n  -> Next: the Skill Security Index joins THIS report with the efficiency metric into one score.");
        Console.WriteLine("\n=== Agent Skills Compliance Scanner Complete ===");
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║   🧩 AGENT SKILLS — COMPLIANCE SCANNER                                         ║
║   Static SKILL.md + governance-flag scan — offline, no model call              ║
╚═══════════════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }
}
