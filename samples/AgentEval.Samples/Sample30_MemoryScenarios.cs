// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 30: Memory Scenarios - Targeted memory tests with evaluators
/// 
/// This demonstrates:
/// - Using ReachBackEvaluator to test recall depth through noise
/// - Using ReducerEvaluator to test information retention under compression
/// - Using built-in scenario libraries (chatty, temporal, priority)
/// - Interpreting detailed per-fact results
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample30_MemoryScenarios
{
    public static async Task RunAsync()
    {
        PrintHeader();

        try
        {
            // Create shared components
            var chatClient = new FakeChatClient();
            var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
            var runner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
            var agent = new SimpleMemoryAgent();

            // Test 1: Reach-Back Depth
            await RunReachBackTestAsync(runner, judge, agent);

            // Test 2: Reducer Fidelity
            await RunReducerTestAsync(runner, agent);

            // Test 3: Built-in Scenario Library
            await RunScenarioLibraryTestAsync(runner, agent);

            PrintKeyTakeaways();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static async Task RunReachBackTestAsync(
        MemoryTestRunner runner, MemoryJudge judge, SimpleMemoryAgent agent)
    {
        Console.WriteLine("🎸 TEST 1: Reach-Back Depth Evaluation");
        Console.WriteLine(new string('─', 55));
        Console.WriteLine("   Testing how far back the agent can recall a fact");
        Console.WriteLine("   through layers of conversational noise...\n");

        var evaluator = new ReachBackEvaluator(runner, judge, NullLogger<ReachBackEvaluator>.Instance);

        var fact = MemoryFact.Create("I'm severely allergic to peanuts", "medical", 100);
        var query = MemoryQuery.Create("Do I have any food allergies?", fact);

        var result = await evaluator.EvaluateAsync(
            agent, fact, query,
            depths: [2, 5, 10, 15, 20]);

        Console.WriteLine($"   Fact: \"{fact.Content}\"");
        Console.WriteLine($"   Depths tested: {result.DepthResults.Count}");
        Console.WriteLine($"   Max Reliable Depth: {result.MaxReliableDepth} turns");
        Console.WriteLine($"   Failure Point: {(result.FailurePoint.HasValue ? $"at depth {result.FailurePoint}" : "none (all passed!)")}");
        Console.WriteLine($"   Overall Score: {result.OverallScore:F1}%");
        Console.WriteLine($"   Passed: {(result.Passed ? "✅ Yes" : "❌ No")}\n");

        Console.WriteLine("   Depth Breakdown:");
        foreach (var dr in result.DepthResults)
        {
            var icon = dr.Recalled ? "✅" : "❌";
            Console.WriteLine($"     {icon} Depth {dr.Depth,3}: {dr.Score,6:F1}%  ({dr.Duration.TotalMilliseconds:F0}ms)");
        }
        Console.WriteLine();
    }

    private static async Task RunReducerTestAsync(
        MemoryTestRunner runner, SimpleMemoryAgent agent)
    {
        Console.WriteLine("🔧 TEST 2: Reducer Fidelity Evaluation");
        Console.WriteLine(new string('─', 55));
        Console.WriteLine("   Testing information retention after context compression...\n");

        var evaluator = new ReducerEvaluator(runner, NullLogger<ReducerEvaluator>.Instance);

        var facts = new[]
        {
            MemoryFact.Create("I'm allergic to peanuts", "medical", 100),
            MemoryFact.Create("My meeting is at 3pm tomorrow", "schedule", 80),
            MemoryFact.Create("I prefer dark mode in all apps", "preference", 40),
            MemoryFact.Create("My dog's name is Max", "personal", 60),
            MemoryFact.Create("I take vitamin D supplements daily", "health", 70)
        };

        var result = await evaluator.EvaluateAsync(agent, facts, noiseCount: 15);

        Console.WriteLine($"   Facts tested: {facts.Length}");
        Console.WriteLine($"   Noise turns: 15");
        Console.WriteLine($"   Fidelity Score: {result.FidelityScore:F1}%");
        Console.WriteLine($"   Retained: {result.RetainedCount} | Lost: {result.LostCount}");
        Console.WriteLine($"   Critical Loss: {(result.HasCriticalLoss ? "⚠️ YES" : "✅ No")}");
        Console.WriteLine($"   Passed: {(result.Passed ? "✅ Yes" : "❌ No")}\n");

        Console.WriteLine("   Per-Fact Results:");
        foreach (var fr in result.FactResults)
        {
            var icon = fr.Retained ? "✅" : "❌";
            Console.WriteLine($"     {icon} [{fr.Fact.Importance,3}] \"{fr.Fact.Content}\" — {fr.Score:F0}%");
        }

        if (result.LostFacts.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n   ⚠️  Lost facts (by importance): {string.Join(", ", result.LostFacts.Select(f => f.Content))}");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static async Task RunScenarioLibraryTestAsync(
        MemoryTestRunner runner, SimpleMemoryAgent agent)
    {
        Console.WriteLine("📚 TEST 3: Built-in Scenario Library");
        Console.WriteLine(new string('─', 55));
        Console.WriteLine("   Using pre-built scenarios from the scenario library...\n");

        var scenarios = new MemoryScenarios();
        var chattyScenarios = new ChattyConversationScenarios();

        // Scenario A: Priority memory test
        Console.WriteLine("   Scenario A: Priority Memory Test");
        var highPriority = new[] { MemoryFact.Create("User is allergic to penicillin", "medical", 100) };
        var lowPriority = new[] { MemoryFact.Create("User likes jazz music", "preference", 30) };
        var priorityScenario = scenarios.CreatePriorityMemoryTest(highPriority, lowPriority);

        var priorityResult = await runner.RunAsync(agent, priorityScenario);
        Console.WriteLine($"     Score: {priorityResult.OverallScore:F1}% | Found: {priorityResult.FoundFacts.Count} | Missing: {priorityResult.MissingFacts.Count}");

        // Scenario B: Buried facts in chatty conversation
        Console.WriteLine("   Scenario B: Buried Facts (Chatty Conversation)");
        var importantFacts = new[]
        {
            MemoryFact.Create("My flight departs at 6:30 AM", "travel", 90),
            MemoryFact.Create("My passport expires next month", "travel", 95)
        };
        var buriedScenario = chattyScenarios.CreateBuriedFactsScenario(importantFacts, noiseRatio: 4.0);

        var buriedResult = await runner.RunAsync(agent, buriedScenario);
        Console.WriteLine($"     Score: {buriedResult.OverallScore:F1}% | Steps: {buriedScenario.Steps.Count} | Queries: {buriedScenario.Queries.Count}");

        // Scenario C: Fact Updates
        Console.WriteLine("   Scenario C: Fact Updates (Correction Handling)");
        var initial = new[] { MemoryFact.Create("My phone number is 555-0100") };
        var updated = new[] { MemoryFact.Create("Actually, my new phone number is 555-0200") };
        var updateScenario = scenarios.CreateMemoryUpdateTest(initial, updated);

        var updateResult = await runner.RunAsync(agent, updateScenario);
        Console.WriteLine($"     Score: {updateResult.OverallScore:F1}% | Found: {updateResult.FoundFacts.Count} | Missing: {updateResult.MissingFacts.Count}");

        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine(new string('═', 70));
        Console.WriteLine("🎯 KEY TAKEAWAYS:");
        Console.WriteLine("   • ReachBackEvaluator shows degradation curve as context grows");
        Console.WriteLine("   • ReducerEvaluator detects fact loss from context compression"); 
        Console.WriteLine("   • High-importance fact loss is flagged as 'critical'");
        Console.WriteLine("   • Built-in scenarios cover priority, noise, and update testing");
        Console.WriteLine("   • All evaluators work with any IEvaluableAgent implementation");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("🔬 AgentEval Memory - Sample 30: Memory Scenarios & Evaluators");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("Targeted memory testing with reach-back depth and reducer fidelity...");
        Console.WriteLine();
    }
}
