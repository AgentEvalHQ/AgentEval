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
/// Sample 03: Agent with Multiple Tools - Ordering, timing, and timeline
/// 
/// This demonstrates:
/// - Agent with multiple tools
/// - Asserting tool call order
/// - Tool call timeline visualization
/// - Duration assertions
/// 
/// ⏱️ Time to understand: 7 minutes
/// </summary>
public static class Sample03_AgentWithMultipleTools
{
    public static async Task RunAsync()
    {
        PrintHeader();

        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Create an agent with multiple tools
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 1: Creating agent with multiple tools...\n");
        
        var agent = CreateResearchAgent();
        Console.WriteLine($"   ✓ Agent '{agent.Name}' created");
        Console.WriteLine("   🔧 Tools:");
        Console.WriteLine("      • SearchTool - Searches for information");
        Console.WriteLine("      • SummarizeTool - Summarizes content");
        Console.WriteLine("      • FactCheckTool - Verifies claims\n");

        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Create evaluation harness
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 2: Creating evaluation harness...\n");
        
        var harness = new MAFEvaluationHarness(verbose: true);

        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Define test case expecting specific tool order
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 3: Defining test case with expected tool order...\n");
        
        var testCase = new TestCase
        {
            Name = "Research Workflow Test",
            Input = "Research the benefits of renewable energy and summarize the findings.",
            ExpectedTools = ["SearchTool", "SummarizeTool"]  // Expected order
        };
        
        Console.WriteLine($"   Test: {testCase.Name}");
        Console.WriteLine($"   Input: \"{testCase.Input}\"");
        Console.WriteLine($"   Expected Order: {string.Join(" → ", testCase.ExpectedTools!)}\n");

        // ═══════════════════════════════════════════════════════════════
        // STEP 4: Run test with streaming
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("📝 Step 4: Running test with streaming...\n");
        
        var adapter = new MAFAgentAdapter(agent);
        
        // Use streaming to see real-time tool calls
        var result = await harness.RunEvaluationStreamingAsync(adapter, testCase, 
            streamingOptions: new StreamingOptions
            {
                OnToolStart = toolInfo =>
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"   ⏳ Tool starting: {toolInfo.Name}");
                    Console.ResetColor();
                },
                OnToolComplete = toolInfo =>
                {
                    var status = toolInfo.Exception == null ? "✅" : "❌";
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"   {status} Tool complete: {toolInfo.Name} ({toolInfo.Duration?.TotalMilliseconds:F0}ms)");
                    Console.ResetColor();
                }
            },
            options: new EvaluationOptions
            {
                TrackTools = true,
                TrackPerformance = true
            });

        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Display timeline
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📊 TOOL CALL TIMELINE:");
        Console.WriteLine(new string('─', 60));
        
        if (result.Timeline != null && result.Timeline.TotalToolCalls > 0)
        {
            Console.WriteLine($"   Total Duration: {result.Timeline.TotalDuration.TotalMilliseconds:F0}ms");
            Console.WriteLine($"   Total Calls: {result.Timeline.TotalToolCalls}");
            Console.WriteLine($"   Failed Calls: {result.Timeline.FailedToolCalls}");
            
            Console.WriteLine("\n   Timeline:");
            foreach (var inv in result.Timeline.Invocations)
            {
                var bar = new string('█', Math.Max(1, (int)(inv.Duration.TotalMilliseconds / 50)));
                var status = inv.Succeeded ? "✓" : "❌";
                Console.WriteLine($"   {status} {inv.ToolName,-15} {bar} {inv.Duration.TotalMilliseconds:F0}ms");
            }
        }
        else if (result.ToolUsage != null && result.ToolUsage.Count > 0)
        {
            Console.WriteLine($"   Tool Calls: {result.ToolUsage.Count}");
            foreach (var call in result.ToolUsage.Calls)
            {
                var status = call.HasError ? "❌" : "✓";
                Console.WriteLine($"   {status} {call.Name,-15} {call.Duration?.TotalMilliseconds:F0}ms");
            }
        }
        else
        {
            Console.WriteLine("   (No tool calls recorded - running in mock mode)");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 6: Demonstrate advanced assertions
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n📝 Step 6: Demonstrating advanced assertions...\n");
        
        if (result.ToolUsage != null && result.ToolUsage.Count >= 2)
        {
            try
            {
                // Advanced assertions with 'because' for self-documenting tests
                result.ToolUsage
                    .Should()
                    .HaveCalledTool("SearchTool",
                        because: "research must start with information gathering")
                        .BeforeTool("SummarizeTool",
                            because: "can't summarize without data")  // Order assertion!
                        .WithoutError()
                    .And()
                    .HaveCallCountAtLeast(2,
                        because: "workflow requires at least search and summarize")
                    .HaveNoErrors(because: "all tools must succeed for quality output");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ All assertions passed!");
                Console.WriteLine(@"
   CODE USED:
   ┌─────────────────────────────────────────────────────────────┐
   │  result.ToolUsage                                           │
   │      .Should()                                              │
   │      .HaveCalledTool(""SearchTool"",                          │
   │          because: ""research starts with search"")           │
   │          .BeforeTool(""SummarizeTool"",                       │
   │              because: ""can't summarize without data"")      │
   │          .WithoutError()                                    │
   │      .And()                                                 │
   │      .HaveCallCountAtLeast(2)                               │
   │      .HaveNoErrors(because: ""all must succeed"");           │
   └─────────────────────────────────────────────────────────────┘
");
                Console.ResetColor();
            }
            catch (ToolAssertionException ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"   ❌ Assertion failed: {ex.Message}");
                Console.ResetColor();
            }
        }
        else
        {
            // Show what assertions WOULD look like
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠️ Running in mock mode - showing assertion examples:");
            Console.ResetColor();
            Console.WriteLine(@"
   AVAILABLE ASSERTIONS:
   ┌─────────────────────────────────────────────────────────────┐
   │  // Tool ordering                                           │
   │  .HaveCalledTool(""A"").BeforeTool(""B"")                       │
   │  .HaveCalledTool(""B"").AfterTool(""A"")                        │
   │                                                             │
   │  // Call counts                                             │
   │  .HaveCallCount(3)                                          │
   │  .HaveCallCountAtLeast(2)                                   │
   │                                                             │
   │  // Tool-specific assertions                                │
   │  .HaveCalledTool(""X"")                                       │
   │      .WithoutError()                                        │
   │      .WithDurationUnder(TimeSpan.FromSeconds(5))            │
   │      .WithArgument(""param"", ""value"")                        │
   │      .WithResultContaining(""expected"")                      │
   │                                                             │
   │  // Error checking                                          │
   │  .HaveNoErrors()                                            │
   │  .NotHaveCalledTool(""ForbiddenTool"")                        │
   └─────────────────────────────────────────────────────────────┘
");
        }

        Console.WriteLine(new string('─', 60));

        // ═══════════════════════════════════════════════════════════════
        // Summary
        // ═══════════════════════════════════════════════════════════════
        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • .BeforeTool(\"X\") ensures tools are called in order");
        Console.WriteLine("   • .AfterTool(\"X\") is the reverse ordering assertion");
        Console.WriteLine("   • .WithDurationUnder() catches slow tools");
        Console.WriteLine("   • Timeline shows visual tool execution flow");
        Console.WriteLine("   • Streaming provides real-time tool status");
        
        Console.WriteLine("\n🔗 NEXT: Run Sample 04 to see performance metrics!\n");
    }

    private static AIAgent CreateResearchAgent()
    {
        if (!AIConfig.IsConfigured)
        {
            return CreateMockResearchAgent();
        }

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "ResearchAgent",
                Instructions = """
                    You are a research assistant.
                    When researching a topic:
                    1. FIRST use SearchTool to find information
                    2. THEN use SummarizeTool to create a summary
                    3. Optionally use FactCheckTool to verify claims
                    
                    Always follow this order for best results.
                    """,
                ChatOptions = new ChatOptions
                {
                    Tools = 
                    [
                        AIFunctionFactory.Create(SearchTool),
                        AIFunctionFactory.Create(SummarizeTool),
                        AIFunctionFactory.Create(FactCheckTool)
                    ]
                }
            });
    }

    private static AIAgent CreateMockResearchAgent()
    {
        var mockClient = new MockResearchChatClient();
        return new ChatClientAgent(
            mockClient,
            new ChatClientAgentOptions
            {
                Name = "ResearchAgent (Mock)",
                Instructions = "You are a research assistant.",
                ChatOptions = new ChatOptions
                {
                    Tools = 
                    [
                        AIFunctionFactory.Create(SearchTool),
                        AIFunctionFactory.Create(SummarizeTool),
                        AIFunctionFactory.Create(FactCheckTool)
                    ]
                }
            });
    }

    [Description("Searches for information on a topic. Returns relevant content.")]
    public static string SearchTool(
        [Description("The search query")] string query)
    {
        Console.WriteLine($"   🔍 SearchTool: \"{query}\"");
        Thread.Sleep(150); // Simulate search time
        return """
            Renewable energy comes from natural sources that replenish faster than they are consumed.
            Key benefits include: reduced carbon emissions, lower long-term costs, energy independence,
            job creation, and improved public health. Solar and wind are the fastest-growing sources.
            """;
    }

    [Description("Summarizes content into a concise format.")]
    public static string SummarizeTool(
        [Description("The content to summarize")] string content)
    {
        Console.WriteLine($"   📝 SummarizeTool: ({content.Length} chars)");
        Thread.Sleep(100); // Simulate processing
        return """
            Summary: Renewable energy offers environmental, economic, and health benefits 
            through reduced emissions and sustainable power generation.
            """;
    }

    [Description("Fact-checks a claim for accuracy.")]
    public static bool FactCheckTool(
        [Description("The claim to verify")] string claim)
    {
        Console.WriteLine($"   ✓ FactCheckTool: \"{claim}\"");
        Thread.Sleep(50);
        return true; // Verified
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🔧 SAMPLE 03: AGENT WITH MULTIPLE TOOLS                                    ║
║   Tool ordering, timing, and timeline visualization                          ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }
}

/// <summary>
/// Mock chat client for multi-tool demo without Azure.
/// </summary>
internal class MockResearchChatClient : IChatClient
{
    private int _callCount = 0;

    public ChatClientMetadata Metadata => new("MockResearchClient");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _callCount++;
        
        // Call 1: SearchTool
        if (_callCount == 1)
        {
            var toolCall = new FunctionCallContent("call_1", "SearchTool", 
                new Dictionary<string, object?> { ["query"] = "renewable energy benefits" });
            var message = new ChatMessage(ChatRole.Assistant, [toolCall]);
            return Task.FromResult(new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls });
        }
        
        // Call 2: SummarizeTool
        if (_callCount == 2)
        {
            var toolCall = new FunctionCallContent("call_2", "SummarizeTool", 
                new Dictionary<string, object?> { ["content"] = "Renewable energy info..." });
            var message = new ChatMessage(ChatRole.Assistant, [toolCall]);
            return Task.FromResult(new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls });
        }
        
        // Call 3: Final response
        var response = new ChatMessage(ChatRole.Assistant, """
            Based on my research, renewable energy offers significant benefits including:
            - **Environmental**: Reduced carbon emissions and pollution
            - **Economic**: Lower long-term energy costs and job creation
            - **Health**: Cleaner air and water for communities
            
            Solar and wind are the fastest-growing renewable sources globally.
            """);
        return Task.FromResult(new ChatResponse(response));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // For mock mode, just call non-streaming and yield as single update
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        var update = new ChatResponseUpdate
        {
            Role = response.Messages.LastOrDefault()?.Role ?? ChatRole.Assistant,
            FinishReason = response.FinishReason
        };
        
        // Add content or tool calls from the response
        var lastMessage = response.Messages.LastOrDefault();
        if (lastMessage?.Contents != null)
        {
            foreach (var content in lastMessage.Contents)
            {
                update.Contents.Add(content);
            }
        }
        
        yield return update;
    }

    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}
