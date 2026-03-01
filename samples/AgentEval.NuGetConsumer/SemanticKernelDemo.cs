// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Assertions;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Metrics.Agentic;
using AgentEval.Models;
using AgentEval.NuGetConsumer.Adapters;
using AgentEval.NuGetConsumer.Tools;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace AgentEval.NuGetConsumer;

/// <summary>
/// Evaluates a real Semantic Kernel ChatCompletionAgent with FlightPlugin.
///
/// Architecture:
///   Kernel + AzureOpenAI → ChatCompletionAgent + FlightPlugin
///   → SKAgentAdapter (IEvaluableAgent)
///   → MAFEvaluationHarness → TestResult (tools, perf, output — all automatic)
///   → Fluent assertions + code metrics + LLM-as-judge
/// </summary>
public static class SemanticKernelDemo
{
    public static async Task RunAsync()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  ✈️  SK ChatCompletionAgent + AgentEval Evaluation");
        Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

        if (!Config.IsConfigured)
        {
            Console.WriteLine("  ❌ Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT.\n");
            return;
        }

        // ─── Step 1: Build Kernel + ChatCompletionAgent + FlightPlugin ───
        Console.WriteLine("  📝 Step 1: Build SK ChatCompletionAgent with FlightPlugin\n");

        var kernel = Kernel.CreateBuilder()
            .AddAzureOpenAIChatCompletion(
                deploymentName: Config.Model,
                endpoint: Config.Endpoint.ToString(),
                apiKey: Config.KeyCredential.Key)
            .Build();

        var plugin = KernelPluginFactory.CreateFromType<FlightPlugin>();

        ChatCompletionAgent agent = new()
        {
            Name = "SK-FlightAgent",
            Instructions = """
                You are a flight booking assistant. You help users search for flights
                and book them. When asked to find flights, use the SearchFlights tool.
                When asked to book, use BookFlight.
                """,
            Kernel = kernel,
            Arguments = new KernelArguments(new PromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
            })
        };
        agent.Kernel.Plugins.Add(plugin);

        Console.WriteLine($"     Agent:  {agent.Name}");
        Console.WriteLine($"     Model:  {Config.Model}");
        Console.WriteLine($"     Plugin: FlightPlugin ({plugin.Count()} functions)\n");

        // ─── Step 2: Wrap in SKAgentAdapter → run via harness ────────────
        Console.WriteLine("  📝 Step 2: Evaluate via harness (tool tracking + perf — automatic)\n");

        var evaluableAgent = new SKAgentAdapter(agent);
        var harness = new MAFEvaluationHarness(verbose: true);

        var testCase = new TestCase
        {
            Name = "Search Flights to Paris",
            Input = "Find me flights from New York to Paris on March 15th",
            ExpectedOutputContains = "Paris",
            ExpectedTools = ["SearchFlights"],
            PassingScore = 70
        };

        var result = await harness.RunEvaluationAsync(evaluableAgent, testCase,
            new EvaluationOptions
            {
                TrackTools = true,
                TrackPerformance = true,
                ModelName = Config.Model
            });

        Console.WriteLine($"\n     Score: {result.Score}/100  Passed: {result.Passed}");
        Console.WriteLine($"     Output: {Truncate(result.ActualOutput, 200)}");
        if (result.ToolUsage?.Count > 0)
            Console.WriteLine($"     Tools: [{string.Join(" → ", result.ToolUsage.Calls.Select(c => c.Name))}]");
        if (result.Performance != null)
            Console.WriteLine($"     Latency: {result.Performance.TotalDuration.TotalMilliseconds:F0}ms");
        Console.WriteLine();

        // ─── Step 3: Fluent tool assertions ──────────────────────────────
        Console.WriteLine("  📝 Step 3: Fluent tool assertions\n");

        if (result.ToolUsage == null || result.ToolUsage.Count == 0)
        {
            Fail("No tool usage captured — agent may have errored before calling tools");
        }
        else
        {
            try
            {
                result.ToolUsage.Should()
                    .HaveCalledTool("SearchFlights",
                        because: "user asked to find flights to Paris")
                    .And()
                    .HaveNoErrors();
                Pass("SearchFlights was called, no errors");
            }
            catch (ToolAssertionException ex)
            {
                Fail(ex.Message);
            }
        }
        Console.WriteLine();

        // ─── Step 4: Code metrics (free — no LLM cost) ──────────────────
        Console.WriteLine("  📝 Step 4: Code metrics\n");

        var context = new EvaluationContext
        {
            Input = testCase.Input,
            Output = result.ActualOutput ?? "",
            ToolUsage = result.ToolUsage
        };

        var selectionResult = await new ToolSelectionMetric(["SearchFlights"])
            .EvaluateAsync(context);
        var efficiencyResult = await new ToolEfficiencyMetric(maxExpectedCalls: 2)
            .EvaluateAsync(context);

        PrintMetric("code_tool_selection", selectionResult.Score, selectionResult.Passed);
        PrintMetric("code_tool_efficiency", efficiencyResult.Score, efficiencyResult.Passed);
        Console.WriteLine();

        // ─── Step 5: LLM-as-Judge ───────────────────────────────────────
        Console.WriteLine($"  📝 Step 5: LLM-as-Judge ({Config.Model})\n");

        var judgeClient = AgentFactory.CreateEvaluatorChatClient();
        var judgeResult = await new TaskCompletionMetric(judgeClient,
        [
            "The response addresses the user's flight search request",
            "The SearchFlights tool was used appropriately",
            "The output includes relevant flight information"
        ]).EvaluateAsync(context);

        PrintMetric("llm_task_completion", judgeResult.Score, judgeResult.Passed);
        if (judgeResult.Explanation != null)
            Console.WriteLine($"     Reason: {judgeResult.Explanation.Split('.').FirstOrDefault()}");
        Console.WriteLine();

        // ─── Summary ─────────────────────────────────────────────────────
        var allPassed = result.Passed && selectionResult.Passed
            && efficiencyResult.Passed && judgeResult.Passed;
        Console.ForegroundColor = allPassed ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine($"  📊 score={result.Score}  selection={selectionResult.Score:F0}  " +
                          $"efficiency={efficiencyResult.Score:F0}  judge={judgeResult.Score:F0}  " +
                          $"latency={result.Performance?.TotalDuration.TotalMilliseconds:F0}ms  " +
                          $"{(allPassed ? "ALL PASSED ✅" : "SOME FAILED ❌")}");
        Console.ResetColor();
        Console.WriteLine();

        ShowCode("""
            // 1. Build SK agent (standard SK pattern)
            var kernel = Kernel.CreateBuilder()
                .AddAzureOpenAIChatCompletion(model, endpoint, key).Build();
            ChatCompletionAgent agent = new()
            {
                Name = "FlightAgent",
                Kernel = kernel,
                Arguments = new(new PromptExecutionSettings
                    { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() })
            };
            agent.Kernel.Plugins.Add(KernelPluginFactory.CreateFromType<FlightPlugin>());
            
            // 2. Wrap → evaluate → assert (AgentEval handles the rest)
            var evaluable = new SKAgentAdapter(agent);
            var result = await harness.RunEvaluationAsync(evaluable, testCase, options);
            
            result.ToolUsage!.Should()
                .HaveCalledTool("SearchFlights")
                .And().HaveNoErrors();
            """);
    }

    // ─── Display helpers ─────────────────────────────────────────────

    private static void Pass(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"     ✅ {msg}");
        Console.ResetColor();
    }

    private static void Fail(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"     ❌ {msg}");
        Console.ResetColor();
    }

    private static void PrintMetric(string name, double score, bool passed)
    {
        Console.ForegroundColor = passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write($"     {name}={score:F0}/100");
        Console.ResetColor();
        Console.WriteLine(passed ? " ✅" : " ❌");
    }

    private static string Truncate(string? text, int max) =>
        string.IsNullOrEmpty(text) ? "(empty)"
        : text.Length <= max ? text
        : text[..(max - 1)] + "…";

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
