// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Testing;

namespace AgentEval.Samples;

/// <summary>
/// Sample C1: Conversation Evaluation - Multi-turn agent interactions
/// 
/// This demonstrates:
/// - Creating a MAF AIAgent (ChatClientAgent) and wrapping it with MAFAgentAdapter
/// - Using ConversationalTestCase to define multi-turn scenarios
/// - Using ConversationRunner to execute and validate agent conversations
/// - Fluent builder API for conversation test cases
/// - Assertion results for tool usage, completeness, and duration
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class ConversationEvaluation
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   🤖 Model: {AIConfig.ModelDeployment}\n");

        var agent = CreateConversationAgent();
        var adapter = new MAFAgentAdapter(agent);
        var runner = new ConversationRunner(adapter);

        Console.WriteLine($"   🤖 Agent: {adapter.Name}\n");

        await RunSimpleConversation(runner);
        await RunConversationWithExpectations(runner);
        PrintKeyTakeaways();
    }

    private static async Task RunSimpleConversation(ConversationRunner runner)
    {
        Console.WriteLine("💬 PART 1: Simple multi-turn conversation\n");

        var testCase = ConversationalTestCase.Create("Geography Quiz")
            .WithDescription("Test context retention across turns")
            .AddUserTurn("What is the capital of France?")
            .AddUserTurn("What is its population?")
            .AddUserTurn("Name a famous landmark there.")
            .WithMaxDuration(TimeSpan.FromSeconds(30))
            .Build();

        Console.WriteLine($"   Test: {testCase.Name}");
        Console.WriteLine($"   Turns: {testCase.Turns.Count} user messages\n");

        var result = await runner.RunAsync(testCase);

        PrintConversationResult(result);
    }

    private static async Task RunConversationWithExpectations(ConversationRunner runner)
    {
        Console.WriteLine("\n💬 PART 2: Conversation with tool expectations\n");

        var testCase = ConversationalTestCase.Create("Travel Planning")
            .WithDescription("Validate multi-turn travel planning flow")
            .InCategory("E2E")
            .AddUserTurn("I want to plan a trip to Tokyo for 5 days in March.")
            .AddUserTurn("What are the must-see attractions?")
            .AddUserTurn("What should I pack for the weather?")
            .WithMaxDuration(TimeSpan.FromSeconds(45))
            .Build();

        Console.WriteLine($"   Test: {testCase.Name} ({testCase.Category})");
        Console.WriteLine($"   Description: {testCase.Description}");
        Console.WriteLine($"   Turns: {testCase.Turns.Count} user messages\n");

        var result = await runner.RunAsync(testCase);

        PrintConversationResult(result);
    }

    private static void PrintConversationResult(ConversationResult result)
    {
        Console.Write("   Result: ");
        if (result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("✅ PASSED");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("❌ FAILED");
        }
        Console.ResetColor();
        Console.WriteLine($" ({result.Duration.TotalMilliseconds:F0}ms)\n");

        // Show turn details
        Console.WriteLine("   📋 Conversation turns:");
        foreach (var turn in result.ActualTurns)
        {
            var icon = turn.Role == "user" ? "👤" : turn.Role == "assistant" ? "🤖" : "⚙️";
            var content = turn.Content.Length > 80 ? turn.Content[..80] + "..." : turn.Content;
            Console.WriteLine($"      {icon} [{turn.Role}] {content}");
        }

        // Show assertions
        if (result.Assertions.Count > 0)
        {
            Console.WriteLine("\n   📊 Assertions:");
            foreach (var assertion in result.Assertions)
            {
                var icon = assertion.Passed ? "✅" : "❌";
                Console.Write($"      {icon} {assertion.Name}");
                if (!assertion.Passed && assertion.Message != null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write($" — {assertion.Message}");
                    Console.ResetColor();
                }
                Console.WriteLine();
            }
        }

        if (result.ToolsCalled.Count > 0)
            Console.WriteLine($"\n   🔧 Tools called: {string.Join(", ", result.ToolsCalled)}");

        if (result.Error != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n   ⚠️ Error: {result.Error}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Creates a MAF ChatClientAgent for multi-turn conversation evaluation.
    /// </summary>
    private static AIAgent CreateConversationAgent()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        return chatClient.AsAIAgent(
            name: "ConversationAgent",
            instructions: """
                You are a helpful, knowledgeable conversational assistant.
                Answer questions thoroughly but concisely.
                Remember context from earlier in the conversation.
                """);
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   💬 SAMPLE C1: CONVERSATION EVALUATION                                      ║
║   Multi-turn testing with ConversationRunner + ConversationalTestCase         ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  ⚠️  SKIPPING SAMPLE C1 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample runs real multi-turn conversations against an AI agent.       │
   │                                                                             │
   │  Set these environment variables:                                           │
   │    AZURE_OPENAI_ENDPOINT     - Your Azure OpenAI endpoint                   │
   │    AZURE_OPENAI_API_KEY      - Your API key                                 │
   │    AZURE_OPENAI_DEPLOYMENT   - Chat model (e.g., gpt-4o)                    │
   └─────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • ChatClientAgent (MAF) creates the AI agent");
        Console.WriteLine("   • MAFAgentAdapter wraps AIAgent → IEvaluableAgent for evaluation");
        Console.WriteLine("   • ConversationRunner accepts any IEvaluableAgent");
        Console.WriteLine("   • ConversationalTestCase.Create() provides a fluent builder API");
        Console.WriteLine("   • Built-in assertions check tool usage, completeness, and duration");
        Console.WriteLine("   • Multi-turn tests catch context drift and memory issues");
        Console.WriteLine("\n🔗 NEXT: Run Sample C2 to see workflow evaluation!\n");
    }
}
