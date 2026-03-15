// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 30: Memory Scenarios - Targeted memory tests with evaluators
///
/// This demonstrates:
/// - Using ReachBackEvaluator to test recall depth through noise
/// - Using ReducerEvaluator to test information retention under compression
/// - Using built-in scenario libraries (chatty, priority, updates)
/// - Interpreting detailed per-fact results
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
///
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample30_MemoryScenarios
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   This sample requires real Azure OpenAI credentials.");
            Console.WriteLine("   Memory evaluators use an LLM judge — they cannot run in mock mode.\n");
            return;
        }

        // Create shared components — all backed by a real LLM
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);

        Console.WriteLine($"   LLM: {AIConfig.ModelDeployment} at {AIConfig.Endpoint}\n");

        // Test 1: Reach-Back Depth
        await RunReachBackTestAsync(runner, judge, chatClient);

        // Test 2: Reducer Fidelity
        await RunReducerTestAsync(runner, chatClient);

        // Test 3: Built-in Scenario Library
        await RunScenarioLibraryTestAsync(runner, chatClient);

        PrintKeyTakeaways();
    }

    private static async Task RunReachBackTestAsync(
        MemoryTestRunner runner, MemoryJudge judge, IChatClient chatClient)
    {
        Console.WriteLine("📏 TEST 1: Reach-Back Depth Evaluation");
        Console.WriteLine(new string('─', 55));
        Console.WriteLine("   Problem: As conversation grows, agents lose track of early facts.");
        Console.WriteLine("   This test plants a fact, then adds N noise turns before asking.");
        Console.WriteLine("   The depth where recall fails reveals the agent's effective memory.\n");

        var evaluator = new ReachBackEvaluator(runner, judge, NullLogger<ReachBackEvaluator>.Instance);

        // Create a fresh agent for this test (clean conversation history)
        var agent = chatClient.AsEvaluableAgent(
            name: "ReachBack Agent",
            systemPrompt: """
                You are a helpful assistant. Remember everything the user tells you.
                When asked a question, answer based on what you remember from the conversation.
                """,
            includeHistory: true);

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
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("   Interpretation: Depth 5 = 5 noise turns between fact and query.");
        Console.WriteLine("   Depth 10+ reliable = good. Depth 20+ = excellent context handling.");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static async Task RunReducerTestAsync(
        MemoryTestRunner runner, IChatClient chatClient)
    {
        Console.WriteLine("🗜️ TEST 2: Reducer Fidelity Evaluation");
        Console.WriteLine(new string('─', 55));
        Console.WriteLine("   Problem: Many agents compress/summarize old conversation history.");
        Console.WriteLine("   This test checks if important facts survive that compression.\n");

        var evaluator = new ReducerEvaluator(runner, NullLogger<ReducerEvaluator>.Instance);

        // Create a fresh agent for this test
        var agent = chatClient.AsEvaluableAgent(
            name: "Reducer Agent",
            systemPrompt: """
                You are a helpful assistant. Remember all facts the user shares with you,
                especially medical information, schedules, and personal preferences.
                When asked, recall these facts accurately.
                """,
            includeHistory: true);

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
        MemoryTestRunner runner, IChatClient chatClient)
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

        var agentA = chatClient.AsEvaluableAgent(
            name: "Priority Agent",
            systemPrompt: "You are a helpful assistant. Remember everything the user tells you.",
            includeHistory: true);

        var priorityResult = await runner.RunAsync(agentA, priorityScenario);
        Console.WriteLine($"     Score: {priorityResult.OverallScore:F1}% | Found: {priorityResult.FoundFacts.Count} | Missing: {priorityResult.MissingFacts.Count}");

        // Scenario B: Buried facts in chatty conversation
        Console.WriteLine("   Scenario B: Buried Facts (Chatty Conversation)");
        var importantFacts = new[]
        {
            MemoryFact.Create("My flight departs at 6:30 AM", "travel", 90),
            MemoryFact.Create("My passport expires next month", "travel", 95)
        };
        var buriedScenario = chattyScenarios.CreateBuriedFactsScenario(importantFacts, noiseRatio: 4.0);

        var agentB = chatClient.AsEvaluableAgent(
            name: "Buried Facts Agent",
            systemPrompt: "You are a helpful assistant. Pay attention to important details in conversation and remember them.",
            includeHistory: true);

        var buriedResult = await runner.RunAsync(agentB, buriedScenario);
        Console.WriteLine($"     Score: {buriedResult.OverallScore:F1}% | Steps: {buriedScenario.Steps.Count} | Queries: {buriedScenario.Queries.Count}");

        // Scenario C: Fact Updates
        Console.WriteLine("   Scenario C: Fact Updates (Correction Handling)");
        var initial = new[] { MemoryFact.Create("My phone number is 555-0100") };
        var updated = new[] { MemoryFact.Create("Actually, my new phone number is 555-0200") };
        var updateScenario = scenarios.CreateMemoryUpdateTest(initial, updated);

        var agentC = chatClient.AsEvaluableAgent(
            name: "Update Agent",
            systemPrompt: "You are a helpful assistant. When the user corrects information, update your understanding accordingly.",
            includeHistory: true);

        var updateResult = await runner.RunAsync(agentC, updateScenario);
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
        Console.WriteLine("   • Each test gets a fresh agent to isolate evaluation");
        Console.WriteLine("   • All scoring is done by a real LLM judge — no keyword hacks");
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
