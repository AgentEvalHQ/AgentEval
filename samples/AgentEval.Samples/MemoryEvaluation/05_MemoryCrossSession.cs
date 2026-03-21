// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 32: Cross-Session Memory - Testing fact persistence across session resets
///
/// This demonstrates:
/// - Using a real LLM agent that implements ISessionResettableAgent
/// - Using CrossSessionEvaluator to verify memory survives resets
/// - Comparing agents that do vs don't persist facts across sessions
/// - Understanding the difference between conversation context and long-term memory
///
/// The key insight: a basic LLM agent with conversation history loses everything on session
/// reset. An agent with separate long-term storage retains facts across sessions.
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
///
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class MemoryCrossSession
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
        Console.WriteLine("📝 Step 1: Creating cross-session evaluator with real LLM judge...\n");

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

        // Step 3: Test a basic LLM agent (conversation history only — will fail cross-session)
        Console.WriteLine("📝 Step 3: Testing basic LLM agent (conversation history only)...\n");
        Console.WriteLine("   This agent uses conversation history. On session reset, history is");
        Console.WriteLine("   cleared, so all facts are lost. This is expected to fail.\n");

        var basicAgent = chatClient.AsEvaluableAgent(
            name: "Basic LLM Agent",
            systemPrompt: "You are a helpful assistant. Remember everything the user tells you.",
            includeHistory: true);

        var basicResult = await evaluator.EvaluateAsync(basicAgent, facts);
        PrintCrossSessionResult("Basic LLM Agent (history only)", basicResult);

        // Step 4: Test an agent with persistent long-term memory
        Console.WriteLine("📝 Step 4: Testing LLM agent with persistent long-term memory...\n");
        Console.WriteLine("   This agent stores facts in a separate long-term memory store.");
        Console.WriteLine("   On session reset, conversation history is cleared but facts persist.\n");

        var persistentAgent = new LLMPersistentMemoryAgent(chatClient);
        var persistentResult = await evaluator.EvaluateAsync(persistentAgent, facts);
        PrintCrossSessionResult("LLM Persistent Memory Agent", persistentResult);

        // Step 5: Show comparison
        Console.WriteLine("📝 Step 5: Comparison Summary\n");
        PrintComparison(basicResult, persistentResult);

        PrintKeyTakeaways();
    }

    private static void PrintCrossSessionResult(string agentName, CrossSessionResult result)
    {
        Console.WriteLine($"   Agent: {agentName}");
        Console.Write("   Session Reset Supported: ");
        Console.ForegroundColor = result.SessionResetSupported ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(result.SessionResetSupported ? "Yes" : "No");
        Console.ResetColor();

        if (!result.SessionResetSupported)
        {
            Console.WriteLine($"   ⚠️  {result.ErrorMessage ?? "Agent does not implement ISessionResettableAgent"}");
            Console.WriteLine("   Score: N/A (skipped)");
            Console.WriteLine();
            return;
        }

        Console.Write("   Score: ");
        Console.ForegroundColor = result.OverallScore >= 80 ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.Write($"{result.OverallScore:F1}%");
        Console.ResetColor();
        Console.WriteLine($"  Passed: {(result.Passed ? "✅" : "❌")}");
        Console.WriteLine($"   Retained: {result.RetainedCount}/{result.FactResults.Count} facts  |  Resets: {result.SessionResetCount}");
        Console.WriteLine($"   Duration: {result.Duration.TotalMilliseconds:F0}ms");

        if (result.FactResults.Count > 0)
        {
            Console.WriteLine("\n   Individual Facts:");
            foreach (var factResult in result.FactResults)
            {
                var icon = factResult.Recalled ? "✅" : "❌";
                Console.WriteLine($"     {icon} {factResult.Fact} → Score: {factResult.Score:F0}%");
            }
        }
        Console.WriteLine();
    }

    private static void PrintComparison(CrossSessionResult basic, CrossSessionResult persistent)
    {
        var basicScore = basic.SessionResetSupported ? $"{basic.OverallScore:F1}%" : "N/A";
        var basicRetained = basic.SessionResetSupported
            ? $"{basic.RetainedCount}/{basic.FactResults.Count}"
            : "N/A";

        Console.WriteLine("   ┌──────────────────────────┬──────────────┬──────────────┐");
        Console.WriteLine("   │ Feature                  │ Basic Agent  │ Persistent   │");
        Console.WriteLine("   ├──────────────────────────┼──────────────┼──────────────┤");
        Console.WriteLine($"   │ Session Reset Support    │ {"Yes",-12} │ {"Yes",-12} │");
        Console.WriteLine($"   │ Cross-Session Score      │ {basicScore,-12} │ {persistent.OverallScore:F1}%{"",-7} │");
        Console.WriteLine($"   │ Facts Retained           │ {basicRetained,-12} │ {persistent.RetainedCount + "/" + persistent.FactResults.Count,-12} │");
        Console.WriteLine("   └──────────────────────────┴──────────────┴──────────────┘");
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine(new string('═', 70));
        Console.WriteLine("🎯 KEY TAKEAWAYS:");
        Console.WriteLine("   • ISessionResettableAgent enables cross-session memory testing");
        Console.WriteLine("   • Basic LLM agents lose all memory on session reset");
        Console.WriteLine("   • Agents with separate long-term storage retain facts across sessions");
        Console.WriteLine("   • This mirrors real-world architecture: chat history vs knowledge store");
        Console.WriteLine("   • All scoring is done by a real LLM judge — no keyword tricks");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("🔄 AgentEval Memory - Sample 32: Cross-Session Memory Persistence");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("Testing whether agents remember facts after session resets...");
        Console.WriteLine();
    }
}

/// <summary>
/// An LLM-backed agent with separate long-term memory that persists across session resets.
///
/// Architecture:
/// - Uses a real IChatClient for all responses (no keyword matching)
/// - Stores extracted facts in a separate list (simulates a vector store / knowledge graph)
/// - On session reset: conversation history is cleared, but long-term facts persist
/// - Facts are injected into the system prompt so the LLM can reference them
///
/// This mirrors the real-world MAF pattern where ChatHistoryProvider (session context) and
/// AIContextProvider (long-term memory) serve different purposes.
/// </summary>
internal class LLMPersistentMemoryAgent : IEvaluableAgent, ISessionResettableAgent
{
    private readonly IChatClient _chatClient;
    private readonly List<string> _longTermMemory = new();
    private readonly List<ChatMessage> _conversationHistory = new();

    public string Name => "LLM Persistent Memory Agent";

    public LLMPersistentMemoryAgent(IChatClient chatClient)
    {
        _chatClient = chatClient;
    }

    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        // Build messages with system prompt that includes long-term memory
        var messages = new List<ChatMessage>();

        var systemPrompt = BuildSystemPrompt();
        messages.Add(new ChatMessage(ChatRole.System, systemPrompt));

        // Add conversation history
        messages.AddRange(_conversationHistory);

        // Add current user message
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        // Call the real LLM
        var result = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        var responseText = result.Text ?? string.Empty;

        // Store in conversation history
        _conversationHistory.Add(new ChatMessage(ChatRole.User, prompt));
        var lastMessage = result.Messages.LastOrDefault();
        if (lastMessage != null)
        {
            _conversationHistory.Add(lastMessage);
        }

        // Extract facts from user messages that contain "remember" or look like fact statements
        ExtractAndStoreFacts(prompt);

        return new AgentResponse
        {
            Text = responseText,
            ModelId = result.ModelId,
            TokenUsage = result.Usage != null
                ? new TokenUsage
                {
                    PromptTokens = (int)(result.Usage.InputTokenCount ?? 0),
                    CompletionTokens = (int)(result.Usage.OutputTokenCount ?? 0)
                }
                : null
        };
    }

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        // Clear conversation history but keep long-term memory
        _conversationHistory.Clear();
        return Task.CompletedTask;
    }

    private string BuildSystemPrompt()
    {
        var prompt = """
            You are a helpful assistant with excellent memory.
            Remember all facts the user tells you and recall them accurately when asked.
            When asked about something you know, include the specific details in your response.
            """;

        if (_longTermMemory.Count > 0)
        {
            prompt += "\n\n## Long-Term Memory (facts from previous sessions)\n";
            prompt += "The following facts were learned in previous conversations:\n";
            foreach (var fact in _longTermMemory)
            {
                prompt += $"- {fact}\n";
            }
            prompt += "\nUse these facts to answer questions accurately.";
        }

        return prompt;
    }

    private void ExtractAndStoreFacts(string prompt)
    {
        // Store the user's message as a fact if it contains factual information.
        // In a real system, this would use an LLM to extract facts (like Mem0 does),
        // or store embeddings in a vector database.
        var lower = prompt.ToLowerInvariant();
        if (lower.Contains("remember") || lower.Contains("please note") || lower.Contains("important"))
        {
            // Extract content after common prefixes
            var content = prompt;
            string[] prefixes = ["please remember this:", "please remember:", "remember:",
                "please remember this important information:", "please note:"];

            foreach (var prefix in prefixes)
            {
                var idx = content.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    content = content[(idx + prefix.Length)..].Trim();
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(content) && !_longTermMemory.Contains(content))
            {
                _longTermMemory.Add(content);
            }
        }
    }
}
