// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;

namespace AgentEval.Samples;

/// <summary>
/// AIContextProvider-Based Persistent Memory
/// 
/// This sample demonstrates:
/// - MAF's native AIContextProvider pattern for persistent memory
/// - ProvideAIContextAsync injects stored facts before each LLM call
/// - StoreAIContextAsync extracts and persists facts after each response
/// - The MAF pipeline handles memory injection/extraction transparently
/// - CrossSessionEvaluator works unchanged — it sees IEvaluableAgent
/// 
/// Architecture comparison:
///   Manual (Sample G5): Agent manually manages _longTermMemory + _conversationHistory
///   Native (this sample): AIContextProvider in MAF pipeline handles memory lifecycle
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// 
/// ⏱️ Time to understand: 10 minutes
/// ⏱️ Time to run: ~30–60 seconds (real LLM calls with memory injection)
/// </summary>
public static class MemoryAIContextProvider
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   This sample requires real Azure OpenAI credentials.");
            Console.WriteLine("   Cross-session evaluation uses an LLM judge — it cannot run in mock mode.\n");
            return;
        }

        // Step 1: Create evaluator with real LLM judge
        Console.WriteLine("📝 Step 1: Creating cross-session evaluator with LLM judge...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
        var evaluator = new CrossSessionEvaluator(judge, NullLogger<CrossSessionEvaluator>.Instance);

        Console.WriteLine($"   LLM: {AIConfig.ModelDeployment} at {AIConfig.Endpoint}");
        Console.WriteLine("   ✅ CrossSessionEvaluator ready\n");

        // Step 2: Define facts to plant
        Console.WriteLine("📝 Step 2: Defining facts for cross-session testing...\n");

        var facts = new List<MemoryFact>
        {
            MemoryFact.Create("Patient blood type is O-negative", "medical", 100),
            MemoryFact.Create("Emergency contact is Jane Doe at 555-0199", "contacts", 90),
            MemoryFact.Create("Allergic to penicillin and sulfa drugs", "medical", 100),
            MemoryFact.Create("Preferred language is Spanish", "preferences", 70),
        };

        foreach (var fact in facts)
        {
            Console.WriteLine($"   📌 [{fact.Category ?? "general"}] {fact.Content} (importance: {fact.Importance})");
        }
        Console.WriteLine();

        // Step 3: Create AIContextProvider-backed agent via MAF pipeline
        Console.WriteLine("📝 Step 3: Creating MAF agent with AIContextProvider memory...\n");

        var memoryProvider = new PersistentMemoryProvider();

        var mafAgent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "AIContextProvider Memory Agent",
            AIContextProviders = [memoryProvider],
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a helpful assistant with excellent memory.
                    Remember all facts the user tells you and recall them accurately when asked.
                    When asked about something you know, include the specific details in your response.
                    """
            }
        });

        // Wrap with MAFAgentAdapter — CrossSessionEvaluator sees IEvaluableAgent + ISessionResettableAgent
        var adapter = new MAFAgentAdapter(mafAgent);

        Console.WriteLine("   Pattern: ChatClientAgent + AIContextProvider → MAFAgentAdapter");
        Console.WriteLine("   Memory managed by: PersistentMemoryProvider (ProvideAIContextAsync / StoreAIContextAsync)");
        Console.WriteLine("   Session resets: Clear conversation history, AIContextProvider facts persist\n");

        // Step 4: Evaluate — same API as Sample G5
        Console.WriteLine("📝 Step 4: Running cross-session evaluation...\n");
        Console.WriteLine("   ⏳ Planting facts → resetting sessions → testing recall...\n");

        var result = await evaluator.EvaluateAsync(adapter, facts);

        // Step 5: Display results
        Console.WriteLine("📊 RESULTS:");
        Console.WriteLine(new string('═', 70));

        Console.Write("   Score: ");
        Console.ForegroundColor = result.OverallScore >= 80 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.Write($"{result.OverallScore:F1}%");
        Console.ResetColor();
        Console.WriteLine($"  Passed: {(result.Passed ? "✅" : "❌")}  Session resets: {result.SessionResetCount}");
        Console.WriteLine($"   Retained: {result.RetainedCount}/{result.FactResults.Count} facts");
        Console.WriteLine($"   Duration: {result.Duration.TotalMilliseconds:F0}ms\n");

        if (result.FactResults.Count > 0)
        {
            Console.WriteLine("   Individual Facts:");
            foreach (var factResult in result.FactResults)
            {
                var icon = factResult.Recalled ? "✅" : "❌";
                Console.WriteLine($"     {icon} {factResult.Fact} → Score: {factResult.Score:F0}%");
            }
            Console.WriteLine();
        }

        // Step 6: Show memory provider state
        Console.WriteLine("📝 Step 5: AIContextProvider internal state...\n");
        Console.WriteLine($"   Stored facts: {memoryProvider.StoredFactCount}");
        foreach (var fact in memoryProvider.StoredFacts)
        {
            Console.WriteLine($"     💾 {fact}");
        }
        Console.WriteLine();

        PrintKeyTakeaways();
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🧠 SAMPLE G6: AIContextProvider-Based Persistent Memory                     ║
║   MAF-Native Memory Pipeline Evaluation                                       ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
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
│  1. AIContextProvider replaces manual memory management:                        │
│     class MemoryProvider : AIContextProvider                                    │
│     {                                                                           │
│         ProvideAIContextAsync → inject stored facts into LLM context            │
│         StoreAIContextAsync   → extract & persist facts from response           │
│     }                                                                           │
│                                                                                 │
│  2. Wire into MAF pipeline via ChatClientAgentOptions:                          │
│     new ChatClientAgent(client, new ChatClientAgentOptions                      │
│     {                                                                           │
│         AIContextProviders = [new PersistentMemoryProvider()]                   │
│     });                                                                         │
│                                                                                 │
│  3. Wrap with MAFAgentAdapter → IEvaluableAgent + ISessionResettableAgent:     │
│     var adapter = new MAFAgentAdapter(mafAgent);                               │
│     // CrossSessionEvaluator works unchanged!                                   │
│                                                                                 │
│  4. Memory lifecycle is transparent:                                            │
│     - On session reset: conversation history cleared, provider facts persist    │
│     - On next call: ProvideAIContextAsync injects stored facts automatically   │
│     - AgentEval.Memory evaluators don't need to know about the pipeline        │
│                                                                                 │
│  5. Compare with Sample G5 (manual memory):                                    │
│     Manual: Agent manages _longTermMemory dict + system prompt injection       │
│     Native: AIContextProvider handles injection/extraction in the pipeline      │
│     Both achieve the same result — but native follows MAF best practices       │
│                                                                                 │
└─────────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }
}

/// <summary>
/// A MAF AIContextProvider that implements persistent memory.
/// 
/// This is the native MAF pattern for long-term memory:
/// - ProvideAIContextAsync: Injects stored facts as system messages before each LLM call
/// - StoreAIContextAsync: Extracts facts from user messages after each call
/// 
/// The provider's state persists across session resets because it lives outside
/// the session lifecycle — exactly like a real vector store or knowledge graph would.
/// </summary>
internal sealed class PersistentMemoryProvider : AIContextProvider
{
    private readonly List<string> _storedFacts = new();

    /// <summary>Number of facts currently stored.</summary>
    public int StoredFactCount => _storedFacts.Count;

    /// <summary>All stored facts (for diagnostics/display).</summary>
    public IReadOnlyList<string> StoredFacts => _storedFacts.AsReadOnly();

    /// <summary>
    /// Called before each LLM invocation. Injects stored facts into the AI context
    /// so the model can reference them in its response.
    /// </summary>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        if (_storedFacts.Count == 0)
            return new ValueTask<AIContext>(new AIContext());

        var memoryText = "## Long-Term Memory (facts from previous sessions)\n" +
                         string.Join("\n", _storedFacts.Select(f => $"- {f}")) +
                         "\n\nUse these facts to answer questions accurately.";

        var memoryMessage = new ChatMessage(ChatRole.System, memoryText);

        return new ValueTask<AIContext>(new AIContext
        {
            Messages = [memoryMessage]
        });
    }

    /// <summary>
    /// Called after each LLM invocation. Extracts factual content from user messages
    /// and stores it in the persistent fact store.
    /// </summary>
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        // Extract facts from request messages (user's input)
        foreach (var message in context.RequestMessages)
        {
            if (message.Role != ChatRole.User) continue;

            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var lower = text.ToLowerInvariant();
            if (!lower.Contains("remember") && !lower.Contains("please note") && !lower.Contains("important"))
                continue;

            // Extract content after common prefixes
            var content = text;
            string[] prefixes = ["please remember this important information:", "please remember this:",
                "please remember:", "remember:", "please note:"];

            foreach (var prefix in prefixes)
            {
                var idx = content.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    content = content[(idx + prefix.Length)..].Trim();
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(content) && !_storedFacts.Contains(content))
            {
                _storedFacts.Add(content);
            }
        }

        return default;
    }
}
