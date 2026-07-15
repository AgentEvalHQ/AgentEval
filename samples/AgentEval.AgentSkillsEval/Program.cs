// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// AgentEval × MAF Agent Skills — Phase 1 live sample: real Azure OpenAI agent, real file-based skill
// (skills/expense-report), real tool-call trace. Demonstrates the five skill assertions
// (AgentEval.Assertions.SkillUsageAssertions) and the progressive-disclosure efficiency metric
// (AgentEval.Metrics.Agentic.SkillDisclosureEfficiencyMetric) across three runs that each exercise a
// different, real assertion/output combination:
//
//   Run 1 — read-only policy lookup   : load_skill -> read_skill_resource (no script)
//   Run 2 — script-computed overage   : load_skill -> run_skill_script (a real "S" stage)
//   Run 3 — off-topic task            : the skill genuinely is not needed; demonstrates the metric's
//                                        honest vacuous pass AND an assertion's real FAILURE path
//                                        (printed in full — never swallowed, never a false checkmark)
//
// Scope note: this sample covers Phase 1 only (assertions + metric). It does NOT include the P2
// compliance scanner or the P3 skill-injection red-team / run_skill_script governance gates — those
// land with later phases per strategy/FutureFeatures/Skills/AgentEval-AgentSkills-Evals-Design-and-Plan.md.

using System.Text.Json;
using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using AgentEval.Skills;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.AgentSkillsEval;

public static class Program
{
    private const string SkillName = "expense-report";
    private const string PolicyResourceName = "resources/policy.md";
    private const string SummarizeScriptName = "scripts/summarize.csx";

    public static async Task Main()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        var skillPath = Path.Combine(AppContext.BaseDirectory, "skills", SkillName);
        if (!Directory.Exists(skillPath))
        {
            Console.WriteLine($"Skill fixture not found at '{skillPath}'.");
            Console.WriteLine("Build the project so 'skills/**' is copied to the output directory, then re-run.");
            return;
        }

        var chatClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential)
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();
        Console.WriteLine($"Model: {AIConfig.ModelDeployment}");
        Console.WriteLine($"Skill fixture: {skillPath}\n");

        await Run1_ReadOnlyPolicyLookup(chatClient, skillPath);
        await Run2_ScriptComputedOverage(chatClient, skillPath);
        await Run3_SkillNotNeeded(chatClient, skillPath);

        Console.WriteLine("\n=== Agent Skills Eval — Phase 1 sample complete ===");
    }

    // ------------------------------------------------------------------------------------------
    // Run 1 — read-only policy lookup: load_skill -> read_skill_resource, NO script execution.
    // ------------------------------------------------------------------------------------------
    private static async Task Run1_ReadOnlyPolicyLookup(IChatClient chatClient, string skillPath)
    {
        PrintScene("Run 1", "Read-only policy lookup — expect load_skill -> read_skill_resource, no script");

        var agent = BuildAgent(chatClient, skillPath);
        const string userMessage = "What is our company's policy on dinner expenses? I need to know the exact per-meal cap.";
        Console.WriteLine($"  Prompt: \"{userMessage}\"\n");

        var response = await agent.RunAsync(userMessage);
        var toolUsage = ExtractToolUsage(response);
        PrintTrace(toolUsage);

        Check(() => toolUsage.Should().HaveLoadedSkill(SkillName), "HaveLoadedSkill(\"expense-report\")");
        Check(() => toolUsage.Should().HaveReadSkillResource(SkillName, PolicyResourceName).AfterTool(SkillToolNames.LoadSkill),
            "HaveReadSkillResource(\"expense-report\", \"resources/policy.md\").AfterTool(load_skill)");
        Check(() => toolUsage.Should().HaveDisclosedProgressively(), "HaveDisclosedProgressively()");
        Check(() => toolUsage.Should().NotHaveRunSkillScript(because: "a policy lookup does not require running the compliance script"),
            "NotHaveRunSkillScript(because: \"a policy lookup does not require running the script\")");

        await RunMetric(toolUsage);
        Console.WriteLine($"\n  Agent said: {Truncate(response.Text, 300)}\n");
    }

    // ------------------------------------------------------------------------------------------
    // Run 2 — needs the script: load_skill -> run_skill_script (a real "S" / run_skill_script stage).
    // ------------------------------------------------------------------------------------------
    private static async Task Run2_ScriptComputedOverage(IChatClient chatClient, string skillPath)
    {
        PrintScene("Run 2", "Script-computed overage — expect load_skill -> run_skill_script");

        var agent = BuildAgent(chatClient, skillPath);
        const string userMessage =
            "I spent $200 on a client dinner. Use the expense-report skill's script to tell me EXACTLY " +
            "how much I am over the policy cap, in dollars.";
        Console.WriteLine($"  Prompt: \"{userMessage}\"\n");

        var response = await agent.RunAsync(userMessage);
        var toolUsage = ExtractToolUsage(response);
        PrintTrace(toolUsage);

        Check(() => toolUsage.Should().HaveLoadedSkill(SkillName), "HaveLoadedSkill(\"expense-report\")");
        Check(() => toolUsage.Should().HaveRunSkillScript(SkillName, SummarizeScriptName).AfterTool(SkillToolNames.LoadSkill),
            "HaveRunSkillScript(\"expense-report\", \"scripts/summarize.csx\").AfterTool(load_skill)");
        Check(() => toolUsage.Should().HaveDisclosedProgressively(), "HaveDisclosedProgressively()");

        await RunMetric(toolUsage);
        Console.WriteLine($"\n  Agent said: {Truncate(response.Text, 300)}\n");
    }

    // ------------------------------------------------------------------------------------------
    // Run 3 — the skill genuinely isn't needed: demonstrates the metric's honest vacuous pass AND an
    // assertion's real FAILURE path (no false checkmark; the failure message is printed in full).
    // ------------------------------------------------------------------------------------------
    private static async Task Run3_SkillNotNeeded(IChatClient chatClient, string skillPath)
    {
        PrintScene("Run 3", "Off-topic task — the skill is not needed; demonstrates an honest assertion FAILURE");

        var agent = BuildAgent(chatClient, skillPath);
        const string userMessage = "In one sentence, what is the capital of France?";
        Console.WriteLine($"  Prompt: \"{userMessage}\"\n");

        var response = await agent.RunAsync(userMessage);
        var toolUsage = ExtractToolUsage(response);
        PrintTrace(toolUsage);

        // Deliberately asserted even though this prompt should NOT trigger the skill — the point is
        // to show the assertion's real failure message, not to fabricate a passing scenario.
        Console.WriteLine("  Deliberately asserting HaveLoadedSkill(\"expense-report\") on an off-topic prompt");
        Console.WriteLine("  (expected to FAIL on a well-behaved model — this is the assertion's real, helpful failure message):\n");
        Check(() => toolUsage.Should().HaveLoadedSkill(SkillName), "HaveLoadedSkill(\"expense-report\") [expected to fail here]");

        await RunMetric(toolUsage);
        Console.WriteLine($"\n  Agent said: {Truncate(response.Text, 300)}\n");
    }

    // ------------------------------------------------------------------------------------------
    // Shared plumbing
    // ------------------------------------------------------------------------------------------

    private static AIAgent BuildAgent(IChatClient chatClient, string skillPath)
    {
        // Phase 1 scope: no Gatekeeper approval/exec gates yet (that lands with Phase 3). MAF's
        // three skill tools require human approval BY DEFAULT (verified this session); auto-approving
        // all three here — via AgentSkillsProviderOptions.DisableXApproval, also verified this
        // session — lets a real run complete end-to-end without a human-in-the-loop pause. This is
        // documented honestly, not hidden: it is a Phase-1-scoped sample choice, not a security claim.
        var skillsOptions = new AgentSkillsProviderOptions
        {
            DisableLoadSkillApproval = true,
            DisableReadSkillResourceApproval = true,
            DisableRunSkillScriptApproval = true,
        };

        var skillsProvider = new AgentSkillsProvider(
            skillPath,
            scriptRunner: InProcessScriptRunner,
            fileOptions: null,
            options: skillsOptions);

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "ExpenseAssistant",
            Description = "Answers questions about the corporate expense policy using the expense-report skill.",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    "You are a corporate expense assistant. Use the expense-report skill's tools when the " +
                    "task genuinely needs them; do not use them for unrelated questions. Answer directly and concisely.",
            },
            AIContextProviders = [skillsProvider],
        });
    }

    /// <summary>
    /// In-process implementation of <c>scripts/summarize.csx</c> — see that file's header comment
    /// for why: MAF's Agent Skills GA ships NO default subprocess script runner (verified against the
    /// live 1.13.0 assembly this session; there is no <c>SubprocessScriptRunner</c> type), so a caller
    /// must supply an <see cref="AgentFileSkillScriptRunner"/> for <c>run_skill_script</c> to do
    /// anything. This keeps the sample dependency-free (no external interpreter required) while still
    /// exercising a REAL <c>run_skill_script</c> tool call end-to-end.
    /// </summary>
    private static Task<object?> InProcessScriptRunner(
        AgentFileSkill skill, AgentFileSkillScript script, JsonElement? arguments,
        IServiceProvider? serviceProvider, CancellationToken cancellationToken)
    {
        const double dinnerCap = 150.0;
        double amount = 0;
        if (arguments is { ValueKind: JsonValueKind.Object } args && args.TryGetProperty("amount", out var amountEl))
        {
            amount = amountEl.GetDouble();
        }

        var overage = Math.Max(0, amount - dinnerCap);
        string result = overage > 0
            ? $"Meal amount ${amount:F2} vs policy cap ${dinnerCap:F2} -> OVER by ${overage:F2}."
            : $"Meal amount ${amount:F2} vs policy cap ${dinnerCap:F2} -> within policy.";
        return Task.FromResult<object?>(result);
    }

    private static ToolUsageReport ExtractToolUsage(Microsoft.Agents.AI.AgentResponse response) =>
        ToolUsageExtractor.Extract(response.Messages.Cast<object>().ToList());

    private static void PrintTrace(ToolUsageReport toolUsage)
    {
        Console.WriteLine($"  Real trace ({toolUsage.Count} tool call(s)):");
        if (toolUsage.Count == 0)
        {
            Console.WriteLine("    (none)");
            return;
        }

        foreach (var call in toolUsage.Calls.OrderBy(c => c.Order))
        {
            var executedTag = call.WasExecuted ? "" : "  [emitted, no paired result observed]";
            Console.WriteLine($"    {call.Order}. {call.Name}({call.GetArgumentsAsJson()}){executedTag}");
        }
    }

    /// <summary>
    /// Runs one assertion and prints an honest pass/fail line — never a bare success claim. A failure
    /// prints the assertion's own real message (first line), which is exactly what a user would see
    /// if this assertion ran inside a unit test.
    /// </summary>
    private static void Check(Action assertion, string label)
    {
        try
        {
            assertion();
            Console.WriteLine($"  [PASS] {label}");
        }
        catch (AgentEvalAssertionException ex)
        {
            var firstLine = ex.Message.Split('\n')[0];
            Console.WriteLine($"  [FAIL] {label}");
            Console.WriteLine($"         {firstLine}");
        }
    }

    private static async Task RunMetric(ToolUsageReport toolUsage)
    {
        var metric = new SkillDisclosureEfficiencyMetric();
        var context = new EvaluationContext
        {
            Input = "n/a",
            Output = "n/a",
            ToolUsage = toolUsage,
        };
        var result = await metric.EvaluateAsync(context);
        Console.WriteLine($"\n  {metric.Name}: {result.Score:F0}/100 ({(result.Passed ? "pass" : "below threshold")})");
        Console.WriteLine($"    {result.Explanation}");
    }

    private static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..max] + "...";
    }

    private static void PrintScene(string label, string description)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"=== {label}: {description} ===");
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
+----------------------------------------------------------------------+
|  MAF Agent Skills Eval — Phase 1 (assertions + disclosure efficiency) |
+----------------------------------------------------------------------+");
        Console.ResetColor();
    }
}
