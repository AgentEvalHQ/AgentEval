// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using AgentEval.Memory.Temporal;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 29: Memory Benchmark Suite - Comprehensive agent memory scoring
///
/// This demonstrates:
/// - Running the Quick/Standard/Full memory benchmark presets
/// - Getting grades, stars, and actionable recommendations
/// - Interpreting benchmark results for agent improvement
/// - Using the benchmark runner with all evaluators
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
///
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class MemoryBenchmarkDemo
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   This sample requires real Azure OpenAI credentials.");
            Console.WriteLine("   Memory benchmarks use an LLM judge — they cannot run in mock mode.\n");
            return;
        }

        // Step 1: Create the Azure OpenAI chat client and all evaluation components
        Console.WriteLine("📝 Step 1: Creating Azure OpenAI client and evaluation components...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var runner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);

        // Create scenario providers
        var memoryScenarios = new MemoryScenarios();
        var chattyScenarios = new ChattyConversationScenarios();
        var temporalScenarios = new TemporalMemoryScenarios();

        // Create evaluators
        var reachBack = new ReachBackEvaluator(runner, judge, NullLogger<ReachBackEvaluator>.Instance);
        var reducer = new ReducerEvaluator(runner, NullLogger<ReducerEvaluator>.Instance);
        var crossSession = new CrossSessionEvaluator(judge, NullLogger<CrossSessionEvaluator>.Instance);

        // Create the benchmark runner
        var benchmarkRunner = new MemoryBenchmarkRunner(
            runner, judge, reachBack, reducer, crossSession,
            memoryScenarios, chattyScenarios, temporalScenarios,
            NullLogger<MemoryBenchmarkRunner>.Instance);

        Console.WriteLine($"   Endpoint:   {AIConfig.Endpoint}");
        Console.WriteLine($"   Deployment: {AIConfig.ModelDeployment}");
        Console.WriteLine("   Components: MemoryJudge, TestRunner, ReachBack, Reducer, CrossSession");
        Console.WriteLine("   Scenarios:  Memory, Chatty, Temporal\n");

        // Step 2: Create a real LLM agent to benchmark
        var agent = chatClient.AsEvaluableAgent(
            name: "Memory Agent",
            systemPrompt: """
                You are a helpful assistant with excellent memory.
                Remember all facts the user tells you and recall them accurately when asked.
                When asked about something you were told, include the specific details in your response.
                Keep responses concise but accurate.
                """,
            includeHistory: true);
        Console.WriteLine($"📝 Step 2: Agent '{agent.Name}' ready (real LLM with conversation history)\n");

        // Step 3: Explain the benchmark tiers
        Console.WriteLine("📝 Step 3: Available benchmark tiers\n");
        Console.WriteLine("   ┌───────────┬────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("   │ Tier      │ Categories                                                   │");
        Console.WriteLine("   ├───────────┼────────────────────────────────────────────────────────────────┤");
        Console.WriteLine("   │ Quick (3) │ BasicRetention, TemporalReasoning, NoiseResilience            │");
        Console.WriteLine("   │ Std   (6) │ + ReachBackDepth, FactUpdateHandling, MultiTopic              │");
        Console.WriteLine("   │ Full  (8) │ + CrossSession, ReducerFidelity                               │");
        Console.WriteLine("   └───────────┴────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();
        Console.WriteLine("   Use Quick for CI, Standard for staging, Full for pre-release.\n");

        // Step 4: Run uickthe Q benchmark (good balance of coverage vs speed)
        Console.WriteLine("📝 Step 4: Running Quick memory benchmark (3 categories)...\n");
        var result = await benchmarkRunner.RunBenchmarkAsync(agent, MemoryBenchmark.Quick);
        PrintBenchmarkResult(result);

        PrintKeyTakeaways();
    }

    private static void PrintBenchmarkResult(MemoryBenchmarkResult result)
    {
        Console.WriteLine($"   Benchmark: {result.BenchmarkName}");
        Console.WriteLine($"   Duration:  {result.Duration.TotalSeconds:F1}s");
        Console.WriteLine();

        // Overall score with grade and stars
        Console.Write("   Overall: ");
        Console.ForegroundColor = result.OverallScore >= 80 ? ConsoleColor.Green :
                                  result.OverallScore >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{result.OverallScore:F1}%");
        Console.ResetColor();
        Console.WriteLine($"  Grade: {result.Grade}  {new string('★', result.Stars)}{new string('☆', 5 - result.Stars)}  {(result.Passed ? "✅ PASSED" : "❌ FAILED")}");
        Console.WriteLine();

        // Category breakdown
        Console.WriteLine("   Category Results:");
        Console.WriteLine("   " + new string('─', 60));
        foreach (var cat in result.CategoryResults)
        {
            if (cat.Skipped)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"   ⏭️  {cat.CategoryName,-25} SKIPPED ({cat.SkipReason})");
                Console.ResetColor();
            }
            else
            {
                var icon = cat.Score >= 80 ? "✅" : cat.Score >= 60 ? "⚠️" : "❌";
                Console.Write($"   {icon} {cat.CategoryName,-25} ");
                Console.ForegroundColor = cat.Score >= 80 ? ConsoleColor.Green :
                                          cat.Score >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
                Console.Write($"{cat.Score,6:F1}%");
                Console.ResetColor();
                Console.WriteLine($"  {new string('★', cat.Stars)}{new string('☆', 5 - cat.Stars)}  (weight: {cat.Weight:P0})");
            }
        }

        // Weak categories
        if (result.WeakCategories.Count > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   ⚠️  Weak categories: {string.Join(", ", result.WeakCategories)}");
            Console.ResetColor();
        }

        // Skipped categories
        if (result.SkippedCategories.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"   ⏭️  Skipped: {string.Join(", ", result.SkippedCategories)}");
            Console.ResetColor();
        }

        // Recommendations
        Console.WriteLine();
        Console.WriteLine("   📋 Recommendations:");
        foreach (var rec in result.Recommendations)
        {
            Console.WriteLine($"      • {rec}");
        }
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 70));
        Console.WriteLine("🎯 KEY TAKEAWAYS:");
        Console.WriteLine("   • Quick (3 cats) for CI, Standard (6) for staging, Full (8) for releases");
        Console.WriteLine("   • All evaluations use a real LLM judge for accurate scoring");
        Console.WriteLine("   • Grades (A-F), stars (1-5), and recommendations are automatic");
        Console.WriteLine("   • Weak categories tell you exactly where to focus improvement");
        Console.WriteLine("   • Next: Try Sample30_MemoryScenarios for targeted testing");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("🏆 AgentEval Memory - Sample 29: Memory Benchmark Suite");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("Comprehensive agent memory scoring with grades and recommendations...");
        Console.WriteLine();
    }
}
