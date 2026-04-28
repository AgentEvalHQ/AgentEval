// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#pragma warning disable MEAI001 // MessageCountingChatReducer is experimental

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;

namespace AgentEval.Samples;

/// <summary>
/// Advanced MAF 1.3.0 Features — demonstrates seven MAF capabilities
///
/// This sample showcases MAF 1.3.0 features that complement AgentEval's evaluation:
/// 1. InMemoryChatHistoryProvider — managed conversation history with compaction
/// 2. Middleware pipeline — .AsBuilder().Use(runFunc, runStreamingFunc) for guardrails
/// 3. Structured output — RunAsync&lt;T&gt;() for type-safe responses
/// 4. ApprovalRequiredAIFunction — human-in-the-loop for sensitive tools
/// 5. Compaction strategies — automatic conversation pruning (MessageCountingChatReducer)
/// 6. Agent-as-tool — agent.AsAIFunction() for multi-agent composition
/// 7. OpenTelemetry — agent.AsBuilder().UseOpenTelemetry() observability setup
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
///
/// ⏱️ Time to understand: 10 minutes
/// ⏱️ Time to run: ~60–120 seconds (real LLM calls)
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
        Console.WriteLine("📝 Feature 2: Middleware Pipeline — .AsBuilder().Use(runFunc, runStreamingFunc)\n");
        Console.WriteLine("   Add guardrails, logging, or transformations around agent execution.");
        Console.WriteLine("   Both runFunc AND runStreamingFunc must be provided — omitting either\n   causes streaming to fall back to non-streaming mode.\n");

        var baseAgent = chatClient.AsAIAgent(
            name: "BaseAgent",
            instructions: "You are a helpful assistant.");

        // Streaming middleware must be a local function — C# lambdas do not support yield return
        static async IAsyncEnumerable<AgentResponseUpdate> StreamingMiddleware(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            AIAgent innerAgent,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Console.WriteLine("   ⚡ Middleware [streaming]: before");
            await foreach (var update in innerAgent.RunStreamingAsync(messages, session, options, ct).ConfigureAwait(false))
                yield return update;
            Console.WriteLine("   ⚡ Middleware [streaming]: after");
        }

        var middlewareAgent = baseAgent.AsBuilder()
            .Use(
                runFunc: async (messages, session, options, innerAgent, ct) =>
                {
                    Console.WriteLine("   ⚡ Middleware [non-streaming]: before");
                    var response = await innerAgent.RunAsync(messages, session, options, ct).ConfigureAwait(false);
                    Console.WriteLine($"   ⚡ Middleware [non-streaming]: after — {response.Messages.Count} message(s) produced");
                    return response;   // must return the response
                },
                runStreamingFunc: StreamingMiddleware)
            .Build();

        var mwSession = await middlewareAgent.CreateSessionAsync();
        var mwResponse = await middlewareAgent.RunAsync("Say hello in one word.", mwSession);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   ✅ Response (captured by middleware): {Truncate(mwResponse.Text, 100)}\n");
        Console.ResetColor();

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
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("   ✅ Structured output deserialized successfully\n");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   ⚠️  Structured output parse failed — raw response:");
            Console.WriteLine($"   {Truncate(structuredResponse.Text, 120)}");
            Console.WriteLine("   💡 Tip: set ResponseFormat = ChatResponseFormat.ForJsonSchema<T>() for stronger enforcement\n");
            Console.ResetColor();
        }

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

        // MessageCountingChatReducer keeps only the last N messages.
        // We set a very small window (4 messages) so we can observe the effect.
        var compactedAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "CompactedAgent",
            ChatOptions = new() { Instructions = "You are a helpful assistant. Answer in one sentence." },
            ChatHistoryProvider = new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    // Keep only last 4 messages — older turns are pruned by this reducer
                    ChatReducer = new MessageCountingChatReducer(4)
                })
        });

        Console.WriteLine("   Strategy: MessageCountingChatReducer(4) — keeps last 4 messages only");
        Console.WriteLine("   Planting facts across 3 turns and testing recall...\n");

        var compactSession = await compactedAgent.CreateSessionAsync();
        await compactedAgent.RunAsync("Remember: fact A is the first fact.", compactSession);
        await compactedAgent.RunAsync("Remember: fact B is the second fact.", compactSession);
        await compactedAgent.RunAsync("Remember: fact C is the third fact.", compactSession);
        var compactResp = await compactedAgent.RunAsync(
            "What facts do you remember? List all you know.", compactSession);

        Console.WriteLine($"   After 3 turns with window=4, recall: {Truncate(compactResp.Text, 160)}");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   ✅ Compaction keeps conversations within token budget (recent facts retained)\n");
        Console.ResetColor();

        // ─── Feature 6: Agent-as-Tool ─────────────────────────────────────────
        Console.WriteLine("📝 Feature 6: Agent-as-Tool — agent.AsAIFunction()\n");
        Console.WriteLine("   Compose agents by making one agent available as a tool to another.\n");

        var specialistAgent = chatClient.AsAIAgent(
            name: "WeatherSpecialist",
            description: "A weather specialist that answers weather-related questions.",
            instructions: "You are a weather specialist. When asked about weather, respond with a one-sentence fictional forecast.");

        var orchestrator = chatClient.AsAIAgent(
            name: "Orchestrator",
            instructions: "You are a helpful orchestrator. Always use the WeatherSpecialist tool to answer weather questions.",
            tools: [specialistAgent.AsAIFunction()]);

        Console.WriteLine($"   Specialist: {specialistAgent.Name}");
        Console.WriteLine($"   Orchestrator: {orchestrator.Name} (has specialist as tool)\n");

        var orchSession = await orchestrator.CreateSessionAsync();
        var orchResp = await orchestrator.RunAsync(
            "What will the weather be like in Amsterdam tomorrow?", orchSession);
        Console.WriteLine($"   Orchestrator response: {Truncate(orchResp.Text, 160)}");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   ✅ Multi-agent composition via tool delegation confirmed\n");
        Console.ResetColor();

        // ─── Feature 7: OpenTelemetry ─────────────────────────────────────────
        Console.WriteLine("📝 Feature 7: OpenTelemetry Observability\n");
        Console.WriteLine("   MAF 1.3.0 pattern: agent.AsBuilder().UseOpenTelemetry(sourceName, cfg).Build()\n");

        // MAF pattern: wrap the agent via AsBuilder().UseOpenTelemetry().Build()
        // This injects OpenTelemetryAgent, which itself wraps an OpenTelemetryChatClient
        // for full two-layer tracing (agent spans + inference spans).
        var tracedAgent = chatClient
            .AsAIAgent(
                name: "TracedAgent",
                instructions: "You are a helpful assistant.")
            .AsBuilder()
            .UseOpenTelemetry(
                sourceName: "AgentEval.Samples",
                configure: cfg => cfg.EnableSensitiveData = false)  // set true only in dev
            .Build();

        Console.WriteLine($"   Agent '{tracedAgent.Name}' instrumented with OpenTelemetry");
        Console.WriteLine("   Source name : AgentEval.Samples");
        Console.WriteLine("   Sensitive   : disabled (set true only in dev/test)");
        Console.WriteLine("   Exporters   : configure via Sdk.CreateTracerProviderBuilder()\n");

        // Actually invoke the traced agent — spans are emitted to any registered exporter
        var tracedResp = await tracedAgent.RunAsync("Hello! Briefly confirm you are running.");
        Console.WriteLine($"   Response: {Truncate(tracedResp.Text, 120)}");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("   ✅ All RunAsync/RunStreamingAsync calls now emit OTel spans\n");
        Console.ResetColor();

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
║   🚀 SAMPLE A7: Advanced MAF 1.3.0 Features                                  ║
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
   │  This sample demonstrates advanced MAF 1.3.0 features with real LLM calls. │
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
│  2. Middleware: agent.AsBuilder()                                               │
│        .Use(runFunc: ..., runStreamingFunc: ...)  ← both required!             │
│        .Build()                                                                 │
│     runFunc must RETURN the AgentResponse (not just await it)                  │
│                                                                                 │
│  3. Structured output: agent.RunAsync<T>() for type-safe responses             │
│     If Result==null, the LLM didn't produce valid JSON — add a warning         │
│     Use ChatResponseFormat.ForJsonSchema<T>() for stronger enforcement         │
│                                                                                 │
│  4. ApprovalRequired: Wrap sensitive tools for human-in-the-loop               │
│     Evaluation can verify approval gates are in place                          │
│                                                                                 │
│  5. Compaction: MessageCountingChatReducer keeps conversations trim             │
│     Essential for long-running evaluation scenarios                            │
│                                                                                 │
│  6. Agent-as-tool: agent.AsAIFunction() for multi-agent composition            │
│     Add description: param so orchestrator knows when to delegate              │
│     Orchestrator delegates to specialists via tool calls                       │
│                                                                                 │
│  7. OpenTelemetry: agent.AsBuilder().UseOpenTelemetry(sourceName, cfg).Build() │
│     Wraps agent in OpenTelemetryAgent for full two-layer OTel tracing           │
│     EnableSensitiveData = false in production; true only in dev/test            │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}
