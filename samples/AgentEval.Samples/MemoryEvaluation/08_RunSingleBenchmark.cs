// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using AgentEval.Core;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Extensions;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Run a Single Memory Benchmark
///
/// Interactive sample that lets you pick a benchmark preset (Quick/Standard/Full),
/// runs it once with the memory-optimized prompt, saves the baseline, and opens the report.
///
/// Each run saves with a 2-hour offset so consecutive runs appear distinct on the timeline.
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// </summary>
public static class RunSingleBenchmark
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   This sample requires real Azure OpenAI credentials.\n");
            return;
        }

        // ──────────────────────────────────────────────────────────
        //  Step 1: Pick a benchmark preset
        // ──────────────────────────────────────────────────────────

        var benchmark = PromptForBenchmark();
        if (benchmark == null) return;

        // ──────────────────────────────────────────────────────────
        //  Step 2: Set up runner + agent
        // ──────────────────────────────────────────────────────────

        Console.WriteLine($"\nRunning {benchmark.Name} benchmark ({benchmark.Categories.Count} categories)...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var benchmarkRunner = MemoryBenchmarkRunner.Create(chatClient);
        var store = new JsonFileBaselineStore();

        var agent = chatClient.AsEvaluableAgent(
            name: "MemoryAgent",
            systemPrompt: """
                You are a helpful assistant with excellent memory.
                IMPORTANT: Remember ALL facts the user tells you. When asked about something
                you were told, include the SPECIFIC details in your response.
                Always recall names, dates, preferences, and personal information accurately.
                Keep responses concise but include every relevant fact you remember.
                """,
            includeHistory: true);

        // ──────────────────────────────────────────────────────────
        //  Step 3: Run the benchmark
        // ──────────────────────────────────────────────────────────

        var progressReporter = new Progress<BenchmarkProgress>(p =>
        {
            var status = p.Skipped ? "SKIP" : $"{p.Score:F1}%";
            var elapsed = $"{(int)p.Elapsed.TotalMinutes}m {p.Elapsed.Seconds:D2}s";
            var remaining = $"~{(int)p.EstimatedRemaining.TotalMinutes}m {p.EstimatedRemaining.Seconds:D2}s";
            Console.Write($"\r   [{p.CompletedCategories}/{p.TotalCategories}] {p.CategoryName}: {status} | {elapsed} elapsed | {remaining} remaining   ");
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await benchmarkRunner.RunBenchmarkAsync(agent, benchmark, progressReporter);
        sw.Stop();

        // Move past the progress line
        Console.WriteLine();

        PrintResult(result, sw.Elapsed);

        // ──────────────────────────────────────────────────────────
        //  Step 4: Save baseline with 2h offset for timeline spread
        // ──────────────────────────────────────────────────────────

        var config = new AgentBenchmarkConfig
        {
            AgentName = "MemoryAgent",
            ModelId = AIConfig.ModelDeployment,
            ReducerStrategy = "None (memory-optimized prompt)",
            MemoryProvider = "InMemoryChatHistory"
        };

        // Name includes timestamp so each run gets a unique baseline file
        var baselineName = $"{benchmark.Name} — {DateTimeOffset.UtcNow:MMM dd HH:mm}";
        var baseline = result.ToBaseline(baselineName, config, tags: [benchmark.Name.ToLowerInvariant()]);

        await store.SaveAsync(baseline);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n   Baseline saved: {baseline.Name}");
        Console.WriteLine($"   Directory: .agenteval/benchmarks/memoryagent/baselines/");
        Console.ResetColor();

        // ──────────────────────────────────────────────────────────
        //  Step 5: Serve the report
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("\nOpening HTML report...\n");
        var serverProcess = store.OpenReport("MemoryAgent");

        if (serverProcess != null)
        {
            Console.WriteLine("   Press Enter to stop the server and exit...");
            Console.ReadLine();
            serverProcess.Kill();
            serverProcess.Dispose();
        }
    }

    private static MemoryBenchmark? PromptForBenchmark()
    {
        Console.WriteLine("Select a benchmark preset:\n");
        Console.WriteLine("   [1] Quick      3 categories   ~3 min    Basic Retention, Temporal, Noise");
        Console.WriteLine("   [2] Standard   8 categories   ~10 min   + Reach-Back, FactUpdate, MultiTopic, Abstention, Preference");
        Console.WriteLine("   [3] Full      12 categories   ~25 min   + CrossSession, Reducer, Conflict, MultiSession, Preference");
        Console.WriteLine("   [Q] Cancel\n");
        Console.Write("   Choice: ");

        var key = Console.ReadLine()?.Trim().ToUpperInvariant();
        return key switch
        {
            "1" => MemoryBenchmark.Quick,
            "2" => MemoryBenchmark.Standard,
            "3" => MemoryBenchmark.Full,
            _ => null
        };
    }

    private static void PrintResult(MemoryBenchmarkResult result, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.Write("   Result: ");
        Console.ForegroundColor = result.OverallScore >= 80 ? ConsoleColor.Green :
                                  result.OverallScore >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{result.OverallScore:F1}%");
        Console.ResetColor();
        Console.WriteLine($"  Grade: {result.Grade}  {new string('*', result.Stars)}{new string('o', 5 - result.Stars)}  ({elapsed.TotalMinutes:F1} min)");
        Console.WriteLine();

        foreach (var cat in result.CategoryResults)
        {
            var status = cat.Skipped ? "SKIP" : $"{cat.Score:F1}%";
            Console.ForegroundColor = cat.Skipped ? ConsoleColor.DarkGray :
                                      cat.Score >= 80 ? ConsoleColor.Green :
                                      cat.Score >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
            Console.Write($"     {cat.CategoryName,-24} {status,7}");
            Console.ResetColor();

            if (cat.Skipped && cat.SkipReason != null)
                Console.Write($"  ({cat.SkipReason})");
            Console.WriteLine();
        }

        if (result.Recommendations.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n   Recommendations:");
            foreach (var rec in result.Recommendations)
                Console.WriteLine($"     - {rec}");
            Console.ResetColor();
        }
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("AgentEval Memory — Run Single Benchmark");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Pick a preset, run it, save the baseline, view the report.");
        Console.WriteLine();
    }
}
