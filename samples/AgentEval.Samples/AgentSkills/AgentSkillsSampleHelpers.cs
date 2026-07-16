// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Shared plumbing for the Agent Skills samples (group K): resolving the shared expense-report skill
/// fixture — reused from <c>samples/AgentEval.AgentSkillsEval/skills/expense-report</c> (copied into this
/// project's own output directory at build time via the .csproj's <c>&lt;None&gt;</c> item; the content
/// itself is never duplicated in source), building a real agent wired to it, and the same honest
/// trace/assertion printing helpers every other sample group already uses.
/// </summary>
internal static class AgentSkillsSampleHelpers
{
    public const string ExpenseSkillName = "expense-report";
    public const string PolicyResourceName = "resources/policy.md";
    public const string SummarizeScriptName = "scripts/summarize.csx";

    /// <summary>
    /// Resolves the shared expense-report skill fixture's output path, or returns <see langword="null"/>
    /// with a friendly console message if the project hasn't been built yet (the fixture is copied by
    /// the .csproj — see the comment there).
    /// </summary>
    public static string? ResolveExpenseReportSkillPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "skills", ExpenseSkillName);
        if (Directory.Exists(path))
        {
            return path;
        }

        Console.WriteLine($"  Skill fixture not found at '{path}'.");
        Console.WriteLine("  Build the project so 'skills/**' is copied to the output directory, then re-run.");
        return null;
    }

    /// <summary>
    /// Builds a real agent wired to the shared expense-report skill, all three skill tools
    /// auto-approved so a run completes without a human-in-the-loop pause (a samples-scoped choice,
    /// documented — not a security claim; MAF requires approval for all three by default).
    /// </summary>
    public static AIAgent BuildExpenseReportAgent(IChatClient chatClient, string skillPath)
    {
        var skillsOptions = new AgentSkillsProviderOptions
        {
            DisableLoadSkillApproval = true,
            DisableReadSkillResourceApproval = true,
            DisableRunSkillScriptApproval = true,
        };

        var skillsProvider = new AgentSkillsProvider(skillPath, InProcessScriptRunner, null, skillsOptions);

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "ExpenseAssistant",
            Description = "Answers questions about the corporate expense policy using the expense-report skill.",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a corporate expense assistant. Use the expense-report skill's tools when the " +
                    "task genuinely needs them; do not use them for unrelated questions. Answer directly and concisely.",
            },
            AIContextProviders = [skillsProvider],
        });
    }

    /// <summary>
    /// In-process implementation of <c>scripts/summarize.csx</c> — identical to
    /// samples/AgentEval.AgentSkillsEval/Program.cs's runner. MAF's Agent Skills GA ships no default
    /// subprocess script runner, so <c>run_skill_script</c> needs one supplied to do anything at all.
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

    public static ToolUsageReport ExtractToolUsage(Microsoft.Agents.AI.AgentResponse response) =>
        ToolUsageExtractor.Extract(response.Messages.Cast<object>().ToList());

    public static void PrintTrace(ToolUsageReport toolUsage)
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
    /// prints the assertion's own real message (first line), exactly what a user would see if this
    /// assertion ran inside a unit test.
    /// </summary>
    public static bool Check(Action assertion, string label)
    {
        try
        {
            assertion();
            Console.WriteLine($"  [PASS] {label}");
            return true;
        }
        catch (AgentEvalAssertionException ex)
        {
            var firstLine = ex.Message.Split('\n')[0];
            Console.WriteLine($"  [FAIL] {label}");
            Console.WriteLine($"         {firstLine}");
            return false;
        }
    }

    public static string Truncate(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..max] + "...";
    }
}
