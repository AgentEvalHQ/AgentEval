// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF.Skills;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using AgentEval.Skills;
using Microsoft.Agents.AI;

namespace AgentEval.ReadmeSnippets;

/// <summary>README: Agent Skills disclosure assertions, efficiency metric, compliance scan and Security Index.</summary>
public static class AgentSkillsSnippets
{
    public static async Task AgentSkills(TestResult result, string skillPath, AIAgent agent)
    {
        // begin-snippet: agent-skills
        // Assert the disclosure trace like any other tool chain
        result.ToolUsage!.Should()
            .HaveLoadedSkill("expense-report")
            .And().HaveReadSkillResource("expense-report", "resources/policy.md")
                .AfterTool(SkillToolNames.LoadSkill)
            .And().HaveDisclosedProgressively()
            .NotHaveRunSkillScript(because: "a policy lookup doesn't need the compliance script");

        // Score the load -> read -> run funnel (structural, free — no LLM call)
        var efficiency = await new SkillDisclosureEfficiencyMetric().EvaluateAsync(new EvaluationContext
        {
            Input = "n/a", Output = "n/a", ToolUsage = result.ToolUsage,
        });
        Console.WriteLine($"Disclosure efficiency: {efficiency.Score:F0}/100");

        // Scan SKILL.md authoring + governance flags, then roll compliance + efficiency + red-team
        // outcome into one composite score — a missing axis is averaged out, never faked as perfect
        var complianceReport = await MafSkillScanner.ScanFileSkillsAsync(skillPath, agent);
        var index = SkillSecurityIndex.Compute(
            new SkillSecurityIndexInputs(complianceReport, efficiency, Security: null));
        Console.WriteLine($"Skill Security Index: {index.Score:F0}/100 ({index.AxesMeasured}/3 axes measured)");
        // end-snippet
    }
}
