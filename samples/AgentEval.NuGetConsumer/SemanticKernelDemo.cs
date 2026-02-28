// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using AgentEval.NuGetConsumer.Tools;
using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace AgentEval.NuGetConsumer;

/// <summary>
/// Semantic Kernel Flight Agent demo — real SK with [KernelFunction] plugins,
/// evaluated by AgentEval with tool assertions, code metrics, and LLM-as-judge.
///
/// Architecture:
///   Kernel (SK) + FlightPlugin ([KernelFunction]) → AIFunctionFactory bridge
///   → ChatClientAgent (MAF) + IChatClient → MAFAgentAdapter → AgentEval harness
///
/// This proves AgentEval evaluates real Semantic Kernel agents — no mocks.
/// </summary>
public static class SemanticKernelDemo
{
    public static async Task RunAsync()
    {
        ShowSection("✈️  SEMANTIC KERNEL FLIGHT AGENT",
            "Real SK [KernelFunction] plugins · Tool Assertions · LLM-as-Judge");

        if (!Config.IsConfigured)
        {
            Console.WriteLine("      ❌ This demo requires Azure OpenAI credentials.\n");
            Console.WriteLine("      Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, and AZURE_OPENAI_DEPLOYMENT.\n");
            return;
        }

        // ─── Step 1: Build Semantic Kernel with FlightPlugin ─────────
        Console.WriteLine("   📝 Step 1: Build Semantic Kernel with FlightPlugin\n");
        Console.WriteLine("      var kernel = Kernel.CreateBuilder()");
        Console.WriteLine($"          .AddAzureOpenAIChatCompletion(\"{Config.Model}\", ...)");
        Console.WriteLine("          .Build();");
        Console.WriteLine("      kernel.Plugins.AddFromType<FlightPlugin>();\n");

        var kernel = Kernel.CreateBuilder()
            .AddAzureOpenAIChatCompletion(
                deploymentName: Config.Model,
                endpoint: Config.Endpoint.ToString(),
                apiKey: Config.KeyCredential.Key)
            .Build();

        kernel.Plugins.AddFromType<FlightPlugin>();

        Console.WriteLine($"      ✅ Kernel built with {kernel.Plugins.Count} plugin(s):");
        foreach (var plugin in kernel.Plugins)
            Console.WriteLine($"         • {plugin.Name}: {string.Join(", ", plugin.Select(f => f.Name))}");
        Console.WriteLine();

        // ─── Step 2: Bridge SK → AgentEval ───────────────────────────
        Console.WriteLine("   📝 Step 2: Bridge SK plugins to AgentEval via IChatClient\n");
        Console.WriteLine("      // The SAME FlightPlugin class works with both SK and M.E.AI!");
        Console.WriteLine("      // SK: kernel.Plugins.AddFromType<FlightPlugin>()");
        Console.WriteLine("      // M.E.AI: AIFunctionFactory.Create(flightPlugin.SearchFlights)\n");

        // Create IChatClient from Azure OpenAI (same endpoint as the kernel)
        var azureClient = new AzureOpenAIClient(Config.Endpoint, Config.KeyCredential);
        var chatClient = azureClient.GetChatClient(Config.Model).AsIChatClient();

        // Bridge SK plugin methods to M.E.AI tools — the cross-framework bridge!
        var flightPlugin = new FlightPlugin();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(flightPlugin.SearchFlights),
            AIFunctionFactory.Create(flightPlugin.BookFlight)
        };

        Console.WriteLine($"      ✅ {tools.Count} SK [KernelFunction] tools bridged to M.E.AI AITools");
        foreach (var tool in tools)
            Console.WriteLine($"         • {tool.Name}");
        Console.WriteLine();

        // Wrap with MAF ChatClientAgent for tool tracking
        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = $"SK-FlightAgent ({Config.Model})",
            ChatOptions = new ChatOptions
            {
                Instructions = """
                    You are a flight booking assistant. You help users search for flights 
                    and book them. When the user provides a specific flight ID, book it
                    directly using BookFlight. For open-ended requests (search + book),
                    call SearchFlights first, then BookFlight with the best matching result.
                    """,
                Tools = tools
            }
        });

        var adapter = new MAFAgentAdapter(agent);
        Console.WriteLine($"      ✅ Agent '{agent.Name}' ready for evaluation\n");

        // ─── Step 3: Define validation cases ─────────────────────────
        Console.WriteLine("   📝 Step 3: Define validation cases with expected tools\n");

        var testCases = new List<TestCase>
        {
            new()
            {
                Name = "Search Flights to Paris",
                Input = "Find me flights from New York to Paris on March 15th",
                ExpectedOutputContains = "Paris",
                ExpectedTools = ["SearchFlights"],
                PassingScore = 70
            },
            new()
            {
                Name = "Book a Specific Flight",
                Input = "I already found flight FL-123. Book it for 2 passengers.",
                ExpectedOutputContains = "FL-123",
                ExpectedTools = ["BookFlight"],
                PassingScore = 70
            },
            new()
            {
                Name = "Search and Book (Multi-Tool)",
                Input = "Find the cheapest flight from London to Tokyo next Friday and book it for 1 passenger",
                ExpectedOutputContains = "booked",
                ExpectedTools = ["SearchFlights", "BookFlight"],
                PassingScore = 70
            }
        };

        foreach (var tc in testCases)
        {
            Console.WriteLine($"      • {tc.Name}");
            Console.WriteLine($"        Input: \"{tc.Input}\"");
            Console.WriteLine($"        Expected tools: [{string.Join(", ", tc.ExpectedTools!)}]");
        }
        Console.WriteLine();

        // ─── Step 4: Run evaluation with tool tracking ───────────────
        Console.WriteLine("   📝 Step 4: Run evaluation with TrackTools + TrackPerformance\n");

        var harness = new MAFEvaluationHarness(verbose: true);
        var evalOptions = new EvaluationOptions
        {
            TrackTools = true,
            TrackPerformance = true,
            ModelName = Config.Model
        };

        var results = new List<TestResult>();
        foreach (var testCase in testCases)
        {
            var result = await harness.RunEvaluationAsync(adapter, testCase, evalOptions);
            results.Add(result);
            PrintToolResult(result);
        }
        Console.WriteLine();

        // ─── Step 5: Fluent Tool Assertions ──────────────────────────
        Console.WriteLine("   📝 Step 5: Fluent tool assertions\n");

        RunFlightAssertions(results);

        // ─── Step 6: Code-based Agentic Metrics ─────────────────────
        Console.WriteLine("   📝 Step 6: Code-based agentic metrics (free — no LLM cost)\n");

        await RunCodeMetrics(results, testCases);

        // ─── Step 7: LLM-as-Judge ───────────────────────────────────
        Console.WriteLine($"   📝 Step 7: LLM-as-Judge — TaskCompletionMetric ({Config.Model})\n");

        await RunLlmAsJudge(results, testCases);

        // ─── Step 8: Performance Summary ─────────────────────────────
        Console.WriteLine("   📝 Step 8: Performance summary\n");
        PrintPerformanceSummary(results);

        // ─── Summary ─────────────────────────────────────────────────
        var summary = new TestSummary("SK FlightAgent Evaluation", results);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n   📊 Total: {summary.TotalCount} validations — " +
                          $"{summary.PassedCount} passed, {summary.FailedCount} failed, " +
                          $"avg score: {summary.AverageScore:F1}/100");
        Console.ResetColor();
        Console.WriteLine();

        ShowCode("""
            // Complete SK + AgentEval pattern
            var kernel = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(model, endpoint, key)
                .Build();
            kernel.Plugins.AddFromType<FlightPlugin>();
            
            // Same plugin class bridges both frameworks!
            var plugin = new FlightPlugin();
            var tools = new List<AITool> {
                AIFunctionFactory.Create(plugin.SearchFlights),
                AIFunctionFactory.Create(plugin.BookFlight)
            };
            var chatClient = azureClient.GetChatClient(model).AsIChatClient();
            var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions {
                ChatOptions = new ChatOptions { Tools = tools }
            });
            var adapter = new MAFAgentAdapter(agent);
            
            var result = await harness.RunEvaluationAsync(adapter, testCase, evalOptions);
            
            result.ToolUsage!.Should()
                .HaveCalledTool("SearchFlights").BeforeTool("BookFlight")
                .And().HaveNoErrors();
            """);
    }

    // ─── Assertion Runner ────────────────────────────────────────────

    private static void RunFlightAssertions(List<TestResult> results)
    {
        // Validation 1: Search only — should call SearchFlights
        var searchResult = results[0];
        if (searchResult.ToolUsage != null)
        {
            try
            {
                searchResult.ToolUsage.Should()
                    .HaveCalledTool("SearchFlights",
                        because: "user asked to find flights")
                        .WithArgument("destination", "Paris")
                        .WithoutError()
                    .And()
                    .HaveNoErrors();

                PrintPass("Search validation: SearchFlights called with destination=Paris");
            }
            catch (ToolAssertionException ex)
            {
                PrintFail(ex.Message);
            }
        }

        // Validation 2: Book only — should call BookFlight
        var bookResult = results[1];
        if (bookResult.ToolUsage != null)
        {
            try
            {
                bookResult.ToolUsage.Should()
                    .HaveCalledTool("BookFlight",
                        because: "user wants to book a specific flight")
                        .WithArgument("flightId", "FL-123")
                        .WithArgument("passengers", 2)
                    .And()
                    .HaveNoErrors();

                PrintPass("Book validation: BookFlight called with flightId=FL-123, passengers=2");
            }
            catch (ToolAssertionException ex)
            {
                PrintFail(ex.Message);
            }
        }

        // Validation 3: Multi-tool — SearchFlights BEFORE BookFlight
        var multiResult = results[2];
        if (multiResult.ToolUsage != null)
        {
            try
            {
                multiResult.ToolUsage.Should()
                    .HaveCalledTool("SearchFlights",
                        because: "must search before booking")
                        .BeforeTool("BookFlight")
                    .And()
                    .HaveCalledTool("BookFlight",
                        because: "user asked to book the cheapest flight")
                    .And()
                    .HaveNoErrors();

                PrintPass("Multi-tool: SearchFlights → BookFlight ordering verified");
            }
            catch (ToolAssertionException ex)
            {
                PrintFail(ex.Message);
            }
        }

        Console.WriteLine();
    }

    // ─── Code Metrics ────────────────────────────────────────────────

    private static async Task RunCodeMetrics(List<TestResult> results, List<TestCase> testCases)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var testCase = testCases[i];

            if (result.ToolUsage == null || testCase.ExpectedTools == null) continue;

            var context = new EvaluationContext
            {
                Input = testCase.Input,
                Output = result.ActualOutput ?? "",
                ToolUsage = result.ToolUsage
            };

            var selectionMetric = new ToolSelectionMetric(testCase.ExpectedTools);
            var selectionResult = await selectionMetric.EvaluateAsync(context);

            var efficiencyMetric = new ToolEfficiencyMetric(maxExpectedCalls: testCase.ExpectedTools.Count + 1);
            var efficiencyResult = await efficiencyMetric.EvaluateAsync(context);

            Console.Write($"      {testCase.Name}: ");
            Console.ForegroundColor = selectionResult.Passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"selection={selectionResult.Score}/100");
            Console.ResetColor();
            Console.Write("  ");
            Console.ForegroundColor = efficiencyResult.Passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"efficiency={efficiencyResult.Score}/100");
            Console.ResetColor();
            Console.WriteLine();
        }

        Console.WriteLine();
    }

    // ─── LLM-as-Judge ────────────────────────────────────────────────

    private static async Task RunLlmAsJudge(List<TestResult> results, List<TestCase> testCases)
    {
        var judgeClient = AgentFactory.CreateEvaluatorChatClient();

        var completionMetric = new TaskCompletionMetric(judgeClient, new[]
        {
            "The response addresses the user's travel request",
            "Appropriate tools were used (search before book)",
            "The output includes actionable flight information"
        });

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var testCase = testCases[i];

            var context = new EvaluationContext
            {
                Input = testCase.Input,
                Output = result.ActualOutput ?? "",
                ToolUsage = result.ToolUsage
            };

            var judgeResult = await completionMetric.EvaluateAsync(context);

            Console.Write($"      {testCase.Name}: ");
            Console.ForegroundColor = judgeResult.Passed ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write($"llm_task_completion={judgeResult.Score}/100");
            Console.ResetColor();
            Console.WriteLine($"  ({judgeResult.Explanation?.Split('.').FirstOrDefault()})");
        }

        Console.WriteLine();
    }

    // ─── Display Helpers ─────────────────────────────────────────────

    private static void PrintToolResult(TestResult result)
    {
        Console.Write($"      {(result.Passed ? "✅" : "❌")} {result.TestName}: score={result.Score}");
        if (result.ToolUsage != null && result.ToolUsage.Count > 0)
            Console.Write($"  tools=[{string.Join(" → ", result.ToolUsage.Calls.Select(c => c.Name))}]");
        if (result.Performance != null)
        {
            Console.Write($"  {result.Performance.TotalDuration.TotalMilliseconds:F0}ms");
            if (result.Performance.EstimatedCost > 0)
                Console.Write($"  ${result.Performance.EstimatedCost:F4}");
        }
        Console.WriteLine();
    }

    private static void PrintPerformanceSummary(List<TestResult> results)
    {
        Console.WriteLine("      ┌─────────────────────────────┬──────────┬──────────┬──────────┐");
        Console.WriteLine("      │ Validation                  │ Latency  │ Tokens   │ Cost     │");
        Console.WriteLine("      ├─────────────────────────────┼──────────┼──────────┼──────────┤");
        foreach (var r in results)
        {
            var name = (r.TestName ?? "").PadRight(27);
            var latency = r.Performance != null
                ? r.Performance.TotalDuration.TotalMilliseconds.ToString("F0").PadLeft(5) + "ms"
                : "    N/A";
            var tokens = r.Performance?.TotalTokens?.ToString().PadLeft(5) ?? "  N/A";
            var cost = r.Performance?.EstimatedCost > 0
                ? "$" + r.Performance.EstimatedCost.Value.ToString("F4").PadLeft(6)
                : "  $0.00";
            Console.WriteLine($"      │ {name} │ {latency} │ {tokens}  │ {cost} │");
        }
        Console.WriteLine("      └─────────────────────────────┴──────────┴──────────┴──────────┘");
    }

    private static void PrintPass(string description)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"      ✅ {description}");
        Console.ResetColor();
    }

    private static void PrintFail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"      ❌ {message}");
        Console.ResetColor();
    }

    private static void ShowSection(string title, string subtitle)
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine($"  {title} - {subtitle}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════\n");
    }

    private static void ShowCode(string code)
    {
        Console.WriteLine("   Code example:");
        Console.ForegroundColor = ConsoleColor.Cyan;
        foreach (var line in code.Split('\n'))
            Console.WriteLine($"       {line}");
        Console.ResetColor();
        Console.WriteLine();
    }
}
