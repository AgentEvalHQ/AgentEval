// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Assertions;
using System.ComponentModel;

namespace AgentEval.Samples;

/// <summary>
/// Sample 04: Performance Metrics - Latency, cost, TTFT, and tokens
/// 
/// This demonstrates:
/// - Capturing performance metrics during tests
/// - Asserting on latency, cost, and token usage
/// - Understanding Time To First Token (TTFT) in streaming
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample04_PerformanceMetrics
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Create an agent
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 1: Creating agent for performance testing...\n");
        
        var agent = CreateAgent();
        Console.WriteLine($"   ✓ Agent '{agent.Name}' created\n");

        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Create harness with performance tracking
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 2: Creating evaluation harness with performance tracking...\n");
        
        var harness = new MAFEvaluationHarness(verbose: true);

        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Run test with streaming (for TTFT measurement)
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 3: Running test with streaming...\n");
        
        var testCase = new TestCase
        {
            Name = "Performance Test",
            Input = "Write a brief paragraph about the importance of software testing."
        };

        var adapter = new MAFAgentAdapter(agent);
        
        Console.WriteLine("   Streaming response:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("   ");
        
        DateTimeOffset? firstTokenTime = null;
        var startTime = DateTimeOffset.UtcNow;
        
        var result = await harness.RunEvaluationStreamingAsync(adapter, testCase, 
            streamingOptions: new StreamingOptions
            {
                OnTextChunk = chunk =>
                {
                    if (firstTokenTime == null)
                    {
                        firstTokenTime = DateTimeOffset.UtcNow;
                        Console.ForegroundColor = ConsoleColor.White;
                    }
                    Console.Write(chunk);
                },
                OnFirstToken = ttft =>
                {
                    // This callback fires when first token arrives
                }
            },
            options: new EvaluationOptions
            {
                TrackPerformance = true
            });
        
        Console.ResetColor();
        Console.WriteLine("\n");

        // ═══════════════════════════════════════════════════════════════
        // STEP 4: Display performance metrics
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📊 PERFORMANCE METRICS:");
        Console.WriteLine(new string('─', 60));
        
        if (result.Performance != null)
        {
            var perf = result.Performance;
            
            Console.WriteLine($"   ⏱️  Total Duration:    {perf.TotalDuration.TotalMilliseconds:F0}ms");
            
            if (perf.TimeToFirstToken.HasValue)
            {
                Console.WriteLine($"   ⚡ Time to First Token: {perf.TimeToFirstToken.Value.TotalMilliseconds:F0}ms");
            }
            
            if (perf.TotalToolTime > TimeSpan.Zero)
            {
                Console.WriteLine($"   🔧 Tool Time:          {perf.TotalToolTime.TotalMilliseconds:F0}ms");
            }
            
            Console.WriteLine($"   📊 Prompt Tokens:      {perf.PromptTokens}");
            Console.WriteLine($"   📝 Completion Tokens:  {perf.CompletionTokens}");
            Console.WriteLine($"   📈 Total Tokens:       {perf.TotalTokens}");
            
            if (perf.EstimatedCost.HasValue)
            {
                Console.WriteLine($"   💰 Estimated Cost:     ${perf.EstimatedCost.Value:F6}");
            }
            
            if (perf.ToolCallCount > 0)
            {
                Console.WriteLine($"   🔧 Tool Calls:         {perf.ToolCallCount}");
            }
            
            Console.WriteLine($"   🔄 Streaming:          {(perf.WasStreaming ? "Yes" : "No")}");
            
            if (!string.IsNullOrEmpty(perf.ModelUsed))
            {
                Console.WriteLine($"   🤖 Model:              {perf.ModelUsed}");
            }
        }
        else
        {
            Console.WriteLine("   (Performance metrics not captured - mock mode)");
            
            // Show what metrics WOULD look like
            Console.WriteLine("\n   Example metrics structure:");
            Console.WriteLine("   ⏱️  Total Duration:    1234ms");
            Console.WriteLine("   ⚡ Time to First Token: 250ms");
            Console.WriteLine("   📊 Prompt Tokens:      150");
            Console.WriteLine("   📝 Completion Tokens:  85");
            Console.WriteLine("   💰 Estimated Cost:     $0.001234");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Demonstrate performance assertions
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 5: Demonstrating performance assertions...\n");
        
        if (result.Performance != null)
        {
            try
            {
                result.Performance
                    .Should()
                    .HaveTotalDurationUnder(TimeSpan.FromSeconds(30))
                    .HaveTokenCountUnder(5000);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ Performance assertions passed!");
                Console.ResetColor();
            }
            catch (PerformanceAssertionException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"   ❌ Assertion failed: {ex.Message}");
                Console.ResetColor();
            }
        }

        Console.WriteLine(@"
   AVAILABLE PERFORMANCE ASSERTIONS:
   ┌─────────────────────────────────────────────────────────────┐
   │  result.Performance                                         │
   │      .Should()                                              │
   │      .HaveTotalDurationUnder(TimeSpan.FromSeconds(10))     │
   │      .HaveTimeToFirstTokenUnder(TimeSpan.FromSeconds(2))   │
   │      .HaveTokenCountUnder(2000)                             │
   │      .HavePromptTokensUnder(500)                            │
   │      .HaveCompletionTokensUnder(1000)                       │
   │      .HaveEstimatedCostUnder(0.10m)                         │
   │      .HaveToolCallCount(2)                                  │
   │      .HaveToolCallCountAtMost(5);                           │
   └─────────────────────────────────────────────────────────────┘
");

        Console.WriteLine(new string('─', 60));

        // ═══════════════════════════════════════════════════════════════
        // STEP 6: Cost estimation info
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n💡 COST ESTIMATION:");
        Console.WriteLine("   AgentEval supports cost tracking for 8+ models:");
        Console.WriteLine("   • gpt-4o, gpt-4o-mini, gpt-4-turbo, gpt-3.5-turbo");
        Console.WriteLine("   • claude-3-opus, claude-3-sonnet, claude-3-haiku");
        Console.WriteLine("   • gemini-1.5-pro, gemini-1.5-flash");
        Console.WriteLine("\n   You can add custom pricing:");
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────┐
   │  ModelPricing.SetPricing(                                   │
   │      modelName: ""my-custom-model"",                          │
   │      inputPer1K: 0.005m,   // $0.005 per 1K input tokens    │
   │      outputPer1K: 0.015m   // $0.015 per 1K output tokens   │
   │  );                                                         │
   └─────────────────────────────────────────────────────────────┘
");

        // ═══════════════════════════════════════════════════════════════
        // Summary
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • Set TrackPerformance = true to capture metrics");
        Console.WriteLine("   • TTFT (Time To First Token) requires streaming");
        Console.WriteLine("   • Cost estimation works with token counts + model pricing");
        Console.WriteLine("   • Use assertions to enforce SLAs and budgets");
        
        Console.WriteLine("\n🔗 NEXT: Run Sample 05 to see RAG evaluation!\n");
    }

    private static AIAgent CreateAgent()
    {
        if (!AIConfig.IsConfigured)
        {
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
                Name = "WriterAgent",
                Instructions = "You are a helpful writing assistant. Keep responses concise but informative."
            });
    }

    private static AIAgent CreateMockAgent()
    {
        var mockClient = new MockWriterChatClient();
        return new ChatClientAgent(
            mockClient,
            new ChatClientAgentOptions
            {
                Name = "WriterAgent (Mock)",
                Instructions = "You are a helpful writing assistant."
            });
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   ⚡ SAMPLE 04: PERFORMANCE METRICS                                          ║
║   Latency, cost, tokens, and TTFT tracking                                    ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}

/// <summary>
/// Mock chat client that simulates streaming response.
/// </summary>
internal class MockWriterChatClient : IChatClient
{
    public ChatClientMetadata Metadata => new("MockWriterClient");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ChatMessage(ChatRole.Assistant, """
            Software testing is essential for delivering reliable, high-quality applications. 
            It helps catch bugs early, reduces maintenance costs, and ensures the software 
            meets user expectations. Comprehensive testing also builds confidence in the 
            codebase, making it easier to add new features without fear of breaking existing 
            functionality.
            """);
        return Task.FromResult(new ChatResponse(response));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = """
            Software testing is essential for delivering reliable, high-quality applications. 
            It helps catch bugs early, reduces maintenance costs, and ensures the software 
            meets user expectations.
            """;
        
        var words = response.Split(' ');
        foreach (var word in words)
        {
            await Task.Delay(30, cancellationToken); // Simulate streaming delay
            yield return new ChatResponseUpdate
            {
                Contents = [new TextContent(word + " ")]
            };
        }
    }

    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}
