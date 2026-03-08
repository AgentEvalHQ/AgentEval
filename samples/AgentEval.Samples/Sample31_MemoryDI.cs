// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 31: DI-Based Memory Evaluation - Using dependency injection for memory services
/// 
/// This demonstrates:
/// - Registering memory services with AddAgentEvalMemory()
/// - Resolving evaluators and scenarios from the DI container
/// - Running evaluations using injected services
/// - The full DI pattern recommended for production use
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample31_MemoryDI
{
    public static async Task RunAsync()
    {
        PrintHeader();

        try
        {
            // Step 1: Configure the DI container
            Console.WriteLine("📝 Step 1: Configuring dependency injection container...\n");
            
            var services = new ServiceCollection();
            
            // Register the fake chat client as IChatClient (in real apps, this would be Azure OpenAI)
            services.AddSingleton<IChatClient>(new FakeChatClient());
            
            // Register all memory services in one call
            services.AddAgentEvalMemory();
            
            // Add null logging (in real apps, use AddLogging with your preferred provider)
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            
            var provider = services.BuildServiceProvider();
            
            Console.WriteLine("   ✅ Services registered:");
            Console.WriteLine("      • AddAgentEvalMemory() — all core + evaluators + scenarios + metrics");
            Console.WriteLine("      • IChatClient → FakeChatClient (simulates LLM judgment)");
            Console.WriteLine();

            // Step 2: Resolve services from DI
            Console.WriteLine("📝 Step 2: Resolving services from container...\n");
            
            using var scope = provider.CreateScope();
            var benchmarkRunner = scope.ServiceProvider.GetRequiredService<IMemoryBenchmarkRunner>();
            var reachBack = scope.ServiceProvider.GetRequiredService<IReachBackEvaluator>();
            var reducer = scope.ServiceProvider.GetRequiredService<IReducerEvaluator>();
            var scenarios = scope.ServiceProvider.GetRequiredService<IMemoryScenarios>();
            
            Console.WriteLine("   ✅ Resolved services:");
            Console.WriteLine("      • IMemoryBenchmarkRunner (orchestrates full benchmarks)");
            Console.WriteLine("      • IReachBackEvaluator (tests recall depth)");
            Console.WriteLine("      • IReducerEvaluator (tests compression fidelity)");
            Console.WriteLine("      • IMemoryScenarios (built-in test scenarios)");
            Console.WriteLine();

            // Step 3: Run a benchmark using DI-resolved services
            Console.WriteLine("📝 Step 3: Running Quick benchmark via DI...\n");
            
            var agent = new SimpleMemoryAgent();
            var result = await benchmarkRunner.RunBenchmarkAsync(agent, MemoryBenchmark.Quick);
            
            PrintBenchmarkSummary(result);

            // Step 4: Run a targeted evaluation
            Console.WriteLine("📝 Step 4: Running targeted reach-back evaluation via DI...\n");
            
            var fact = MemoryFact.Create("Patient is allergic to penicillin", "medical", 100);
            var query = MemoryQuery.Create("Does this patient have any drug allergies?", fact);
            
            var reachBackResult = await reachBack.EvaluateAsync(agent, fact, query, [3, 7, 15]);
            
            Console.WriteLine($"   Reach-back result: MaxReliableDepth={reachBackResult.MaxReliableDepth}, Score={reachBackResult.OverallScore:F1}%");
            Console.WriteLine();

            // Step 5: Demonstrate selective registration
            Console.WriteLine("📝 Step 5: Demonstrating selective DI registration...\n");
            PrintSelectiveRegistration();

            PrintKeyTakeaways();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void PrintBenchmarkSummary(MemoryBenchmarkResult result)
    {
        Console.Write($"   {result.BenchmarkName}: ");
        Console.ForegroundColor = result.OverallScore >= 80 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.Write($"{result.OverallScore:F1}%");
        Console.ResetColor();
        Console.WriteLine($"  Grade: {result.Grade}  {new string('★', result.Stars)}{new string('☆', 5 - result.Stars)}");
        Console.WriteLine();
    }

    private static void PrintSelectiveRegistration()
    {
        Console.WriteLine("   You can also register services selectively:");
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("   // Register everything:");
        Console.WriteLine("   services.AddAgentEvalMemory();");
        Console.WriteLine();
        Console.WriteLine("   // Or register selectively:");
        Console.WriteLine("   services.AddAgentEvalMemoryCore();       // Just runner + judge");
        Console.WriteLine("   services.AddAgentEvalMemoryScenarios();  // Just scenario providers");
        Console.WriteLine("   services.AddAgentEvalMemoryMetrics();    // Just memory metrics");
        Console.WriteLine("   services.AddAgentEvalMemoryTemporal();   // Temporal evaluation");
        Console.WriteLine("   services.AddAgentEvalMemoryEvaluators(); // Advanced evaluators");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine(new string('═', 70));
        Console.WriteLine("🎯 KEY TAKEAWAYS:");
        Console.WriteLine("   • AddAgentEvalMemory() registers all memory services at once");
        Console.WriteLine("   • Use selective methods for minimal registration");
        Console.WriteLine("   • Evaluators are Scoped; scenarios are Singleton; metrics are Transient");
        Console.WriteLine("   • Inject interfaces (IMemoryBenchmarkRunner), not implementations");
        Console.WriteLine("   • In production, replace FakeChatClient with Azure OpenAI");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("💉 AgentEval Memory - Sample 31: DI-Based Memory Evaluation");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("Using dependency injection for memory evaluation services...");
        Console.WriteLine();
    }
}
