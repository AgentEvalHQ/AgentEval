// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

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
/// - Controls: the same checks must fail three canned unsafe replies, or their passes mean nothing
///
/// The agent answers three of the pack's 15 cases through the same checks and runner as
/// samples/AgentEval.HealthcareSafetyPack, which has all 15, a --calibrate mode that measures the judges against
/// the author's gold labels, and offline tests. The work is in 01_HealthcareSafetyPack.Run.cs.
///
/// ⏱️ Time to understand: 5 minutes
/// ⏱️ Time to run: about a minute (3 agent conversations, plus a round trip per tool call; 12 judge calls when
/// every case is graded, a judge may retry)
/// </summary>
public static partial class HealthcareSafetyPackSample
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   ⚠️  This sample needs a model provider: the agent and the judges are model calls.\n");
            SampleOutcome.Skip("no chat provider configured");
            return;
        }
        Console.WriteLine($"   🔗 {AIConfig.Describe()}\n");

        var agent = new ChatClientBuilder(AIConfig.CreateChatClient()).UseFunctionInvocation().Build();
        var judge = HealthcareSafetyPackFactory.CreateJudge(AIConfig.CreateChatClient());
        var outcome = await ExecuteAsync(agent, judge, AIConfig.ModelDeployment, Console.Out);
        RecordOutcome(outcome);

        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • A domain pack is a CompositeEval: the checks of one field, and one rule for the verdict");
        Console.WriteLine("   • Judge what the agent said; check deterministically what it did (recorded tool calls)");
        Console.WriteLine("   • A check the case does not need is not applicable: left out of the score, no judge called");
        Console.WriteLine("   • Controls first: a check that cannot fail a reply written to fail it is not measuring anything");
        Console.WriteLine("   • samples/AgentEval.HealthcareSafetyPack --calibrate measures the judges on all 15 labelled replies");
        Console.WriteLine("\n🔗 NEXT: dotnet run --project samples/AgentEval.HealthcareSafetyPack (all 15 cases, --calibrate)\n");
    }

    // The agent's cases are the measurement; the controls say whether the checks can fail at all. A control the
    // checks missed makes every live verdict unreadable, so it is a failure of the sample's own instrument.
    private static void RecordOutcome(Outcome outcome)
    {
        foreach (var live in outcome.Live)
        {
            if (live.Graded)
                SampleOutcome.RecordVerdict($"healthcare case {live.Scenario.ScenarioId}", live.Result!.Score);
            else
                SampleOutcome.Error($"healthcare case {live.Scenario.ScenarioId} was not graded: {live.Error}");
        }

        if (outcome.ControlsMissed > 0)
            SampleOutcome.Fail($"{outcome.ControlsMissed} of {outcome.ControlsMeasured} measured control(s) passed a reply written to fail");
        if (outcome.ControlsMeasured < outcome.Controls.Count)
            SampleOutcome.Skip($"{outcome.Controls.Count - outcome.ControlsMeasured} of {outcome.Controls.Count} control(s) produced no verdict");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  O1. HEALTHCARE SAFETY PACK - a domain pack, synthetic cases                 ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("   Synthetic cases only. NOT clinical validation, NOT a medical device.\n");
    }
}
