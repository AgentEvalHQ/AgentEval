// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Models;

namespace AgentEval.Samples;

/// <summary>
/// Sample 01: Hello World - The simplest possible AgentEval test
/// 
/// This demonstrates:
/// - Creating a simple agent
/// - Running a basic test with AgentEval
/// - Checking if the test passed
/// 
/// ⏱️ Time to understand: 2 minutes
/// </summary>
public static class Sample01_HelloWorld
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Create a simple agent
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 1: Creating a simple greeting agent...\n");
        
        var agent = CreateGreetingAgent();
        Console.WriteLine($"   ✓ Agent '{agent.Name}' created\n");

        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Create a evaluation harness
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 2: Creating evaluation harness...\n");
        
        // Simple harness without AI evaluation (for this hello world)
        var harness = new MAFEvaluationHarness(verbose: true);
        Console.WriteLine("   ✓ evaluation harness ready\n");

        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Define a test case
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 3: Defining test case...\n");
        
        // NOTE: This sample uses ExpectedOutputContains for simple validation.
        // EvaluationCriteria requires an AI evaluator (IChatClient) to be passed
        // to MAFEvaluationHarness for AI-powered evaluation. Without an evaluator,
        // the test passes if the output is non-empty and contains the expected substring.
        var testCase = new TestCase
        {
            Name = "Greeting Test",
            Input = "Hello, my name is Alice!",
            ExpectedOutputContains = "Alice"  // Simple substring check (no AI evaluator needed)
        };
        
        Console.WriteLine($"   Test: {testCase.Name}");
        Console.WriteLine($"   Input: \"{testCase.Input}\"");
        Console.WriteLine($"   Expected: Output should contain \"{testCase.ExpectedOutputContains}\"\n");

        // ═══════════════════════════════════════════════════════════════
        // STEP 4: Run the test
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 4: Running test...\n");
        
        var adapter = new MAFAgentAdapter(agent);
        var result = await harness.RunEvaluationAsync(adapter, testCase);

        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Check results
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📊 RESULTS:");
        Console.WriteLine(new string('─', 60));
        
        Console.Write("   Status: ");
        if (result.Passed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("✅ PASSED");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("❌ FAILED");
        }
        Console.ResetColor();
        
        Console.WriteLine($"   Score: {result.Score}/100");
        Console.WriteLine($"   Details: {result.Details}");
        
        if (!string.IsNullOrEmpty(result.ActualOutput))
        {
            Console.WriteLine($"\n   Agent Response:\n   \"{Truncate(result.ActualOutput, 200)}\"");
        }

        Console.WriteLine(new string('─', 60));
        
        // ═══════════════════════════════════════════════════════════════
        // Summary
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • MAFEvaluationHarness is the main entry point for testing");
        Console.WriteLine("   • TestCase defines what to test and what to expect");
        Console.WriteLine("   • MAFAgentAdapter wraps MAF agents for testing");
        Console.WriteLine("   • TestResult contains pass/fail status, score, and output");
        
        Console.WriteLine("\n🔗 NEXT: Run Sample 02 to see tool tracking in action!\n");
    }

    private static AIAgent CreateGreetingAgent()
    {
        if (!AIConfig.IsConfigured)
        {
            // Return a mock agent for demo purposes
            return CreateMockAgent();
        }

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "GreetingAgent",
                Instructions = """
                    You are a friendly greeting assistant.
                    When someone introduces themselves, greet them warmly by name.
                    Keep responses brief and friendly.
                    """
            });
    }

    private static AIAgent CreateMockAgent()
    {
        // For demo without Azure credentials - uses a fake response
        var mockClient = new MockChatClient("Hello Alice! 👋 Nice to meet you!");
        return new ChatClientAgent(
            mockClient,
            new ChatClientAgentOptions
            {
                Name = "GreetingAgent (Mock)",
                Instructions = "You are a friendly greeting assistant."
            });
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🌍 SAMPLE 01: HELLO WORLD                                                   ║
║   The simplest possible AgentEval test                                        ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }
}

/// <summary>
/// Simple mock chat client for demos without Azure credentials.
/// </summary>
internal class MockChatClient : IChatClient
{
    private readonly string _response;

    public MockChatClient(string response)
    {
        _response = response;
    }

    public ChatClientMetadata Metadata => new("MockChatClient");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var message = new ChatMessage(ChatRole.Assistant, _response);
        return Task.FromResult(new ChatResponse(message));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Streaming not implemented in mock");
    }

    public object? GetService(Type serviceType, object? key = null) => null;
    
    public void Dispose() { }
}
