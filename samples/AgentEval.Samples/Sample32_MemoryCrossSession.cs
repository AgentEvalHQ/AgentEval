// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 32: Cross-Session Memory - Testing fact persistence across session resets
/// 
/// This demonstrates:
/// - Implementing ISessionResettableAgent for session boundary testing
/// - Using CrossSessionEvaluator to verify memory survives resets
/// - Comparing agents that do vs don't support cross-session memory
/// - Understanding the difference between session context and long-term memory
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample32_MemoryCrossSession
{
    public static async Task RunAsync()
    {
        PrintHeader();

        try
        {
            // Step 1: Create evaluator
            Console.WriteLine("📝 Step 1: Creating cross-session evaluator...\n");

            var chatClient = new FakeChatClient();
            var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
            var evaluator = new CrossSessionEvaluator(judge, NullLogger<CrossSessionEvaluator>.Instance);
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

            // Step 3: Test a non-resettable agent (should report "not supported")
            Console.WriteLine("📝 Step 3: Testing non-resettable agent...\n");
            
            var basicAgent = new SimpleMemoryAgent();
            var basicResult = await evaluator.EvaluateAsync(basicAgent, facts);
            
            PrintCrossSessionResult("SimpleMemoryAgent (no reset)", basicResult);

            // Step 4: Test a resettable agent with persistent memory
            Console.WriteLine("📝 Step 4: Testing resettable agent with persistent memory...\n");
            
            var persistentAgent = new PersistentMemoryAgent();
            var persistentResult = await evaluator.EvaluateAsync(persistentAgent, facts);
            
            PrintCrossSessionResult("PersistentMemoryAgent (with reset)", persistentResult);

            // Step 5: Show comparison
            Console.WriteLine("📝 Step 5: Comparison Summary\n");
            PrintComparison(basicResult, persistentResult);

            PrintKeyTakeaways();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    private static void PrintCrossSessionResult(string agentName, CrossSessionResult result)
    {
        Console.WriteLine($"   Agent: {agentName}");
        Console.Write($"   Session Reset Supported: ");
        Console.ForegroundColor = result.SessionResetSupported ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(result.SessionResetSupported ? "Yes" : "No");
        Console.ResetColor();

        if (!result.SessionResetSupported)
        {
            Console.WriteLine($"   ⚠️  {result.ErrorMessage ?? "Agent does not implement ISessionResettableAgent"}");
            Console.WriteLine($"   Score: N/A (skipped)");
            Console.WriteLine();
            return;
        }

        Console.Write($"   Score: ");
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
        Console.WriteLine("   ┌──────────────────────────┬──────────────┬──────────────┐");
        Console.WriteLine("   │ Feature                  │ Basic Agent  │ Persistent   │");
        Console.WriteLine("   ├──────────────────────────┼──────────────┼──────────────┤");
        Console.WriteLine($"   │ Session Reset Support    │ {"No",-12} │ {"Yes",-12} │");
        Console.WriteLine($"   │ Cross-Session Score      │ {"N/A",-12} │ {persistent.OverallScore:F1}%{"",-7} │");
        Console.WriteLine($"   │ Facts Retained           │ {"N/A",-12} │ {persistent.RetainedCount + "/" + persistent.FactResults.Count,-12} │");
        Console.WriteLine("   └──────────────────────────┴──────────────┴──────────────┘");
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine(new string('═', 70));
        Console.WriteLine("🎯 KEY TAKEAWAYS:");
        Console.WriteLine("   • ISessionResettableAgent enables cross-session memory testing");
        Console.WriteLine("   • ResetSessionAsync clears conversation but preserves long-term memory");
        Console.WriteLine("   • Non-resettable agents are gracefully skipped (not errored)");
        Console.WriteLine("   • Use successThreshold to control pass/fail sensitivity");
        Console.WriteLine("   • Real agents: Chat history is context, learned facts are memory");
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
/// An agent that supports session reset with persistent long-term memory.
/// Conversation history is cleared on reset, but stored facts persist.
/// </summary>
internal class PersistentMemoryAgent : IEvaluableAgent, ISessionResettableAgent
{
    public string Name => "Persistent Memory Agent";
    
    private readonly List<string> _longTermMemory = new();
    private readonly List<string> _conversationHistory = new();
    private int _resetCount;

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        _conversationHistory.Add(prompt);
        var response = ProcessPrompt(prompt);
        
        return Task.FromResult(new AgentResponse
        {
            Text = response,
            TokenUsage = new TokenUsage
            {
                PromptTokens = prompt.Length / 4,
                CompletionTokens = response.Length / 4
            }
        });
    }

    public Task ResetSessionAsync(CancellationToken cancellationToken = default)
    {
        // Clear conversation history but keep long-term memory
        _conversationHistory.Clear();
        _resetCount++;
        return Task.CompletedTask;
    }

    private string ProcessPrompt(string prompt)
    {
        var lower = prompt.ToLowerInvariant();

        if (lower.Contains("remember") || lower.Contains("please note") || lower.Contains("important"))
        {
            StoreFact(prompt);
            return "I've stored that in my long-term memory.";
        }

        if (lower.Contains('?') || lower.Contains("recall") || lower.Contains("what"))
        {
            return AnswerFromMemory(lower);
        }

        return "Understood.";
    }

    private void StoreFact(string prompt)
    {
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

        if (!string.IsNullOrWhiteSpace(content))
            _longTermMemory.Add(content);
    }

    private string AnswerFromMemory(string question)
    {
        var matching = _longTermMemory
            .Where(fact => HasOverlap(question, fact.ToLowerInvariant()))
            .ToList();

        if (matching.Count > 0)
            return string.Join(" ", matching);

        if (_longTermMemory.Count > 0 && (question.Contains("remember") || question.Contains("know")))
            return "Here's what I remember: " + string.Join(". ", _longTermMemory);

        return "I don't have that information stored.";
    }

    private static bool HasOverlap(string question, string fact)
    {
        HashSet<string> stopWords = ["what", "is", "my", "do", "you", "the", "a", "an", "i", 
            "me", "have", "any", "about", "know", "remember", "does", "can", "to", "of", "in",
            "for", "on", "with", "at", "by", "from", "or", "and", "not", "no", "but", "if",
            "are", "was", "were", "has", "had", "will", "would", "could", "should", "please",
            "tell", "information", "this", "that"];

        var qWords = question.Split([' ', '?', '.', ',', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToHashSet();

        var fWords = fact.Split([' ', '.', ',', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToHashSet();

        return qWords.Overlaps(fWords);
    }
}
