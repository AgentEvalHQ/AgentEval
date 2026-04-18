// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#pragma warning disable MEAI001 // MessageCountingChatReducer is experimental

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Advanced MAF 1.1.0 Features — demonstrates seven MAF capabilities
///
/// This sample showcases MAF 1.1.0 features that complement AgentEval's evaluation:
/// 1. InMemoryChatHistoryProvider — managed conversation history with compaction
/// 2. Middleware pipeline — .AsBuilder().Use(...) for guardrails
/// 3. Structured output — RunAsync&lt;T&gt;() for type-safe responses
/// 4. ApprovalRequiredAIFunction — human-in-the-loop for sensitive tools
/// 5. Compaction strategies — automatic conversation pruning
/// 6. Agent-as-tool — agent.AsAIFunction() for multi-agent composition
/// 7. OpenTelemetry — observability setup
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
///
/// ⏱️ Time to understand: 10 minutes
/// ⏱️ Time to run: ~30–60 seconds (real LLM calls)
/// </summary>
public static class AdvancedMAFFeatures
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

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        // ─── Feature 1: InMemoryChatHistoryProvider ───────────────────────────
        Console.WriteLine("📝 Feature 1: InMemoryChatHistoryProvider\n");
        Console.WriteLine("   MAF manages conversation history automatically, with optional compaction.\n");

        var historyAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "HistoryManagedAgent",
            ChatOptions = new() { Instructions = "You are a helpful assistant. Keep responses concise." },
            // ChatHistoryProvider stores conversation turns and applies compaction
            ChatHistoryProvider = new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new MessageCountingChatReducer(20) // Keep last 20 messages
                })
        });

        var historySession = await historyAgent.CreateSessionAsync();
        var r1 = await historyAgent.RunAsync("My favorite color is blue.", historySession);
        Console.WriteLine($"   Turn 1: {Truncate(r1.Text, 100)}");
        var r2 = await historyAgent.RunAsync("What is my favorite color?", historySession);
        Console.WriteLine($"   Turn 2: {Truncate(r2.Text, 100)}");
        Console.ForegroundColor = r2.Text?.Contains("blue", StringComparison.OrdinalIgnoreCase) == true
            ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"   ✅ History retained across turns\n");
        Console.ResetColor();

        // ─── Feature 2: Middleware Pipeline ───────────────────────────────────
        Console.WriteLine("📝 Feature 2: Middleware Pipeline — .AsBuilder().Use(...)\n");
        Console.WriteLine("   Add guardrails, logging, or transformations around agent execution.\n");

        var baseAgent = chatClient.AsAIAgent(
            name: "BaseAgent",
            instructions: "You are a helpful assistant.");

        var middlewareAgent = baseAgent.AsBuilder()
            .Use(async (messages, session, options, next, ct) =>
            {
                Console.WriteLine("   ⚡ Middleware: before agent execution");
                await next(messages, session, options, ct).ConfigureAwait(false);
                Console.WriteLine("   ⚡ Middleware: after agent execution");
            })
            .Build();

        var mwSession = await middlewareAgent.CreateSessionAsync();
        var mwResponse = await middlewareAgent.RunAsync("Say hello in one word.", mwSession);
        Console.WriteLine($"   Response: {Truncate(mwResponse.Text, 100)}\n");

        // ─── Feature 3: Structured Output ─────────────────────────────────────
        Console.WriteLine("📝 Feature 3: Structured Output — RunAsync<T>()\n");
        Console.WriteLine("   Get type-safe deserialized responses from the agent.\n");

        var structuredAgent = chatClient.AsAIAgent(
            name: "StructuredAgent",
            instructions: "You are a helpful assistant that provides structured data. Always respond with valid JSON.");

        var structuredSession = await structuredAgent.CreateSessionAsync();
        var structuredResponse = await structuredAgent.RunAsync<CityInfo>(
            "Give me a JSON object with name and population for Paris.", structuredSession);
        if (structuredResponse.Result is not null)
        {
            Console.WriteLine($"   City: {structuredResponse.Result.Name}");
            Console.WriteLine($"   Population: {structuredResponse.Result.Population:N0}");
        }
        else
        {
            Console.WriteLine($"   Raw: {Truncate(structuredResponse.Text, 100)}");
        }
        Console.WriteLine();

        // ─── Feature 4: ApprovalRequiredAIFunction ────────────────────────────
        Console.WriteLine("📝 Feature 4: ApprovalRequiredAIFunction\n");
        Console.WriteLine("   Wrap sensitive tools to require human approval before execution.\n");

        var transferTool = AIFunctionFactory.Create(TransferFunds);
        var approvalRequired = new ApprovalRequiredAIFunction(transferTool);

        Console.WriteLine($"   Original tool: {transferTool.Name}");
        Console.WriteLine($"   Wrapped tool:  {approvalRequired.Name} (requires approval)");
        Console.WriteLine($"   ✅ Sensitive operations now gated — evaluation can verify this\n");

        // ─── Feature 5: Compaction Strategies ─────────────────────────────────
        Console.WriteLine("📝 Feature 5: Compaction — automatic conversation pruning\n");
        Console.WriteLine("   For long conversations, compaction keeps context manageable.\n");

        // MessageCountingChatReducer keeps only the last N messages
        var compactedAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "CompactedAgent",
            ChatOptions = new() { Instructions = "You are a helpful assistant." },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    // Keep only last 10 messages — older ones are pruned
                    ChatReducer = new MessageCountingChatReducer(10)
                })
        });

        Console.WriteLine("   Strategy: MessageCountingChatReducer(10) — keeps last 10 messages");
        Console.WriteLine("   ✅ Long conversations stay within token budget\n");

        // ─── Feature 6: Agent-as-Tool ─────────────────────────────────────────
        Console.WriteLine("📝 Feature 6: Agent-as-Tool — agent.AsAIFunction()\n");
        Console.WriteLine("   Compose agents by making one agent available as a tool to another.\n");

        var specialistAgent = chatClient.AsAIAgent(
            name: "WeatherSpecialist",
            instructions: "You are a weather specialist. When asked about weather, respond with a brief forecast.");

        var orchestrator = chatClient.AsAIAgent(
            name: "Orchestrator",
            instructions: "You are a helpful orchestrator. Use the available specialist tools to answer questions.",
            tools: [specialistAgent.AsAIFunction()]);

        Console.WriteLine($"   Specialist: {specialistAgent.Name}");
        Console.WriteLine($"   Orchestrator: {orchestrator.Name} (has specialist as tool)");
        Console.WriteLine($"   ✅ Multi-agent composition via tool delegation\n");

        // ─── Feature 7: OpenTelemetry ─────────────────────────────────────────
        Console.WriteLine("📝 Feature 7: OpenTelemetry Observability\n");
        Console.WriteLine("   Enable tracing on workflows via WorkflowBuilder.WithOpenTelemetry().\n");

        Console.WriteLine("   // Example (requires a workflow):");
        Console.WriteLine("   // var workflow = Workflow.CreateBuilder()");
        Console.WriteLine("   //     .AddAgent(agent)");
        Console.WriteLine("   //     .WithOpenTelemetry(cfg => cfg.EnableSensitiveData = true)");
        Console.WriteLine("   //     .Build();");
        Console.WriteLine("   ✅ Workflow operations emit OpenTelemetry spans for observability\n");

        PrintKeyTakeaways();
    }

    [System.ComponentModel.Description("Transfer funds between accounts")]
    private static string TransferFunds(string fromAccount, string toAccount, decimal amount)
        => $"Transferred ${amount} from {fromAccount} to {toAccount}";

    private record CityInfo(string Name, long Population);

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "(empty)";
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🚀 SAMPLE A7: Advanced MAF 1.1.0 Features                                  ║
║   ChatHistoryProvider, Middleware, Structured Output, Approval,                ║
║   Compaction, Agent-as-Tool, OpenTelemetry                                    ║
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
   │  ⚠️  SKIPPING SAMPLE A7 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample demonstrates advanced MAF 1.1.0 features with real LLM calls. │
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
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
┌─────────────────────────────────────────────────────────────────────────────────┐
│                              🎯 KEY TAKEAWAYS                                   │
├─────────────────────────────────────────────────────────────────────────────────┤
│                                                                                 │
│  1. InMemoryChatHistoryProvider: Automatic history + compaction                 │
│     Use new ChatClientAgent() with ChatHistoryProvider option                  │
│     (Cannot use .AsAIAgent() when ChatHistoryProvider is needed)               │
│                                                                                 │
│  2. Middleware: agent.AsBuilder().Use(...).Build()                              │
│     Add guardrails, logging, transformations around execution                  │
│                                                                                 │
│  3. Structured output: agent.RunAsync<T>() for type-safe responses             │
│     Combines LLM with JSON deserialization                                      │
│                                                                                 │
│  4. ApprovalRequired: Wrap sensitive tools for human-in-the-loop               │
│     Evaluation can verify approval gates are in place                          │
│                                                                                 │
│  5. Compaction: MessageCountingChatReducer keeps conversations trim             │
│     Essential for long-running evaluation scenarios                            │
│                                                                                 │
│  6. Agent-as-tool: agent.AsAIFunction() for multi-agent composition            │
│     Orchestrator delegates to specialists via tool calls                       │
│                                                                                 │
│  7. OpenTelemetry: agent.WithOpenTelemetry() for observability                 │
│     One-line setup emits spans for all agent operations                        │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}
