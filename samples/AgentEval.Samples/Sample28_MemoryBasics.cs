// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 28: Memory Evaluation Basics - Testing if agents remember facts
/// 
/// This demonstrates:
/// - Setting up memory evaluation without complex DI
/// - Testing basic fact recall with MemoryTestRunner
/// - Using MemoryJudge for LLM-based fact verification
/// - Fluent memory assertions with detailed results
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample28_MemoryBasics
{
    public static async Task RunAsync()
    {
        PrintHeader();

        try
        {
            // Step 1: Create memory evaluation components
            var chatClient = new FakeChatClient();
            var memoryJudge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);
            var memoryRunner = new MemoryTestRunner(memoryJudge, NullLogger<MemoryTestRunner>.Instance);
            PrintStepComplete("Step 1", "Memory evaluation components created");

            // Step 2: Create a memory-enabled test agent
            var agent = new SimpleMemoryAgent();
            PrintStepComplete("Step 2", $"Memory agent '{agent.Name}' created");

            // Step 3: Define facts to remember and test queries
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

            // Step 4: Create memory test scenario
            var scenario = CreateMemoryScenario(facts, queries);
            PrintStepComplete("Step 4", "Memory test scenario prepared");

            // Step 5: Run the memory evaluation
            Console.WriteLine("📝 Step 5: Running memory evaluation...");
            var result = await memoryRunner.RunAsync(agent, scenario);
            
            // Step 6: Display detailed results
            PrintResults(result);
            PrintKeyTakeaways();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Error during memory evaluation: {ex.Message}");
            Console.ResetColor();
            
            if (ex.InnerException != null)
            {
                Console.WriteLine($"   Inner exception: {ex.InnerException.Message}");
            }
        }
    }

    private static MemoryTestScenario CreateMemoryScenario(MemoryFact[] facts, MemoryQuery[] queries)
    {
        var steps = facts.Select(fact => 
            MemoryStep.Fact($"Please remember: {fact.Content}")
        ).ToArray();
        
        return new MemoryTestScenario
        {
            Name = "Basic Memory Recall Test",
            Description = "Tests if agent can remember and recall basic personal facts",
            Steps = steps,
            Queries = queries
        };
    }

    private static void PrintTestDetails(MemoryFact[] facts, MemoryQuery[] queries)
    {
        Console.WriteLine("📝 Step 3: Memory test defined");
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

    private static void PrintStepComplete(string step, string message)
    {
        Console.WriteLine($"📝 {step}: {message}\n");
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
        Console.WriteLine("   • Memory evaluation tests fact retention across conversation");
        Console.WriteLine("   • LLM judges evaluate whether facts are present in responses");
        Console.WriteLine("   • Scores above 80% indicate good memory performance");
        Console.WriteLine("   • Use this pattern to test any IEvaluableAgent implementation");
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

/// <summary>
/// Simple memory-enabled agent for demonstration.
/// Stores facts in memory and retrieves them when asked.
/// </summary>
public class SimpleMemoryAgent : IEvaluableAgent
{
    public string Name => "Simple Memory Agent";
    
    private readonly Dictionary<string, string> _memoryStore = new();

    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var response = ProcessPrompt(prompt);
        
        return Task.FromResult(new AgentResponse
        {
            Text = response,
            TokenUsage = new TokenUsage
            {
                PromptTokens = prompt.Length / 4, // Rough estimation
                CompletionTokens = response.Length / 4
            }
        });
    }

    private string ProcessPrompt(string prompt)
    {
        var lowerPrompt = prompt.ToLowerInvariant();
        
        // Handle memory storage requests
        if (lowerPrompt.Contains("remember") || lowerPrompt.Contains("my name is") || lowerPrompt.Contains("i work"))
        {
            StoreFact(prompt);
            return "I'll remember that information for you.";
        }
        
        // Handle retrieval questions  
        if (lowerPrompt.Contains('?'))
        {
            return AnswerQuestion(lowerPrompt);
        }
        
        return "Hello! I can remember facts and answer questions about them.";
    }

    private void StoreFact(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        
        if (lower.Contains("my name is"))
        {
            var name = ExtractAfter(prompt, "my name is");
            _memoryStore["name"] = name.Trim();
        }
        else if (lower.Contains("i work as"))
        {
            var job = ExtractAfter(prompt, "i work as");
            _memoryStore["job"] = job.Trim();
        }
        else if (lower.Contains("favorite programming language is") || lower.Contains("programming language do i prefer"))
        {
            var language = ExtractAfter(prompt, lower.Contains("favorite programming language is") ? "favorite programming language is" : "prefer");
            _memoryStore["language"] = language.Trim();
        }
    }

    private string AnswerQuestion(string question)
    {
        if (question.Contains("name") && _memoryStore.ContainsKey("name"))
            return $"Your name is {_memoryStore["name"]}.";
        if (question.Contains("job") && _memoryStore.ContainsKey("job"))
            return $"You work as {_memoryStore["job"]}.";
        if ((question.Contains("programming") || question.Contains("language")) && _memoryStore.ContainsKey("language"))
            return $"Your favorite programming language is {_memoryStore["language"]}.";
            
        return "I don't have that information stored in my memory.";
    }
    
    private static string ExtractAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index == -1) return "";
        
        var start = index + marker.Length;
        return text[start..].Trim().TrimEnd('.', '!', ',');
    }
}

/// <summary>
/// Fake chat client that simulates LLM responses for memory judgment.
/// In real scenarios, this would be a real OpenAI/Azure OpenAI client.
/// </summary>
public class FakeChatClient : IChatClient
{
    public ChatClientMetadata Metadata { get; } = new("fake-model-for-judgment", new Uri("http://localhost"));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        // Simulate memory judgment - normally an LLM would analyze the response
        var jsonResponse = """
        {
          "found_facts": ["My name is Alice Johnson", "I work as a software engineer", "My favorite programming language is C#"],
          "missing_facts": [],
          "forbidden_found": [],
          "score": 90,
          "explanation": "Agent correctly recalled all three facts when asked relevant questions."
        }
        """;
        
        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, jsonResponse)]) 
        { 
            ModelId = "fake-judgment-model"
        };
        
        return Task.FromResult(response);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages, 
        ChatOptions? options = null, 
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Streaming not needed for memory judgment");
    }

    public TService? GetService<TService>(object? key = null) where TService : class => null;
    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}