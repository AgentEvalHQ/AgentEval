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
/// Memory Benchmark Reporting &amp; Comparison
///
/// This demonstrates:
/// - Running benchmarks with two different memory configurations
/// - Saving baselines with full agent config metadata
/// - Comparing configurations programmatically
/// - Generating an interactive HTML report
/// - Using the export bridge (.ToEvaluationReport()) for CI/CD integration
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// </summary>
public static class MemoryBenchmarkReporting
{
    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            Console.WriteLine("   This sample requires real Azure OpenAI credentials.");
            Console.WriteLine("   Memory benchmarks use an LLM judge.\n");
            return;
        }

        // ──────────────────────────────────────────────────────────
        //  Step 1: Create the benchmark runner + baseline store
        // ──────────────────────────────────────────────────────────
        //  MemoryBenchmarkRunner.Create(chatClient) wires everything internally.
        //  In production with DI, use: services.AddAgentEvalMemory()
        //  then inject IMemoryBenchmarkRunner and IBaselineStore.

        Console.WriteLine("Step 1: Creating benchmark runner...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var benchmarkRunner = MemoryBenchmarkRunner.Create(chatClient);
        var store = new JsonFileBaselineStore();

        Console.WriteLine($"   Endpoint:   {AIConfig.Endpoint}");
        Console.WriteLine($"   Deployment: {AIConfig.ModelDeployment}\n");

        // ──────────────────────────────────────────────────────────
        //  Step 2: Benchmark Config A — minimal system prompt
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 2: Running Quick benchmark — Config A (minimal prompt)...\n");

        var agentA = chatClient.AsEvaluableAgent(
            name: "MemoryAgent",
            systemPrompt: "You are a helpful assistant. Answer questions concisely.",
            includeHistory: true);

        var resultA = await benchmarkRunner.RunBenchmarkAsync(agentA, MemoryBenchmark.Quick);
        PrintResult("Config A (minimal)", resultA);

        var configA = new AgentBenchmarkConfig
        {
            AgentName = "MemoryAgent",
            ModelId = AIConfig.ModelDeployment,
            ReducerStrategy = "None (minimal prompt)",
            MemoryProvider = "InMemoryChatHistory"
        };

        var baselineA = resultA.ToBaseline("Minimal Prompt", configA, tags: ["config-a"]);
        await store.SaveAsync(baselineA);
        Console.WriteLine($"   Saved. ConfigurationId: {baselineA.ConfigurationId}\n");

        // ──────────────────────────────────────────────────────────
        //  Step 3: Benchmark Config B — memory-optimized prompt
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 3: Running Quick benchmark — Config B (memory-optimized)...\n");

        var agentB = chatClient.AsEvaluableAgent(
            name: "MemoryAgent",
            systemPrompt: """
                You are a helpful assistant with excellent memory.
                IMPORTANT: Remember ALL facts the user tells you. When asked about something
                you were told, include the SPECIFIC details in your response.
                Always recall names, dates, preferences, and personal information accurately.
                Keep responses concise but include every relevant fact you remember.
                """,
            includeHistory: true);

        var resultB = await benchmarkRunner.RunBenchmarkAsync(agentB, MemoryBenchmark.Quick);
        PrintResult("Config B (memory-optimized)", resultB);

        var configB = new AgentBenchmarkConfig
        {
            AgentName = "MemoryAgent",
            ModelId = AIConfig.ModelDeployment,
            ReducerStrategy = "None (memory-optimized prompt)",
            MemoryProvider = "InMemoryChatHistory"
        };

        var baselineB = resultB.ToBaseline("Memory-Optimized Prompt", configB, tags: ["config-b"]);
        await store.SaveAsync(baselineB);
        Console.WriteLine($"   Saved. ConfigurationId: {baselineB.ConfigurationId}");
        Console.WriteLine($"   Different from Config A -> report shows them as separate pentagon shapes.\n");

        // ──────────────────────────────────────────────────────────
        //  Step 4: Compare the two configurations
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 4: Comparing configurations...\n");

        var comparer = new BaselineComparer();
        var comparison = comparer.Compare([baselineA, baselineB]);

        PrintComparison(comparison);

        // ──────────────────────────────────────────────────────────
        //  Step 5: Export bridge — integrate with CI/CD
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 5: Export bridge (CI/CD integration)\n");
        var report = resultB.ToEvaluationReport(agentName: "MemoryAgent", modelName: AIConfig.ModelDeployment);
        Console.WriteLine($"   EvaluationReport.OverallScore: {report.OverallScore:F1}%");
        Console.WriteLine($"   EvaluationReport.Grade:        {report.Metadata["Grade"]}");
        Console.WriteLine($"   EvaluationReport.Passed:       {report.PassedTests}/{report.TotalTests} categories");
        Console.WriteLine("   Ready for: JsonExporter, CsvExporter, JUnitXmlExporter, MarkdownExporter\n");

        // ──────────────────────────────────────────────────────────
        //  Step 6: Open the HTML report
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 6: Opening HTML Report...\n");
        var serverProcess = store.OpenReport("MemoryAgent");

        if (serverProcess != null)
        {
            Console.WriteLine("   Press Enter to stop the server and exit...");
            Console.ReadLine();
            serverProcess.Kill();
            serverProcess.Dispose();
        }

        PrintKeyTakeaways();
    }

    private static void PrintResult(string label, MemoryBenchmarkResult result)
    {
        Console.Write($"   {label}: ");
        Console.ForegroundColor = result.OverallScore >= 80 ? ConsoleColor.Green :
                                  result.OverallScore >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{result.OverallScore:F1}%");
        Console.ResetColor();
        Console.WriteLine($"  Grade: {result.Grade}  {new string('*', result.Stars)}{new string('o', 5 - result.Stars)}");

        foreach (var cat in result.CategoryResults.Where(c => !c.Skipped))
        {
            Console.WriteLine($"     {cat.CategoryName,-22} {cat.Score,6:F1}%");
        }
        Console.WriteLine();
    }

    private static void PrintComparison(BaselineComparison comparison)
    {
        if (comparison.Baselines.Count < 2)
        {
            Console.WriteLine("   Need at least 2 baselines to compare.\n");
            return;
        }

        var a = comparison.Baselines[0];
        var b = comparison.Baselines[1];

        Console.WriteLine("   +----------------+----------+----------+------------------+");
        Console.WriteLine("   | Dimension      | Config A | Config B | Winner           |");
        Console.WriteLine("   +----------------+----------+----------+------------------+");

        foreach (var dim in comparison.Dimensions)
        {
            var scoreA = dim.Scores.GetValueOrDefault(a.Id, 0);
            var scoreB = dim.Scores.GetValueOrDefault(b.Id, 0);
            var delta = scoreB - scoreA;
            var winner = delta > 2 ? "B wins" : delta < -2 ? "A wins" : "~tie";

            Console.Write($"   | {dim.DimensionName,-14} | {scoreA,7:F1}% | {scoreB,7:F1}% | ");
            Console.ForegroundColor = delta > 0 ? ConsoleColor.Green : delta < 0 ? ConsoleColor.Red : ConsoleColor.Gray;
            Console.Write($"{winner,10}");
            if (Math.Abs(delta) > 0.5) Console.Write($" ({delta:+0.0;-0.0}%)");
            Console.ResetColor();
            Console.WriteLine(" |");
        }

        Console.WriteLine("   +----------------+----------+----------+------------------+");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"   Best overall: {comparison.Baselines.First(bl => bl.Id == comparison.BestBaselineId).Name}");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine("   * MemoryBenchmarkRunner.Create(chatClient) — zero boilerplate");
        Console.WriteLine("   * .ToBaseline(name, config) snapshots scores + full agent metadata");
        Console.WriteLine("   * ConfigurationId routes: same config -> timeline, different -> radar");
        Console.WriteLine("   * .ToEvaluationReport() bridges to all 6 AgentEval exporters");
        Console.WriteLine("   * report.html shows pentagon overlay, score timeline, A/B comparison");
        Console.WriteLine("   * Run multiple times to see the timeline build up");
        Console.WriteLine("   * In production, use DI: services.AddAgentEvalMemory()");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("AgentEval Memory — Benchmark Reporting & Comparison");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Run benchmarks with different configs, save baselines, compare & report.");
        Console.WriteLine();
    }
}
