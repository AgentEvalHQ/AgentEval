// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using AgentEval.Core;
using AgentEval.Memory.Assertions;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 28: Memory Evaluation Basics - Testing if agents remember facts
///
/// This demonstrates:
/// - Setting up memory evaluation with a real LLM judge
/// - Creating a real LLM-backed agent and testing its memory
/// - Using MemoryJudge for LLM-based fact verification
/// - Fluent memory assertions with detailed results
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
///
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample28_MemoryBasics
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   This sample requires real Azure OpenAI credentials to evaluate memory.");
            Console.WriteLine("   Memory evaluation uses an LLM judge — it cannot run in mock mode.\n");
            return;
        }

        // Step 1: Create the Azure OpenAI chat client
        Console.WriteLine("📝 Step 1: Creating Azure OpenAI chat client...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        Console.WriteLine($"   Endpoint:   {AIConfig.Endpoint}");
        Console.WriteLine($"   Deployment: {AIConfig.ModelDeployment}\n");

        // Step 2: Create memory evaluation components using the real LLM
        var memoryJudge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var memoryRunner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);
        Console.WriteLine("📝 Step 2: Memory judge and test runner created (real LLM-backed)\n");

        // Step 3: Create a real LLM agent with conversation history
        var agent = chatClient.AsEvaluableAgent(
            name: "Memory Agent",
            systemPrompt: """
                You are a helpful assistant with excellent memory.
                Remember all facts the user tells you and recall them accurately when asked.
                When asked about something you were told, include the specific details in your response.
                Keep responses concise but accurate.
                """,
            includeHistory: true);
        Console.WriteLine($"📝 Step 3: Agent '{agent.Name}' created (real LLM with conversation history)\n");

        // Step 4: Define facts to remember and test queries
        var facts = new[]
        {
            MemoryFact.Create("My name is Alice Johnson"),
            MemoryFact.Create("I work as a software engineer"),
            MemoryFact.Create("My favorite programming language is C#")
        };

        var queries = new[]
        {
            MemoryQuery.Create("What is my name?", facts[0]),
            MemoryQuery.Create("What is my job?", facts[1]),
            MemoryQuery.Create("What programming language do I prefer?", facts[2])
        };

        PrintTestDetails(facts, queries);

        // Step 5: Create memory test scenario
        var scenario = new MemoryTestScenario
        {
            Name = "Basic Memory Recall Test",
            Description = "Tests if agent can remember and recall basic personal facts",
            Steps = facts.Select(fact =>
                MemoryStep.Fact($"Please remember: {fact.Content}")
            ).ToArray(),
            Queries = queries
        };
        Console.WriteLine("📝 Step 5: Memory test scenario prepared\n");

        // Step 6: Run the memory evaluation
        Console.WriteLine("📝 Step 6: Running memory evaluation against real LLM...\n");
        var result = await memoryRunner.RunAsync(agent, scenario);

        // Step 7: Display detailed results
        PrintResults(result);

        // Step 8: Fluent assertions — use these in your test suites
        Console.WriteLine("\n📝 Step 8: Fluent memory assertions\n");
        Console.WriteLine("   These assertions integrate with xUnit, NUnit, or MSTest:");
        Console.WriteLine();

        try
        {
            result.Should()
                .HaveOverallScoreAtLeast(70, because: "basic fact recall should work for a well-prompted LLM")
                .NotHaveRecalledForbiddenFacts(because: "no forbidden facts were defined")
                .HaveCompletedWithin(TimeSpan.FromSeconds(60), because: "memory evaluation should be fast");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("   ✅ All assertions passed!");
            Console.ResetColor();
        }
        catch (MemoryAssertionException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ❌ Assertion failed: {ex.Message}");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("   // Example assertion code:");
        Console.WriteLine("   result.Should()");
        Console.WriteLine("       .HaveOverallScoreAtLeast(80)");
        Console.WriteLine("       .HaveAllQueriesPassed()");
        Console.WriteLine("       .NotHaveRecalledForbiddenFacts()");
        Console.WriteLine("       .HaveCompletedWithin(TimeSpan.FromSeconds(30));");
        Console.ResetColor();
        Console.WriteLine();

        PrintKeyTakeaways();
    }

    private static void PrintTestDetails(MemoryFact[] facts, MemoryQuery[] queries)
    {
        Console.WriteLine("📝 Step 4: Memory test defined");
        Console.WriteLine("   Facts to remember:");
        foreach (var fact in facts)
        {
            Console.WriteLine($"     - \"{fact.Content}\"");
        }
        Console.WriteLine("   Queries to test:");
        foreach (var query in queries)
        {
            Console.WriteLine($"     - \"{query.Question}\"");
        }
        Console.WriteLine();
    }

    private static void PrintResults(MemoryEvaluationResult result)
    {
        Console.WriteLine("\n📊 MEMORY EVALUATION RESULTS:");
        Console.WriteLine(new string('═', 70));

        // Overall score with visual indicator
        Console.Write("   Overall Score: ");
        var scoreColor = result.OverallScore >= 80 ? ConsoleColor.Green :
                        result.OverallScore >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.ForegroundColor = scoreColor;
        Console.WriteLine($"{result.OverallScore:F1}% {GetScoreEmoji(result.OverallScore)}");
        Console.ResetColor();

        Console.WriteLine($"   Duration: {result.Duration.TotalMilliseconds:F0}ms");
        Console.WriteLine($"   Facts Found: {result.FoundFacts.Count} | Facts Missing: {result.MissingFacts.Count}");

        if (result.QueryResults.Any())
        {
            Console.WriteLine("\n   Detailed Query Results:");
            Console.WriteLine(new string('─', 50));

            foreach (var queryResult in result.QueryResults)
            {
                var status = queryResult.Score >= 80 ? "✅" : queryResult.Score >= 60 ? "⚠️" : "❌";
                Console.WriteLine($"   {status} Query: \"{queryResult.Query.Question}\"");
                Console.WriteLine($"      Score: {queryResult.Score:F1}%");

                // Show agent's response (truncated)
                var response = queryResult.Response.Length > 100
                    ? queryResult.Response[..97] + "..."
                    : queryResult.Response;
                Console.WriteLine($"      Response: \"{response}\"");
                Console.WriteLine();
            }
        }

        // Show missing facts if any
        if (result.MissingFacts.Any())
        {
            Console.WriteLine("   ⚠️  Missing Facts:");
            foreach (var missingFact in result.MissingFacts)
            {
                Console.WriteLine($"      - {missingFact.Content}");
            }
        }
    }

    private static string GetScoreEmoji(double score)
    {
        return score switch
        {
            >= 90 => "🏆",
            >= 80 => "✅",
            >= 70 => "👍",
            >= 60 => "⚠️",
            _ => "❌"
        };
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine(new string('═', 70));
        Console.WriteLine("🎯 KEY TAKEAWAYS:");
        Console.WriteLine("   • Memory evaluation uses a real LLM judge to verify fact recall");
        Console.WriteLine("   • The agent under test is a real LLM with conversation history");
        Console.WriteLine("   • MemoryJudge analyzes responses for expected facts (not keyword matching)");
        Console.WriteLine("   • Fluent assertions (result.Should()...) integrate with any test framework");
        Console.WriteLine("   • Scores above 80% indicate good memory performance");
        Console.WriteLine("   • Next: Try Sample29_MemoryBenchmark for comprehensive testing");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("🧠 AgentEval Memory - Sample 28: Memory Evaluation Basics");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("Testing whether AI agents remember what you tell them...");
        Console.WriteLine();
    }
}
