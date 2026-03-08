// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using AgentEval.Memory.Temporal;
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
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample29_MemoryBenchmark
{
    public static async Task RunAsync()
    {
        PrintHeader();

        try
        {
            // Step 1: Create all memory evaluation components
            var chatClient = new FakeChatClient();
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

            PrintStepComplete("Step 1", "All memory evaluation components created");

            // Step 2: Create the test agent
            var agent = new SimpleMemoryAgent();
            PrintStepComplete("Step 2", $"Test agent '{agent.Name}' ready");

            // Step 3: Run the Quick benchmark
            Console.WriteLine("📝 Step 3: Running Quick memory benchmark (3 categories)...\n");
            var quickResult = await benchmarkRunner.RunBenchmarkAsync(agent, MemoryBenchmark.Quick);
            PrintBenchmarkResult(quickResult);

            // Step 4: Run the Standard benchmark
            Console.WriteLine("\n📝 Step 4: Running Standard memory benchmark (6 categories)...\n");
            var standardResult = await benchmarkRunner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard);
            PrintBenchmarkResult(standardResult);

            // Step 5: Run the Full benchmark (includes cross-session)
            Console.WriteLine("\n📝 Step 5: Running Full memory benchmark (8 categories)...\n");
            var fullResult = await benchmarkRunner.RunBenchmarkAsync(agent, MemoryBenchmark.Full);
            PrintBenchmarkResult(fullResult);

            PrintKeyTakeaways();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.ResetColor();

            if (ex.InnerException != null)
                Console.WriteLine($"   Inner: {ex.InnerException.Message}");
        }
    }

    private static void PrintBenchmarkResult(MemoryBenchmarkResult result)
    {
        Console.WriteLine($"   Benchmark: {result.BenchmarkName}");
        Console.WriteLine($"   Duration:  {result.Duration.TotalMilliseconds:F0}ms");
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

    private static void PrintStepComplete(string step, string message)
    {
        Console.WriteLine($"📝 {step}: {message}\n");
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 70));
        Console.WriteLine("🎯 KEY TAKEAWAYS:");
        Console.WriteLine("   • Quick benchmark (3 cats) is ideal for CI pipelines");
        Console.WriteLine("   • Standard benchmark (6 cats) covers core memory capabilities");
        Console.WriteLine("   • Full benchmark (8 cats) includes cross-session and reducer tests");
        Console.WriteLine("   • Skipped categories don't penalize the overall score");
        Console.WriteLine("   • Recommendations are actionable and category-specific");
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
