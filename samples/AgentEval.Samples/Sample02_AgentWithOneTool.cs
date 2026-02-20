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
/// Sample 02: Agent with One Tool - Tool tracking and assertions
/// 
/// This demonstrates:
/// - Creating an agent with a tool
/// - Tracking tool calls during test execution
/// - Using fluent assertions for tool verification
/// 
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample02_AgentWithOneTool
{
    public static async Task RunAsync()
    {
        PrintHeader();

        var agent = CreateCalculatorAgent();
        PrintAgentCreated(agent);

        var harness = new MAFEvaluationHarness(verbose: true);
        Console.WriteLine("📝 Step 2: Evaluation harness ready\n");

        var testCase = CreateCalculatorTestCase();
        PrintTestCaseDetails(testCase);

        var adapter = new MAFAgentAdapter(agent);
        var result = await harness.RunEvaluationAsync(adapter, testCase, new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true
        });

        PrintResults(result);
        RunFluentAssertions(result);
        PrintKeyTakeaways();
    }

    private static void PrintAgentCreated(AIAgent agent)
    {
        Console.WriteLine($"📝 Step 1: Agent '{agent.Name}' created");
        Console.WriteLine("   🔧 Tool: CalculatorTool - Performs basic math\n");
    }

    private static TestCase CreateCalculatorTestCase()
    {
        return new TestCase
        {
            Name = "Calculator Tool Test",
            Input = "What is 42 multiplied by 17?",
            ExpectedTools = ["CalculatorTool"]
        };
    }

    private static void PrintTestCaseDetails(TestCase testCase)
    {
        Console.WriteLine($"📝 Step 3: Test case defined");
        Console.WriteLine($"   Test: {testCase.Name}");
        Console.WriteLine($"   Input: \"{testCase.Input}\"");
        Console.WriteLine($"   Expected Tool: {string.Join(", ", testCase.ExpectedTools!)}\n");
    }

    private static void PrintResults(TestResult result)
    {
        Console.WriteLine("\n📊 RESULTS:");
        Console.WriteLine(new string('─', 60));
        
        PrintPassFail(result.Passed);
        Console.WriteLine($"   Score: {result.Score}/100");
        
        if (result.ToolUsage != null && result.ToolUsage.Count > 0)
        {
            Console.WriteLine($"\n   🔧 Tool Calls: {result.ToolUsage.Count}");
            foreach (var tool in result.ToolUsage.Calls)
            {
                var status = tool.HasError ? "❌" : "✓";
                var duration = tool.Duration?.TotalMilliseconds.ToString("F0") + "ms" ?? "N/A";
                Console.WriteLine($"      {status} {tool.Name} ({duration})");
            }
        }
    }

    private static void RunFluentAssertions(TestResult result)
    {
        Console.WriteLine("\n📝 Step 6: Running fluent assertions...\n");
        
        if (result.ToolUsage != null)
        {
            try
            {
                result.ToolUsage
                    .Should()
                    .HaveCalledTool("CalculatorTool",
                        because: "agent must use calculator for math operations")
                        .WithoutError(because: "calculation errors should be caught")
                    .And()
                    .HaveNoErrors(because: "all tool calls must succeed");

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("   ✅ All assertions passed!");
                Console.WriteLine(@"
   CODE USED:
   ┌─────────────────────────────────────────────────────────────┐
   │  result.ToolUsage                                           │
   │      .Should()                                              │
   │      .HaveCalledTool(""CalculatorTool"",                      │
   │          because: ""agent must use calculator for math"")    │
   │          .WithoutError(because: ""calculations must succeed"")│
   │      .And()                                                 │
   │      .HaveNoErrors(because: ""all tool calls must succeed""); │
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
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   ⚠️ No tool usage recorded (tool may not have been called)");
            Console.ResetColor();
        }

        Console.WriteLine(new string('─', 60));
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • Set TrackTools = true in EvaluationOptions to track tool calls");
        Console.WriteLine("   • result.ToolUsage contains all tool call information");
        Console.WriteLine("   • .Should() starts fluent assertions");
        Console.WriteLine("   • .HaveCalledTool(\"Name\") verifies a tool was called");
        Console.WriteLine("   • .WithoutError() verifies no exceptions occurred");
        Console.WriteLine("   • .HaveNoErrors() verifies ALL tools completed successfully");
        Console.WriteLine("   • 💡 Use 'because:' to document WHY assertions matter!");
        
        Console.WriteLine("\n🔗 NEXT: Run Sample 03 to see multi-tool ordering!\n");
    }

    private static AIAgent CreateCalculatorAgent()
    {
        if (!AIConfig.IsConfigured)
        {
            return CreateMockCalculatorAgent();
        }

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient
            .GetChatClient(AIConfig.ModelDeployment)
            .AsIChatClient();

        return new ChatClientAgent(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "MathAgent",
                ChatOptions = new ChatOptions
                {
                    Instructions = """
                        You are a helpful math assistant.
                        When asked to perform calculations, ALWAYS use the CalculatorTool.
                        Provide the result in a clear, conversational way.
                        """,
                    Tools = [AIFunctionFactory.Create(CalculatorTool)]
                }
            });
    }

    private static AIAgent CreateMockCalculatorAgent()
    {
        var mockClient = new MockCalculatorChatClient();
        return new ChatClientAgent(
            mockClient,
            new ChatClientAgentOptions
            {
                Name = "MathAgent (Mock)",
                ChatOptions = new ChatOptions
                {
                    Instructions = "You are a math assistant.",
                    Tools = [AIFunctionFactory.Create(CalculatorTool)]
                }
            });
    }

    [Description("Performs basic arithmetic operations. Supports add, subtract, multiply, divide.")]
    public static double CalculatorTool(
        [Description("First number")] double a,
        [Description("Second number")] double b,
        [Description("Operation: add, subtract, multiply, or divide")] string operation)
    {
        Console.WriteLine($"   🔧 CalculatorTool called: {a} {operation} {b}");
        
        return operation.ToLower() switch
        {
            "add" => a + b,
            "subtract" => a - b,
            "multiply" => a * b,
            "divide" => b != 0 ? a / b : throw new DivideByZeroException(),
            _ => throw new ArgumentException($"Unknown operation: {operation}")
        };
    }

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   🔧 SAMPLE 02: AGENT WITH ONE TOOL                                          ║
║   Tool tracking and fluent assertions                                         ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintPassFail(bool passed)
    {
        Console.Write("   Status: ");
        if (passed)
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
    }

    /// <summary>
    /// Mock chat client that simulates tool calling for tutorial demo without Azure.
    /// Private to Sample02 to avoid naming collisions.
    /// </summary>
    private class MockCalculatorChatClient : IChatClient
    {
        private int _callCount = 0;

        public ChatClientMetadata Metadata => new("MockCalculatorClient");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _callCount++;
            
            if (_callCount == 1)
            {
                var toolCall = new FunctionCallContent("call_1", "CalculatorTool", 
                    new Dictionary<string, object?> { ["a"] = 42, ["b"] = 17, ["operation"] = "multiply" });
                var message = new ChatMessage(ChatRole.Assistant, [toolCall]);
                return Task.FromResult(new ChatResponse(message) { FinishReason = ChatFinishReason.ToolCalls });
            }
            
            var response = new ChatMessage(ChatRole.Assistant, "42 multiplied by 17 equals **714**.");
            return Task.FromResult(new ChatResponse(response));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _callCount++;

            if (_callCount == 1)
            {
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new FunctionCallContent("call_1", "CalculatorTool",
                        new Dictionary<string, object?> { ["a"] = 42, ["b"] = 17, ["operation"] = "multiply" })],
                    FinishReason = ChatFinishReason.ToolCalls
                };
                yield break;
            }

            foreach (var word in "42 multiplied by 17 equals **714**.".Split(' '))
            {
                await Task.Delay(50, cancellationToken);
                yield return new ChatResponseUpdate
                {
                    Role = ChatRole.Assistant,
                    Contents = [new TextContent(word + " ")]
                };
            }
        }

        public object? GetService(Type serviceType, object? key = null) => null;
        public void Dispose() { }
    }
}
