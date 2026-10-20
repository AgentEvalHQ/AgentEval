// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Extensions;
using AgentEval.Benchmarks;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using AgentEval.Providers;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample G7: Memory Benchmark Reporting &amp; Multi-Model Comparison
///
/// This demonstrates:
/// - Running one memory benchmark preset across the models you configured (Quick by default)
/// - Comparing model capabilities with the same system prompt and the same judge
/// - Saving baselines named after the model that actually ran
/// - Generating an interactive HTML report with overlaid pentagons
/// - Using the export bridge (.ToEvaluationReport()) for CI/CD integration
///
/// Requires: a model provider — AZURE_OPENAI_* or BITDEER_API_KEY or OPENAI_COMPATIBLE_* (see AIConfig) — and a second
/// model to compare: the provider's <c>*_MODEL_2</c> (and optionally <c>*_MODEL_3</c>), or
/// <c>AZURE_OPENAI_DEPLOYMENT_2</c> / <c>_3</c>. Only models the environment names run; a provider's default second
/// model is not one of them. Preset: <c>--preset quick|standard|full</c> (or the H-group tier names), default Quick.
/// </summary>
/// <remarks>
/// Until 2026-10 this sample labelled its three runs "GPT-4o-mini", "GPT-4o" and "GPT-4.1" whatever ran, sent the literal
/// <c>gpt-4o</c> as the second model, and always ran Standard: on Bitdeer it saved GLM-5.3-Flash's scores under GPT names.
/// </remarks>
public static class MemoryBenchmarkReporting
{
    private const string AgentName = "MemoryAgent";

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
            Console.WriteLine("   This sample requires a model provider (Azure OpenAI, Bitdeer, or any OpenAI-compatible endpoint).");
            Console.WriteLine("   Memory benchmarks use an LLM judge.\n");
            SampleOutcome.Skip("no chat provider configured");
            return;
        }

        // ──────────────────────────────────────────────────────────
        //  Step 1: The models to compare, the preset, one judge
        // ──────────────────────────────────────────────────────────

        // Only the models the environment names. The provider's fallback second and third models are defaults nobody
        // configured (the primary again, or on Azure deployments that may not exist), and a baseline must carry the
        // name of the model that produced it.
        var models = InferenceProviderEnvironment.NamedModels(AIConfig.Settings, Environment.GetEnvironmentVariable);
        if (!ComparisonModels.Require("G7 Benchmark Reporting", models))
            return;

        var benchmark = MemoryBenchmarkSampleSupport.ResolveBenchmark();
        if (benchmark is null)
        {
            Console.WriteLine("\n   Cancelled: nothing ran.\n");
            SampleOutcome.Skip("cancelled at the preset menu: no model ran");
            return;
        }

        // One judge for every model: the comparison then varies the model under test and nothing else. Each model
        // judging its own answers would also vary the judge.
        var judgeModel = models[0];

        Console.WriteLine($"Step 1: Creating benchmark runner for {models.Count} models...\n");
        Console.WriteLine($"   Provider:   {AIConfig.ProviderName} ({AIConfig.Endpoint})");
        Console.WriteLine($"   Preset:     {benchmark.Name} ({benchmark.Categories.Count} categories)" +
                          (benchmark.Name == MemoryBenchmark.Quick.Name ? "  — --preset standard or full for more" : ""));
        Console.WriteLine($"   Judge:      {judgeModel} (the same judge scores every model)");
        foreach (var model in models)
            Console.WriteLine($"   Model:      {model}");
        Console.WriteLine();

        var store = new JsonFileBaselineStore();
        var runner = MemoryBenchmarkRunner.Create(AIConfig.CreateChatClient(judgeModel));
        var allBaselines = new List<MemoryBaseline>();
        var incomplete = new List<string>();

        // ──────────────────────────────────────────────────────────
        //  Step 2…: Run the preset for each model
        // ──────────────────────────────────────────────────────────

        for (int i = 0; i < models.Count; i++)
        {
            var model = models[i];
            Console.WriteLine($"Step {i + 2}: Running {benchmark.Name} benchmark — {model}...\n");

            var agent = AIConfig.CreateChatClient(model).AsEvaluableAgent(
                name: AgentName,
                systemPrompt: SystemPrompt,
                includeHistory: true);

            var progress = new Progress<BenchmarkProgress>(p =>
            {
                var status = p.Skipped ? "SKIP" : $"{p.Score:F1}%";
                var elapsed = $"{(int)p.Elapsed.TotalMinutes}m {p.Elapsed.Seconds:D2}s";
                var remaining = $"~{(int)p.EstimatedRemaining.TotalMinutes}m {p.EstimatedRemaining.Seconds:D2}s";
                Console.Write($"\r   [{p.CompletedCategories}/{p.TotalCategories}] {p.CategoryName}: {status} | {elapsed} elapsed | {remaining} remaining   ");
            });

            var result = await runner.RunBenchmarkAsync(agent, benchmark, progress);
            Console.WriteLine(); // Clear progress line
            PrintResult(model, result);
            MemoryBenchmarkSampleSupport.RecordVerdict(model, result);
            if (!result.IsComplete || result.SkippedCategories.Count > 0)
                incomplete.Add(model);

            var config = new AgentBenchmarkConfig
            {
                AgentName = AgentName,
                ModelId = model,
                ReducerStrategy = "None (memory-optimized prompt)",
                MemoryProvider = "InMemoryChatHistory"
            };

            var baseline = result.ToBaseline(
                model, config,
                description: $"{benchmark.Name} preset on {AIConfig.ProviderName}, judged by {judgeModel}",
                tags: [model, AIConfig.ProviderTag, benchmark.Name.ToLowerInvariant()]);
            await store.SaveAsync(baseline);
            allBaselines.Add(baseline);

            Console.WriteLine($"   Saved. ConfigurationId: {baseline.ConfigurationId}\n");
        }

        // ──────────────────────────────────────────────────────────
        //  Compare the models
        // ──────────────────────────────────────────────────────────

        var step = models.Count + 2;
        Console.WriteLine($"Step {step}: Comparing models...\n");

        var comparer = new BaselineComparer();
        var comparison = comparer.Compare(allBaselines);
        PrintComparison(comparison, incomplete);

        // ──────────────────────────────────────────────────────────
        //  Export bridge — integrate with CI/CD
        // ──────────────────────────────────────────────────────────

        Console.WriteLine($"Step {step + 1}: Export bridge (CI/CD integration)\n");
        var leaders = MemoryComparisonLeaders.Overall(allBaselines);
        var bestBaseline = leaders[0];
        Console.WriteLine($"   Best model: {string.Join(" = ", leaders.Select(b => b.Name))} at {bestBaseline.OverallScore:F1}%" +
                          (leaders.Count > 1 ? " (a tie)" : ""));
        Console.WriteLine($"   Grade: {bestBaseline.Grade}  Stars: {new string('*', bestBaseline.Stars)}{new string('o', 5 - bestBaseline.Stars)}");
        Console.WriteLine("   Ready for: JsonExporter, CsvExporter, JUnitXmlExporter, MarkdownExporter\n");

        // ──────────────────────────────────────────────────────────
        //  The HTML report
        // ──────────────────────────────────────────────────────────

        Console.WriteLine($"Step {step + 2}: HTML Report...\n");
        MemoryBenchmarkSampleSupport.ServeOrPrintReport(store, AgentName);

        PrintKeyTakeaways(models.Count);
    }

    private static void PrintResult(string label, MemoryBenchmarkResult result)
    {
        Console.Write($"   {label}: ");
        Console.ForegroundColor = result.OverallScore >= 80 ? ConsoleColor.Green :
                                  result.OverallScore >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{result.OverallScore:F1}%");
        Console.ResetColor();
        Console.WriteLine($"  Grade: {result.Grade}  {new string('*', result.Stars)}{new string('o', 5 - result.Stars)}");

        MemoryBenchmarkSampleSupport.PrintCategories(result);
        Console.WriteLine();
    }

    private static void PrintComparison(MemoryBaselineComparison comparison, IReadOnlyList<string> incomplete)
    {
        // Header
        Console.Write("   | Dimension      ");
        foreach (var bl in comparison.Baselines)
            Console.Write($"| {Fit(bl.Name, 12),-12} ");
        Console.WriteLine("| Winner           |");

        Console.Write("   |----------------");
        foreach (var _ in comparison.Baselines)
            Console.Write("|--------------");
        Console.WriteLine("|------------------|");

        foreach (var dim in comparison.Dimensions)
        {
            Console.Write($"   | {dim.DimensionName,-14} ");
            foreach (var bl in comparison.Baselines)
            {
                // Read the baseline's own scores: the comparer fills a dimension a baseline did not measure with 0,
                // and a dimension that was not measured is not a 0.
                Console.Write(bl.DimensionScores.TryGetValue(dim.DimensionName, out var score)
                    ? $"| {score,10:F1}%  "
                    : $"| {MemoryComparisonLeaders.NotMeasured,11}  ");
            }

            // A tie has no winner: the first model listed used to take it.
            var winner = MemoryComparisonLeaders.Winner(MemoryComparisonLeaders.OnDimension(comparison.Baselines, dim.DimensionName));
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"| {Fit(winner, 16),-16} ");
            Console.ResetColor();
            Console.WriteLine("|");
        }

        Console.Write("   |----------------");
        foreach (var _ in comparison.Baselines)
            Console.Write("|--------------");
        Console.WriteLine("|------------------|");

        var leaders = MemoryComparisonLeaders.Overall(comparison.Baselines);
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(leaders.Count == 1
            ? $"\n   Best overall: {leaders[0].Name}"
            : $"\n   Best overall: a tie — {string.Join(", ", leaders.Select(b => b.Name))} at {leaders[0].OverallScore:F1}%");
        Console.ResetColor();
        if (MemoryComparisonLeaders.NoneSeparated(comparison))
            Console.WriteLine("   This preset does not separate these models; a larger one (--preset standard or full) may.");
        if (incomplete.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"   Not a like-for-like comparison: {string.Join(", ", incomplete)} did not measure every category or question.");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    private static string Fit(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";

    private static void PrintKeyTakeaways(int modelCount)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine($"   * Multi-model comparison: same prompt, same judge, {modelCount} models, side-by-side");
        Console.WriteLine("   * Which model led each category is in the comparison table above: measured in");
        Console.WriteLine("     this run, on this benchmark, not assumed from the model names");
        Console.WriteLine("   * MemoryBenchmarkRunner.Create(judgeClient) — zero boilerplate");
        Console.WriteLine("   * .ToBaseline(name, config) snapshots scores + full agent metadata");
        Console.WriteLine("   * ConfigurationId routes: same config -> timeline, different -> radar");
        Console.WriteLine("   * .ToEvaluationReport() bridges to all 6 AgentEval exporters");
        Console.WriteLine("   * Quick = 3 categories; Standard = 8 (Retention, Temporal, Noise, Depth, Updates,");
        Console.WriteLine("     MultiTopic, Abstention, Preference); Full = 12");
        Console.WriteLine("   * Report pentagon overlays every model for instant visual comparison");
        Console.WriteLine("   * In production, use DI: services.AddAgentEvalMemory()");
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("AgentEval Memory — Sample G7: Multi-Model Benchmark Comparison");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Run one benchmark preset across your configured models, compare & report.");
        Console.WriteLine();
    }
}
