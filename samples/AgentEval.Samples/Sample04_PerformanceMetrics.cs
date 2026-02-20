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
/// - Capturing performance metrics during evaluations
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

        var agent = CreateAgent();
        Console.WriteLine($"\ud83d\udcdd Step 1: Agent '{agent.Name}' created\n");

        var harness = new MAFEvaluationHarness(verbose: true);

        var testCase = new TestCase
        {
            Name = "Performance Test",
            Input = "Write a brief paragraph about the importance of software testing."
        };

        var adapter = new MAFAgentAdapter(agent);
        Console.WriteLine("\ud83d\udcdd Step 3: Streaming response:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("   ");

        var result = await harness.RunEvaluationStreamingAsync(adapter, testCase, 
            streamingOptions: CreateStreamingOptions(),
            options: new EvaluationOptions { TrackPerformance = true });
        
        Console.ResetColor();
        Console.WriteLine("\n");

        PrintPerformanceMetrics(result);
        RunPerformanceAssertions(result);
        PrintPerformanceAssertionReference();
        PrintCostEstimationInfo();
        PrintKeyTakeaways();
    }

    private static StreamingOptions CreateStreamingOptions()
    {
        return new StreamingOptions
        {
            OnTextChunk = chunk => Console.Write(chunk),
            OnFirstToken = ttft => { }
        };
    }

    private static void PrintPerformanceMetrics(TestResult result)
    {
        Console.WriteLine("\ud83d\udcca PERFORMANCE METRICS:");
        Console.WriteLine(new string('\u2500', 60));
        
        if (result.Performance != null)
        {
            var perf = result.Performance;
            Console.WriteLine($"   \u23f1\ufe0f  Total Duration:    {perf.TotalDuration.TotalMilliseconds:F0}ms");
            if (perf.TimeToFirstToken.HasValue)
                Console.WriteLine($"   \u26a1 Time to First Token: {perf.TimeToFirstToken.Value.TotalMilliseconds:F0}ms");
            if (perf.TotalToolTime > TimeSpan.Zero)
                Console.WriteLine($"   \ud83d\udd27 Tool Time:          {perf.TotalToolTime.TotalMilliseconds:F0}ms");
            Console.WriteLine($"   \ud83d\udcca Prompt Tokens:      {perf.PromptTokens}");
            Console.WriteLine($"   \ud83d\udcdd Completion Tokens:  {perf.CompletionTokens}");
            Console.WriteLine($"   \ud83d\udcc8 Total Tokens:       {perf.TotalTokens}");
            if (perf.EstimatedCost.HasValue)
                Console.WriteLine($"   \ud83d\udcb0 Estimated Cost:     ${perf.EstimatedCost.Value:F6}");
            if (perf.ToolCallCount > 0)
                Console.WriteLine($"   \ud83d\udd27 Tool Calls:         {perf.ToolCallCount}");
            Console.WriteLine($"   \ud83d\udd04 Streaming:          {(perf.WasStreaming ? "Yes" : "No")}");
            if (!string.IsNullOrEmpty(perf.ModelUsed))
                Console.WriteLine($"   \ud83e\udd16 Model:              {perf.ModelUsed}");
        }
        else
        {
            Console.WriteLine("   (Performance metrics not captured)");
        }
    }

    private static void RunPerformanceAssertions(TestResult result)
    {
        Console.WriteLine("\n\ud83d\udcdd Step 5: Performance assertions...\n");
        
        if (result.Performance != null)
        {
            try
            {
                result.Performance
                    .Should()
                    .HaveTotalDurationUnder(TimeSpan.FromSeconds(30))
                    .HaveTokenCountUnder(5000);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   \u2705 Performance assertions passed!");
                Console.ResetColor();
            }
            catch (PerformanceAssertionException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"   \u274c Assertion failed: {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    private static void PrintPerformanceAssertionReference()
    {
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
    }

    private static void PrintCostEstimationInfo()
    {
        Console.WriteLine("\n💡 COST ESTIMATION:");
        Console.WriteLine("   AgentEval supports cost tracking for 9+ models:");
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
    }

    private static void PrintKeyTakeaways()
    {
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
                ChatOptions = new() { Instructions = "You are a helpful writing assistant. Keep responses concise but informative." }
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
                ChatOptions = new() { Instructions = "You are a helpful writing assistant." }
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

    /// <summary>
    /// Mock chat client that simulates streaming with Usage data for tutorial demo.
    /// Private to Sample04 to avoid naming collisions.
    /// </summary>
    private class MockWriterChatClient : IChatClient
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
            return Task.FromResult(new ChatResponse(response)
            {
                Usage = new UsageDetails { InputTokenCount = 45, OutputTokenCount = 62 }
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var words = "Software testing is essential for delivering reliable high-quality applications.".Split(' ');
            foreach (var word in words)
            {
                await Task.Delay(30, cancellationToken);
                yield return new ChatResponseUpdate
                {
                    Contents = [new TextContent(word + " ")]
                };
            }

            yield return new ChatResponseUpdate
            {
                Contents = [new UsageContent(new UsageDetails { InputTokenCount = 45, OutputTokenCount = 62 })]
            };
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
