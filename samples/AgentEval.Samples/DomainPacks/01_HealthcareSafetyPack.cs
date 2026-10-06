// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.HealthcareSafetyPack;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample O1: Healthcare Safety Pack - a domain pack, five checks for one field composed into one verdict
///
/// This demonstrates:
/// - A domain pack: a CompositeEval of checks written for one field (synthetic healthcare-support cases)
/// - LLM judges for what the agent SAID beside a deterministic check of what it DID (its recorded tool calls)
/// - Applicability as a property of the case: a check the case does not need is not applicable and calls no judge
/// - MinAggregation: one critical failure fails the case, whatever the other checks say
///
/// It runs three of the pack's 15 cases through the same checks and runner as
/// samples/AgentEval.HealthcareSafetyPack, which has all 15, a --calibrate mode that measures the judges against
/// the author's gold labels, and offline tests.
///
/// ⏱️ Time to understand: 5 minutes
/// ⏱️ Time to run: about a minute (3 agent turns, at least 6 judge calls)
/// </summary>
public static class HealthcareSafetyPackSample
{
    // One case per kind of check: an urgent red flag, an action the deployment does not permit, a dose request
    // without the details a safe dose depends on.
    private static readonly string[] CaseIds = ["hc-esc-001", "hc-ab-001", "hc-med-001"];

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   ⚠️  This sample needs a model provider: the agent and the judges are model calls.\n");
            return;
        }
        Console.WriteLine($"   🔗 {AIConfig.Describe()}\n");

        Console.WriteLine("📝 Step 1: Building the pack...\n");
        var dataDir = HealthcareSafetyData.ResolveDataDirectory();
        var scenarios = await HealthcareSafetyData.LoadJsonlAsync<HealthcareScenario>(Path.Combine(dataDir, "scenarios.jsonl"));
        var gold = await HealthcareSafetyData.LoadJsonlAsync<GoldLabel>(Path.Combine(dataDir, "gold.jsonl"));
        var byId = HealthcareSafetyRunner.ValidateFixtures(scenarios, gold);

        var pack = HealthcareSafetyPackFactory.Build(new ChatClientEvaluator(AIConfig.CreateChatClient()), AIConfig.ModelDeployment);
        Console.WriteLine($"   ✓ '{pack.Name}': {pack.Components.Count} checks, {pack.Aggregation.Name} aggregation, no threshold");
        Console.WriteLine("     hc_escalation, hc_medication_safety, hc_source_support, hc_audit_trail: LLM judges");
        Console.WriteLine("     hc_action_boundary: deterministic, reads the tool calls the run recorded\n");

        Console.WriteLine("📝 Step 2: The agent answers, with three tools that only record their calls...\n");
        var agent = new ChatClientBuilder(AIConfig.CreateChatClient()).UseFunctionInvocation().Build();

        Console.WriteLine("📊 RESULTS:");
        Console.WriteLine(new string('─', 60));
        foreach (var id in CaseIds)
        {
            var scenario = byId[id];
            var outcome = await HealthcareSafetyRunner.GradeCaseAsync(pack, scenario, gold.Single(g => g.ScenarioId == id), agent);

            Console.WriteLine($"\n   {id}: \"{scenario.Input}\"");
            if (!outcome.Graded)
            {
                Console.WriteLine($"   ⚠️  Not graded: {outcome.Error}");
                continue;
            }

            Console.WriteLine($"   Agent: {Clip(outcome.Response ?? "", 140)}");
            if (outcome.ToolCalls is { Count: > 0 } calls)
                Console.WriteLine($"   Tools called: {string.Join(", ", calls.Select(c => c.Name))}");
            foreach (var check in CheckKeys.All)
            {
                var score = outcome.Checks[check].Score;
                var mark = score.Label switch { "pass" => "✓", "inapplicable" => "·", _ => "✗" };
                Console.WriteLine($"     {mark} {check,-22} {score.Label}{(score.Passed || score.Label == "inapplicable" ? "" : $" ({score.Severity})")}");
            }
            Console.WriteLine($"   Pack verdict: {outcome.Result!.Score.Label.ToUpperInvariant()}");
            Console.WriteLine($"   Why ({scenario.CheckId}): {Clip(outcome.Target!.Details.Summary ?? "", 160)}");
        }

        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • A domain pack is a CompositeEval: the checks of one field, and one rule for the verdict");
        Console.WriteLine("   • Judge what the agent said; check deterministically what it did (recorded tool calls)");
        Console.WriteLine("   • A check the case does not need is not applicable: left out of the score, no judge called");
        Console.WriteLine("   • Judges are instruments: samples/AgentEval.HealthcareSafetyPack --calibrate measures them");
        Console.WriteLine("     against labelled replies before you trust their verdicts");
        Console.WriteLine("\n🔗 NEXT: dotnet run --project samples/AgentEval.HealthcareSafetyPack (all 15 cases, --calibrate)\n");
    }

    private static string Clip(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  O1. HEALTHCARE SAFETY PACK — a domain pack, synthetic cases                 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("   Synthetic cases only. NOT clinical validation, NOT a medical device.\n");
    }
}
