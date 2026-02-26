// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentEval.Benchmarks;
using AgentEval.DataLoaders;
using AgentEval.MAF;
using AgentEval.Models;
using System.ComponentModel;
using ChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace AgentEval.Samples;

/// <summary>
/// Sample 23: Benchmark System — Real Performance &amp; Agentic Benchmarking
/// 
/// This sample shows how to use AgentEval's benchmark classes against a
/// real Azure OpenAI-backed agent, loading test data from JSONL files
/// via <see cref="DatasetLoaderFactory"/> (the industry-standard format
/// for AI benchmark datasets — used by BFCL, GAIA, MMLU, GSM8K, etc.).
/// 
/// It demonstrates:
///   1. Loading all 3 JSONL dataset types (latency, cost, tool-accuracy)
///   2. Converting <see cref="AgentEval.Models.DatasetTestCase"/> to benchmark types via bridge extensions
///   3. Running a tool accuracy benchmark as the showcase (full JSONL → bridge → benchmark pipeline)
/// 
/// Only one benchmark type is executed to keep API costs low while still
/// proving the complete data loading and conversion pipeline.
/// 
/// Everything here is REAL: actual LLM calls, actual measurements, actual results.
/// Test data is loaded from <c>samples/datasets/benchmark-*.jsonl</c> files.
/// No hardcoded fallbacks — JSONL datasets must be present.
/// 
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// ⏱️ Time to understand: 5 minutes
/// ⏱️ Time to run: ~15–30 seconds (depends on model latency)
/// </summary>
public static class Sample23_BenchmarkSystem
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            PrintMissingCredentialsBox();
            return;
        }

        Console.WriteLine($"   Endpoint: {AIConfig.Endpoint}");
        Console.WriteLine($"   Model:    {AIConfig.ModelDeployment}\n");

        // ── Create a real agent with tools ──────────────────────────────────
        var agent = CreateAgentWithTools();
        var adapter = new MAFAgentAdapter(agent);

        // ── Load benchmark datasets from JSONL ─────────────────────────────
        // Demonstrates loading from all 3 JSONL dataset types via DatasetLoaderFactory.
        // This proves the JSONL → DatasetTestCase pipeline works for all benchmark types.
        Console.WriteLine("Loading benchmark datasets from JSONL...\n");

        var latencyPrompts = await LoadPromptsFromJsonl("benchmark-latency.jsonl");
        var costPrompts = await LoadPromptsFromJsonl("benchmark-cost.jsonl");
        var toolCases = await LoadToolAccuracyCases("benchmark-tool-accuracy.jsonl");

        Console.WriteLine($"   Loaded {latencyPrompts.Count} latency prompts");
        Console.WriteLine($"   Loaded {costPrompts.Count} cost prompts");
        Console.WriteLine($"   Loaded {toolCases.Count} tool accuracy test cases\n");

        // ── Run Tool Accuracy Benchmark (showcase) ──────────────────────────
        // We run only the tool accuracy benchmark here to keep API costs low.
        // It demonstrates the full pipeline: JSONL → DatasetTestCase → bridge → benchmark.
        // See PerformanceBenchmark for latency/throughput/cost benchmarks.
        Console.WriteLine("Running Tool Accuracy Benchmark (JSONL → bridge → benchmark)\n");

        var agenticBenchmark = new AgenticBenchmark(adapter, evaluator: null,
            new AgenticBenchmarkOptions { Verbose = true });

        var toolResult = await agenticBenchmark.RunToolAccuracyBenchmarkAsync(toolCases);

        PrintToolAccuracyResults(toolResult);

        // ── Summary ─────────────────────────────────────────────────────────
        Console.WriteLine($"\n   SUMMARY: {toolResult.PassedTests}/{toolResult.TotalTests} tool accuracy tests passed ({toolResult.OverallAccuracy:P0})");
        Console.WriteLine($"   Loaded {latencyPrompts.Count + costPrompts.Count + toolCases.Count} total items from 3 JSONL files");
        PrintKeyTakeaways();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // JSONL Dataset Loading
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Loads prompts from a JSONL file via <see cref="DatasetLoaderFactory"/>.
    /// Each line's <c>input</c> field becomes a prompt string.
    /// </summary>
    private static async Task<List<string>> LoadPromptsFromJsonl(string fileName)
    {
        var path = ResolveDatasetPath(fileName)
            ?? throw new FileNotFoundException(
                $"Benchmark dataset not found: {fileName}. " +
                $"Ensure the file exists in samples/datasets/. " +
                $"See docs/benchmarks.md for JSONL format details.");

        var dataset = await DatasetLoaderFactory.LoadAsync(path);
        return dataset.Select(dc => dc.Input).ToList();
    }

    /// <summary>
    /// Loads tool accuracy test cases from a JSONL file via <see cref="DatasetLoaderFactory"/>,
    /// converting each <see cref="DatasetTestCase"/> to a <see cref="ToolAccuracyTestCase"/>
    /// using the <see cref="DatasetTestCaseBenchmarkExtensions.ToToolAccuracyTestCase"/> bridge.
    /// </summary>
    private static async Task<List<ToolAccuracyTestCase>> LoadToolAccuracyCases(string fileName)
    {
        var path = ResolveDatasetPath(fileName)
            ?? throw new FileNotFoundException(
                $"Benchmark dataset not found: {fileName}. " +
                $"Ensure the file exists in samples/datasets/. " +
                $"See docs/benchmarks.md for JSONL format details.");

        var dataset = await DatasetLoaderFactory.LoadAsync(path);
        return dataset.Select(dc => dc.ToToolAccuracyTestCase()).ToList();
    }

    /// <summary>
    /// Resolves a dataset file path relative to the samples/datasets directory.
    /// </summary>
    private static string? ResolveDatasetPath(string fileName)
    {
        // Try relative to the project output directory
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "datasets", fileName);
        if (File.Exists(path)) return Path.GetFullPath(path);

        // Try relative to working directory (repo root)
        path = Path.Combine("samples", "datasets", fileName);
        if (File.Exists(path)) return Path.GetFullPath(path);

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Agent Setup
    // ═══════════════════════════════════════════════════════════════════════════

    private static AIAgent CreateAgentWithTools()
    {
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        return new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "BenchmarkAgent",
            ChatOptions = new ChatOptions
            {
                Instructions = "You are a helpful assistant. Use the available tools when appropriate.",
                Tools =
                [
                    AIFunctionFactory.Create(GetWeather),
                    AIFunctionFactory.Create(Calculate)
                ]
            }
        });
    }

    [Description("Get the current weather for a city")]
    private static string GetWeather([Description("City name")] string city) =>
        $"The weather in {city} is 18°C and partly cloudy.";

    [Description("Calculate a math expression and return the result")]
    private static string Calculate([Description("Math expression to evaluate")] string expression) =>
        $"Result of {expression} = (calculated)";

    // ═══════════════════════════════════════════════════════════════════════════
    // Result Printers
    // ═══════════════════════════════════════════════════════════════════════════

    private static void PrintToolAccuracyResults(ToolAccuracyResult r)
    {
        Console.WriteLine();
        foreach (var t in r.Results)
        {
            var icon = t.Passed ? "PASS" : "FAIL";
            Console.Write($"      [{icon}] {t.TestCaseName}");
            if (t.ToolsMissed.Count > 0)
                Console.Write($"  (missed: {string.Join(", ", t.ToolsMissed)})");
            if (t.ParameterErrors.Count > 0)
                Console.Write($"  (params: {string.Join("; ", t.ParameterErrors)})");
            if (!string.IsNullOrEmpty(t.Error))
                Console.Write($"  (error: {t.Error})");
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("   +----------------------------------------------------------+");
        Console.WriteLine("   |             TOOL ACCURACY BENCHMARK RESULTS              |");
        Console.WriteLine("   +----------------------------------------------------------+");
        Console.WriteLine($"   |  Passed / Total:         {r.PassedTests,4} / {r.TotalTests,-24}|");
        Console.WriteLine($"   |  Overall Accuracy:       {r.OverallAccuracy,27:P1}   |");
        Console.WriteLine("   +----------------------------------------------------------+");
    }


    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("+============================================================================+");
        Console.WriteLine("|                    Sample 23: Benchmark System                              |");
        Console.WriteLine("|          Real Performance & Agentic Benchmarks with AgentEval               |");
        Console.WriteLine("+============================================================================+");
        Console.WriteLine();
    }

    private static void PrintMissingCredentialsBox()
    {
        Console.WriteLine("+-----------------------------------------------------------------------------+");
        Console.WriteLine("|  SKIPPING SAMPLE 23 - Azure OpenAI Credentials Required                     |");
        Console.WriteLine("|                                                                             |");
        Console.WriteLine("|  This sample runs REAL benchmarks against a live agent.                     |");
        Console.WriteLine("|  Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT  |");
        Console.WriteLine("+-----------------------------------------------------------------------------+");
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine("\n   KEY TAKEAWAYS:");
        Console.WriteLine("   - Test data loaded from JSONL files via DatasetLoaderFactory (industry standard!)");
        Console.WriteLine("   - DatasetTestCase → ToolAccuracyTestCase bridge enables JSONL → Benchmark pipeline");
        Console.WriteLine("   - PerformanceBenchmark provides latency percentiles and cost estimation");
        Console.WriteLine("   - AgenticBenchmark verifies tool selection against declared expectations");
        Console.WriteLine("   - All data shown above comes from REAL LLM calls — no faked numbers");
        Console.WriteLine("\n   NEXT: Explore Sample 24 for calibrated evaluation!\n");
    }
}