// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using AgentEval.Core;
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
/// - Registering a real Azure OpenAI client as IChatClient in DI
/// - Registering memory services with AddAgentEvalMemory()
/// - Resolving evaluators and scenarios from the DI container
/// - Running evaluations using injected services
/// - The full DI pattern recommended for production use
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
///
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class MemoryDI
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   This sample requires real Azure OpenAI credentials.");
            Console.WriteLine("   DI registration needs a real IChatClient for the LLM judge.\n");
            return;
        }

        // Step 1: Configure the DI container with real Azure OpenAI
        Console.WriteLine("📝 Step 1: Configuring dependency injection container...\n");

        var services = new ServiceCollection();

        // Register the real Azure OpenAI chat client as IChatClient
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        services.AddSingleton<IChatClient>(chatClient);

        // Register all memory services in one call
        services.AddAgentEvalMemory();

        // Add null logging (in real apps, use AddLogging with your preferred provider)
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var provider = services.BuildServiceProvider();

        Console.WriteLine("   ✅ Services registered:");
        Console.WriteLine($"      • IChatClient → Azure OpenAI ({AIConfig.ModelDeployment})");
        Console.WriteLine("      • AddAgentEvalMemory() — all core + evaluators + scenarios + metrics");
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

        // Step 3: Use CanRemember one-liner extensions (powered by DI services)
        Console.WriteLine("📝 Step 3: One-liner memory tests via CanRemember extensions...\n");

        var agent = chatClient.AsEvaluableAgent(
            name: "DI Memory Agent",
            systemPrompt: """
                You are a helpful assistant with excellent memory.
                Remember all facts the user tells you and recall them accurately when asked.
                """,
            includeHistory: true);

        // One-liner: test if agent can remember a fact
        var canRememberResult = await agent.CanRememberAsync(
            "My blood type is AB positive",
            question: "What is my blood type?",
            serviceProvider: scope.ServiceProvider);

        Console.WriteLine($"   CanRememberAsync: Score={canRememberResult.OverallScore:F1}% " +
            $"({(canRememberResult.OverallScore >= 80 ? "✅ Remembered" : "❌ Forgot")})");

        // One-liner: test recall through noise
        var noiseResult = await agent.CanRememberThroughNoiseAsync(
            ["I'm allergic to shellfish"],
            distractionTurns: 5,
            serviceProvider: scope.ServiceProvider);

        Console.WriteLine($"   CanRememberThroughNoiseAsync: Score={noiseResult.OverallScore:F1}% " +
            $"(through 5 noise turns)");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("   // One-liner API examples:");
        Console.WriteLine("   await agent.CanRememberAsync(\"My name is Alice\");");
        Console.WriteLine("   await agent.CanRememberThroughNoiseAsync([\"fact1\", \"fact2\"], distractionTurns: 10);");
        Console.WriteLine("   await agent.QuickMemoryCheckAsync(\"My name is Alice\"); // No LLM judge needed");
        Console.ResetColor();
        Console.WriteLine();

        // Step 4: Run a targeted evaluation using DI-resolved evaluator
        Console.WriteLine("📝 Step 4: Running targeted reach-back evaluation via DI...\n");

        // Create a fresh agent for the reach-back test
        var reachBackAgent = chatClient.AsEvaluableAgent(
            name: "ReachBack Agent",
            systemPrompt: "You are a helpful assistant. Remember everything you are told.",
            includeHistory: true);

        var fact = MemoryFact.Create("Patient is allergic to penicillin", "medical", 100);
        var query = MemoryQuery.Create("Does this patient have any drug allergies?", fact);

        var reachBackResult = await reachBack.EvaluateAsync(reachBackAgent, fact, query, [3, 7, 15]);

        Console.WriteLine($"   Reach-back result: MaxReliableDepth={reachBackResult.MaxReliableDepth}, Score={reachBackResult.OverallScore:F1}%");
        Console.WriteLine();

        // Step 5: Demonstrate selective registration
        Console.WriteLine("📝 Step 5: Demonstrating selective DI registration...\n");
        PrintSelectiveRegistration();

        PrintKeyTakeaways();
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
        Console.WriteLine("   • Register a real IChatClient (Azure OpenAI) for the LLM judge");
        Console.WriteLine("   • AddAgentEvalMemory() registers all memory services at once");
        Console.WriteLine("   • CanRememberAsync() one-liners use DI services automatically");
        Console.WriteLine("   • Evaluators are Scoped; scenarios are Singleton; metrics are Transient");
        Console.WriteLine("   • Use selective methods (AddAgentEvalMemoryCore, etc.) for minimal registration");
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
