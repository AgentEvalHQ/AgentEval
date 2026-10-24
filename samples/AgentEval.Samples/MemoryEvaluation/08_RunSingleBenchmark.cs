// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Extensions;
using AgentEval.Benchmarks;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample G9: Run a Single Memory Benchmark
///
/// Pick a benchmark preset (Quick/Standard/Full), run it once with the memory-optimized prompt, save the baseline,
/// and open the report. From the menu it asks which preset; run non-interactively (<c>dotnet run -- &lt;n&gt;</c>,
/// <c>AGENTEVAL_SAMPLES_NONINTERACTIVE=1</c>, redirected input) it runs Quick, or the preset
/// <c>--preset quick|standard|full</c> names, and prints the report path instead of serving it.
///
/// Each baseline's name carries the run's time, so consecutive runs are separate points on the timeline.
///
/// Requires: a model provider — AZURE_OPENAI_* or BITDEER_API_KEY or OPENAI_COMPATIBLE_* (see AIConfig)
/// </summary>
/// <remarks>
/// Non-interactive runs default to Quick rather than skipping: the sample exists to run one preset and save its
/// baseline, Quick is the cheapest preset and the one every preset-driven sample runs by default, and a skip would
/// leave a sweep or CI job never measuring anything here. Until 2026-10 it printed the menu, read nothing, ran nothing
/// and exited 0.
/// </remarks>
public static class RunSingleBenchmark
{
    private const string AgentName = "MemoryAgent";

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   This sample requires a model provider (Azure OpenAI, Bitdeer, or any OpenAI-compatible endpoint).\n");
            SampleOutcome.Skip("no chat provider configured");
            return;
        }

        // ──────────────────────────────────────────────────────────
        //  Step 1: Pick a benchmark preset
        // ──────────────────────────────────────────────────────────

        var benchmark = MemoryBenchmarkSampleSupport.ResolveBenchmark();
        if (benchmark == null)
        {
            Console.WriteLine("\n   Cancelled: nothing ran.\n");
            SampleOutcome.Skip("cancelled at the preset menu: no benchmark ran");
            return;
        }

        // ──────────────────────────────────────────────────────────
        //  Step 2: Set up runner + agent
        // ──────────────────────────────────────────────────────────

        Console.WriteLine($"\nRunning {benchmark.Name} benchmark ({benchmark.Categories.Count} categories) on {AIConfig.ModelDeployment}...\n");

        var chatClient = AIConfig.CreateChatClient(AIConfig.ModelDeployment);
        var judgeModel = AIConfig.JudgeModelDeployment;
        Console.WriteLine($"   Judge: {SampleJudgeModel.DescribeFor(AIConfig.ModelDeployment, judgeModel)}");

        var benchmarkRunner = MemoryBenchmarkRunner.Create(AIConfig.CreateChatClient(judgeModel));
        var store = new JsonFileBaselineStore();

        var agent = chatClient.AsEvaluableAgent(
            name: AgentName,
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
        MemoryBenchmarkSampleSupport.RecordVerdict(AIConfig.ModelDeployment, result);

        // ──────────────────────────────────────────────────────────
        //  Step 4: Save the baseline, named with the run's time
        // ──────────────────────────────────────────────────────────

        var config = new AgentBenchmarkConfig
        {
            AgentName = AgentName,
            ModelId = AIConfig.ModelDeployment,
            ReducerStrategy = "None (memory-optimized prompt)",
            MemoryProvider = "InMemoryChatHistory",
            CustomConfig = SampleJudgeModel.BaselineConfiguration(judgeModel, AIConfig.ProviderTag)
        };

        // Name includes timestamp so each run gets a unique baseline file
        var baselineName = $"{benchmark.Name} — {DateTimeOffset.UtcNow:MMM dd HH:mm}";
        var baseline = result.ToBaseline(baselineName, config,
            description: $"Judge: {SampleJudgeModel.DescribeFor(AIConfig.ModelDeployment, judgeModel)}",
            tags: [benchmark.Name.ToLowerInvariant()]);

        await store.SaveAsync(baseline);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n   Baseline saved: {baseline.Name}");
        Console.WriteLine($"   Directory: {Path.Combine(store.GetReportDirectory(AgentName), "baselines")}");
        Console.ResetColor();

        // ──────────────────────────────────────────────────────────
        //  Step 5: Serve the report
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("\nHTML report...\n");
        MemoryBenchmarkSampleSupport.ServeOrPrintReport(store, AgentName);
    }

    private static void PrintResult(MemoryBenchmarkResult result, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.Write("   Result: ");
        Console.ForegroundColor = !result.IsComplete ? ConsoleColor.DarkYellow : result.OverallScore >= 80 ? ConsoleColor.Green :
                                  result.OverallScore >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write(MemoryBenchmarkSampleSupport.FormatSummary(result));
        Console.ResetColor();
        Console.WriteLine($"  ({elapsed.TotalMinutes:F1} min)");
        Console.WriteLine();

        MemoryBenchmarkSampleSupport.PrintCategories(result);

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
        Console.WriteLine("AgentEval Memory — Sample G9: Run Single Benchmark");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Pick a preset (Quick when non-interactive), run it, save the baseline, view the report.");
        Console.WriteLine();
    }
}
