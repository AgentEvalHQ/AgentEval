// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Calibration;
using AgentEval.Core;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample B5: CalibratedEvaluator - Multi-model harness evaluation with criteria-based consensus.
///
/// This demonstrates:
/// - Using CalibratedEvaluator as a drop-in replacement for ChatClientEvaluator
/// - Multi-model majority voting on per-criterion pass/fail
/// - Agreement percentage and per-judge score visibility
/// - Comparing single-judge vs. calibrated evaluation outcomes
///
/// ⏱️ Time to understand: 5 minutes
/// 🔑 Requires: AZURE_OPENAI_ENDPOINT + AZURE_OPENAI_API_KEY
/// </summary>
public static class CalibratedEvaluatorDemo
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("   ❌ This sample requires Azure OpenAI credentials.");
            Console.WriteLine("      Set AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY environment variables.");
            Console.ResetColor();
            return;
        }

        Console.WriteLine("📝 Step 1: Why CalibratedEvaluator?\n");
        Console.WriteLine(@"   MAFEvaluationHarness uses a single LLM to judge pass/fail.
   A single model might:
   • Hallucinate a passing score for a bad response
   • Be overly strict and fail a decent response
   • Give inconsistent results across runs

   CalibratedEvaluator wraps multiple judges behind the IEvaluator interface:
   ✓ Drop-in replacement — same harness, same TestCase, same API
   ✓ Per-criterion Met decided by majority vote
   ✓ OverallScore aggregated via voting strategy (Median, Mean, Weighted)
   ✓ Summary includes agreement percentage for transparency
");

        await RunWithRealModels();

        PrintKeyTakeaways();
    }

    private static async Task RunWithRealModels()
    {
        Console.WriteLine("📝 Step 2: Creating CalibratedEvaluator with real Azure OpenAI judges...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);

        var model1 = AIConfig.ModelDeployment;
        var model2 = !string.IsNullOrEmpty(AIConfig.SecondaryModelDeployment)
            ? AIConfig.SecondaryModelDeployment : model1;

        var client1 = azureClient.GetChatClient(model1).AsIChatClient();
        var client2 = azureClient.GetChatClient(model2).AsIChatClient();
        var client3 = azureClient.GetChatClient(model1).AsIChatClient(); // 3rd instance of primary

        var evaluator = new CalibratedEvaluator(
            new (string, IChatClient)[]
            {
                ($"Judge-A ({model1})", client1),
                ($"Judge-B ({model2})", client2),
                ($"Judge-C ({model1})", client3)
            },
            new CalibratedJudgeOptions { Strategy = VotingStrategy.Median });

        Console.WriteLine($"   Judges: {string.Join(", ", evaluator.JudgeNames)}");
        Console.WriteLine($"   Strategy: {evaluator.Options.Strategy}\n");

        Console.WriteLine("📝 Step 3: Running calibrated criteria evaluation...\n");

        var criteria = new[]
        {
            "Response should confirm the booking",
            "Response should mention the destination city",
            "Response should provide a reference number"
        };

        var result = await evaluator.EvaluateAsync(
            "Book a flight to Paris",
            "Your flight to Paris has been booked successfully! Your confirmation reference is FLT-2026-PARIS-0042. Departure is scheduled for tomorrow at 10:00 AM from Gate B7.",
            criteria);

        DisplayResult(result);
    }

    private static void DisplayResult(EvaluationResult result)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   ┌──────────────────────────────────────────────────┐");
        Console.WriteLine($"   │  Calibrated Score: {result.OverallScore,3}/100                        │");
        Console.WriteLine("   └──────────────────────────────────────────────────┘");
        Console.ResetColor();

        Console.WriteLine("\n   Per-Criterion Results (majority vote):");
        foreach (var criterion in result.CriteriaResults)
        {
            var icon = criterion.Met ? "✅" : "❌";
            Console.WriteLine($"   {icon} {criterion.Criterion}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"      {criterion.Explanation}");
            Console.ResetColor();
        }

        if (result.Improvements.Count > 0)
        {
            Console.WriteLine("\n   💡 Merged Improvements:");
            foreach (var improvement in result.Improvements)
            {
                Console.WriteLine($"      • {improvement}");
            }
        }

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"\n   📊 Summary: {result.Summary}");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║         Sample B5: CalibratedEvaluator (Multi-Model Harness Evaluation)       ║
║                                                                               ║
║   Learn how to:                                                               ║
║   • Replace single-model evaluation with multi-model consensus                ║
║   • Use majority voting for per-criterion pass/fail decisions                 ║
║   • Drop CalibratedEvaluator into MAFEvaluationHarness                        ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintKeyTakeaways()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              🎯 KEY TAKEAWAYS                                   │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  1. CalibratedEvaluator implements IEvaluator — drop-in for harness             │
│                                                                                 │
│  2. Per-criterion Met is decided by MAJORITY VOTE across judges                 │
│                                                                                 │
│  3. OverallScore uses your chosen voting strategy (Median, Mean, Weighted)      │
│                                                                                 │
│  4. Improvements are MERGED from all judges (deduplicated)                      │
│                                                                                 │
│  5. Summary includes agreement % for audit trail                                │
│                                                                                 │
│  6. Usage: var harness = new MAFEvaluationHarness(calibratedEvaluator);         │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}
