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
/// Sample G7: Memory Benchmark Reporting &amp; Multi-Model Comparison
///
/// This demonstrates:
/// - Running the Standard benchmark across 3 models (GPT-4o-mini, GPT-4o, GPT-4.1)
/// - Comparing model capabilities with the same system prompt
/// - Saving baselines with model-specific metadata
/// - Generating an interactive HTML report with overlaid pentagons
/// - Using the export bridge (.ToEvaluationReport()) for CI/CD integration
///
/// Requires: AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
/// Optional: AZURE_OPENAI_DEPLOYMENT_2 (default: gpt-4o-mini), AZURE_OPENAI_DEPLOYMENT_3 (default: gpt-4.1)
/// </summary>
public static class MemoryBenchmarkReporting
{
    private const string SystemPrompt = """
        You are a helpful assistant with excellent memory.
        IMPORTANT: Remember ALL facts the user tells you. When asked about something
        you were told, include the SPECIFIC details in your response.
        Always recall names, dates, preferences, and personal information accurately.
        Keep responses concise but include every relevant fact you remember.
        """;

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
        //  Step 1: Set up models and runner
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 1: Creating benchmark runner for 3 models...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var store = new JsonFileBaselineStore();

        // The 3 models to compare — weakest to strongest
        // Uses AIConfig deployment names (env vars or defaults: gpt-4o-mini, gpt-4o, gpt-4.1)
        var models = new[]
        {
            (Name: "GPT-4o-mini", Deployment: AIConfig.SecondaryModelDeployment),
            (Name: "GPT-4o",      Deployment: "gpt-4o"),
            (Name: "GPT-4.1",     Deployment: AIConfig.TertiaryModelDeployment),
        };

        Console.WriteLine($"   Endpoint:   {AIConfig.Endpoint}");
        foreach (var m in models)
            Console.WriteLine($"   Model: {m.Name,-12} → deployment: {m.Deployment}");
        Console.WriteLine();

        var allBaselines = new List<MemoryBaseline>();

        // ──────────────────────────────────────────────────────────
        //  Step 2-4: Run Standard benchmark for each model
        // ──────────────────────────────────────────────────────────

        for (int i = 0; i < models.Length; i++)
        {
            var model = models[i];
            Console.WriteLine($"Step {i + 2}: Running Standard benchmark — {model.Name}...\n");

            var chatClient = azureClient.GetChatClient(model.Deployment).AsIChatClient();
            var runner = MemoryBenchmarkRunner.Create(chatClient);

            var agent = chatClient.AsEvaluableAgent(
                name: "MemoryAgent",
                systemPrompt: SystemPrompt,
                includeHistory: true);

            var progress = new Progress<BenchmarkProgress>(p =>
            {
                var status = p.Skipped ? "SKIP" : $"{p.Score:F1}%";
                var elapsed = $"{(int)p.Elapsed.TotalMinutes}m {p.Elapsed.Seconds:D2}s";
                var remaining = $"~{(int)p.EstimatedRemaining.TotalMinutes}m {p.EstimatedRemaining.Seconds:D2}s";
                Console.Write($"\r   [{p.CompletedCategories}/{p.TotalCategories}] {p.CategoryName}: {status} | {elapsed} elapsed | {remaining} remaining   ");
            });

            var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard, progress);
            Console.WriteLine(); // Clear progress line
            PrintResult(model.Name, result);

            var config = new AgentBenchmarkConfig
            {
                AgentName = "MemoryAgent",
                ModelId = model.Deployment,
                ReducerStrategy = "None (memory-optimized prompt)",
                MemoryProvider = "InMemoryChatHistory"
            };

            var baseline = result.ToBaseline(model.Name, config, tags: [model.Deployment]);
            await store.SaveAsync(baseline);
            allBaselines.Add(baseline);

            Console.WriteLine($"   Saved. ConfigurationId: {baseline.ConfigurationId}\n");
        }

        // ──────────────────────────────────────────────────────────
        //  Step 5: Compare all 3 models
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 5: Comparing models...\n");

        var comparer = new BaselineComparer();
        var comparison = comparer.Compare(allBaselines);
        PrintComparison(comparison, allBaselines);

        // ──────────────────────────────────────────────────────────
        //  Step 6: Export bridge — integrate with CI/CD
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 6: Export bridge (CI/CD integration)\n");
        var bestBaseline = allBaselines.OrderByDescending(b => b.OverallScore).First();
        Console.WriteLine($"   Best model: {bestBaseline.Name} at {bestBaseline.OverallScore:F1}%");
        Console.WriteLine($"   Grade: {bestBaseline.Grade}  Stars: {new string('*', bestBaseline.Stars)}{new string('o', 5 - bestBaseline.Stars)}");
        Console.WriteLine("   Ready for: JsonExporter, CsvExporter, JUnitXmlExporter, MarkdownExporter\n");

        // ──────────────────────────────────────────────────────────
        //  Step 7: Open the HTML report
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 7: Opening HTML Report...\n");
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
            Console.ForegroundColor = cat.Score >= 80 ? ConsoleColor.Green :
                                      cat.Score >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
            Console.Write($"     {cat.CategoryName,-24} {cat.Score,6:F1}%");
            Console.ResetColor();
            Console.WriteLine();
        }
        Console.WriteLine();
    }

    private static void PrintComparison(BaselineComparison comparison, List<MemoryBaseline> baselines)
    {
        if (comparison.Baselines.Count < 2)
        {
            Console.WriteLine("   Need at least 2 baselines to compare.\n");
            return;
        }

        // Header
        Console.Write("   | Dimension      ");
        foreach (var bl in comparison.Baselines)
            Console.Write($"| {bl.Name,-12} ");
        Console.WriteLine("| Winner           |");

        Console.Write("   |----------------");
        foreach (var _ in comparison.Baselines)
            Console.Write("|--------------");
        Console.WriteLine("|------------------|");

        foreach (var dim in comparison.Dimensions)
        {
            Console.Write($"   | {dim.DimensionName,-14} ");
            double bestScore = 0;
            string bestName = "";

            foreach (var bl in comparison.Baselines)
            {
                var score = dim.Scores.GetValueOrDefault(bl.Id, 0);
                if (score > bestScore) { bestScore = score; bestName = bl.Name; }
                Console.Write($"| {score,10:F1}%  ");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"| {bestName,-16} ");
            Console.ResetColor();
            Console.WriteLine("|");
        }

        Console.Write("   |----------------");
        foreach (var _ in comparison.Baselines)
            Console.Write("|--------------");
        Console.WriteLine("|------------------|");

        var best = comparison.Baselines.First(bl => bl.Id == comparison.BestBaselineId);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n   Best overall: {best.Name}");
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine("   * Multi-model comparison: same prompt, 3 models, side-by-side");
        Console.WriteLine("   * GPT-4o-mini is significantly weaker at memory recall under context pressure");
        Console.WriteLine("   * GPT-4.1 excels but differences emerge in temporal + conflict resolution");
        Console.WriteLine("   * MemoryBenchmarkRunner.Create(chatClient) — zero boilerplate");
        Console.WriteLine("   * .ToBaseline(name, config) snapshots scores + full agent metadata");
        Console.WriteLine("   * ConfigurationId routes: same config -> timeline, different -> radar");
        Console.WriteLine("   * .ToEvaluationReport() bridges to all 6 AgentEval exporters");
        Console.WriteLine("   * Standard = 8 categories: Retention, Temporal, Noise, Depth, Updates,");
        Console.WriteLine("     MultiTopic, Abstention, Preference");
        Console.WriteLine("   * Report pentagon overlays all 3 models for instant visual comparison");
        Console.WriteLine("   * In production, use DI: services.AddAgentEvalMemory()");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("AgentEval Memory — Sample G7: Multi-Model Benchmark Comparison");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Run Standard benchmark across 3 models, compare & report.");
        Console.WriteLine();
    }
}
