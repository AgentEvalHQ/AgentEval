// SPDX-License-Identifier: MIT
// Copyright (c) 2025-2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Core;
using System.ComponentModel;
using System.Diagnostics;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 06: Benchmarks - Real performance and agentic benchmarks
/// 
/// This demonstrates:
/// - Running real prompts through an agent and collecting latency data
/// - Computing statistical percentiles (p50, p90, p99) from actual measurements
/// - Evaluating tool selection accuracy against expected tool calls
/// - Performance tracking with token usage and cost estimation
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 5 minutes
/// </summary>
public static class Sample06_Benchmarks
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine($"   🔗 Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   🤖 Model: {AIConfig.ModelDeployment}\n");

        var agent = CreateAgentWithTools();
        var harness = new MAFEvaluationHarness(verbose: false);
        var adapter = new MAFAgentAdapter(agent);

        await RunPerformanceBenchmark(harness, adapter);
        await RunAgenticBenchmark(harness, adapter);
        PrintKeyTakeaways();
    }

    private static async Task RunPerformanceBenchmark(MAFEvaluationHarness harness, MAFAgentAdapter adapter)
    {
        Console.WriteLine("📊 PART 1: PERFORMANCE BENCHMARK\n");

        var prompts = new[]
        {
            "What is 2 + 2?",
            "Explain software testing in one sentence.",
            "List three benefits of exercise.",
            "What is the capital of France?",
            "Summarize the concept of recursion."
        };

        Console.WriteLine($"   Running {prompts.Length} prompts with performance tracking...\n");

        var latencies = new List<double>();
        int totalPromptTokens = 0, totalCompletionTokens = 0;

        foreach (var prompt in prompts)
        {
            var testCase = new TestCase { Name = "Bench", Input = prompt };
            var sw = Stopwatch.StartNew();
            var result = await harness.RunEvaluationAsync(adapter, testCase,
                new EvaluationOptions { TrackPerformance = true });
            sw.Stop();

            latencies.Add(sw.Elapsed.TotalMilliseconds);
            if (result.Performance != null)
            {
                totalPromptTokens += result.Performance.PromptTokens ?? 0;
                totalCompletionTokens += result.Performance.CompletionTokens ?? 0;
            }
            Console.WriteLine($"      ✅ \"{Truncate(prompt, 40)}\" — {sw.Elapsed.TotalMilliseconds:F0}ms");
        }

        latencies.Sort();
        PrintPerformanceResults(latencies, totalPromptTokens, totalCompletionTokens);
    }

    private static void PrintPerformanceResults(List<double> latencies, int promptTokens, int completionTokens)
    {
        var mean = latencies.Average();
        var p50 = Percentile(latencies, 0.50);
        var p90 = Percentile(latencies, 0.90);
        var p99 = Percentile(latencies, 0.99);

        Console.WriteLine("\n   ┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("   │              PERFORMANCE BENCHMARK RESULTS              │");
        Console.WriteLine("   ├─────────────────────────────────────────────────────────┤");
        Console.WriteLine($"   │  Total Runs:           {latencies.Count,30} │");
        Console.WriteLine($"   │  Mean Latency:         {mean,27:F1} ms │");
        Console.WriteLine($"   │  P50 Latency:          {p50,27:F1} ms │");
        Console.WriteLine($"   │  P90 Latency:          {p90,27:F1} ms │");
        Console.WriteLine($"   │  P99 Latency:          {p99,27:F1} ms │");
        Console.WriteLine($"   │  Total Prompt Tokens:  {promptTokens,30} │");
        Console.WriteLine($"   │  Total Compl. Tokens:  {completionTokens,30} │");
        Console.WriteLine("   └─────────────────────────────────────────────────────────┘\n");
    }

    private static async Task RunAgenticBenchmark(MAFEvaluationHarness harness, MAFAgentAdapter adapter)
    {
        Console.WriteLine("📊 PART 2: AGENTIC BENCHMARK (Tool Accuracy)\n");

        var testCases = new (string Name, string Prompt, string[] ExpectedTools)[]
        {
            ("Weather Query", "What is the weather in Seattle?", ["GetWeather"]),
            ("Math Calculation", "Calculate 15 * 7 + 3", ["Calculate"]),
            ("Multi-Step", "What is the weather in Paris and also calculate 100 / 4?", ["GetWeather", "Calculate"])
        };

        int passed = 0;
        int totalExpected = 0, totalMatched = 0;

        foreach (var (name, prompt, expectedTools) in testCases)
        {
            var testCase = new TestCase { Name = name, Input = prompt };
            var result = await harness.RunEvaluationAsync(adapter, testCase,
                new EvaluationOptions { TrackPerformance = true });

            var actualTools = result.ToolUsage?.Calls?.Select(t => t.Name).ToHashSet() 
                ?? new HashSet<string>();

            int matched = expectedTools.Count(t => actualTools.Contains(t));
            totalExpected += expectedTools.Length;
            totalMatched += matched;

            bool allFound = matched == expectedTools.Length;
            if (allFound) passed++;

            var icon = allFound ? "✅" : "⚠️";
            Console.WriteLine($"      {icon} {name}: expected [{string.Join(", ", expectedTools)}] → got [{string.Join(", ", actualTools)}]");
        }

        var accuracy = totalExpected > 0 ? (double)totalMatched / totalExpected * 100 : 0;
        PrintAgenticResults(testCases.Length, passed, accuracy);
    }

    private static void PrintAgenticResults(int total, int passed, double accuracy)
    {
        Console.WriteLine("\n   ┌─────────────────────────────────────────────────────────┐");
        Console.WriteLine("   │               AGENTIC BENCHMARK RESULTS                 │");
        Console.WriteLine("   ├─────────────────────────────────────────────────────────┤");
        Console.WriteLine($"   │  Total Tests:          {total,30} │");
        Console.WriteLine($"   │  Passed:               {passed,30} │");
        Console.WriteLine($"   │  Tool Accuracy:        {accuracy,27:F1}% │");
        Console.WriteLine("   └─────────────────────────────────────────────────────────┘\n");
    }

    private static AIAgent CreateAgentWithTools()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "BenchmarkAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a helpful assistant. Use the available tools when appropriate to answer questions.",
                Tools = [AIFunctionFactory.Create(GetWeather), AIFunctionFactory.Create(Calculate)]
            }
        });
    }

    [Description("Get the current weather for a city")]
    private static string GetWeather([Description("City name")] string city)
    {
        return $"The weather in {city} is 18°C and partly cloudy.";
    }

    [Description("Calculate a math expression and return the result")]
    private static string Calculate([Description("Math expression to evaluate")] string expression)
    {
        return $"Result: {expression} = (calculated)";
    }

    private static double Percentile(List<double> sorted, double p)
    {
        int index = Math.Min(sorted.Count - 1, (int)(sorted.Count * p));
        return sorted[index];
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════════════════════╗
║                                                                               ║
║   📊 SAMPLE 06: PERFORMANCE & AGENTIC BENCHMARKS                             ║
║   Real latency measurement, percentiles, and tool accuracy                    ║
║                                                                               ║
╚═══════════════════════════════════════════════════════════════════════════════╝
");
        Console.ResetColor();
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(@"
   ┌─────────────────────────────────────────────────────────────────────────────┐
   │  ⚠️  SKIPPING SAMPLE 06 - Azure OpenAI Credentials Required               │
   ├─────────────────────────────────────────────────────────────────────────────┤
   │  This sample runs real prompts to measure actual latency and tool accuracy. │
   │                                                                             │
   │  Set these environment variables:                                           │
   │    AZURE_OPENAI_ENDPOINT     - Your Azure OpenAI endpoint                   │
   │    AZURE_OPENAI_API_KEY      - Your API key                                 │
   │    AZURE_OPENAI_DEPLOYMENT   - Chat model (e.g., gpt-4o)                    │
   └─────────────────────────────────────────────────────────────────────────────┘
");
        Console.ResetColor();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("💡 KEY TAKEAWAYS:");
        Console.WriteLine("   • Measure real latency — never fake benchmark data");
        Console.WriteLine("   • Use p50/p90/p99 percentiles for SLA enforcement");
        Console.WriteLine("   • Compare expected vs actual tool calls for agentic accuracy");
        Console.WriteLine("   • Track token usage to estimate and control costs");
        Console.WriteLine("\n🔗 NEXT: Run Sample 07 to see snapshot testing!\n");
    }
}

