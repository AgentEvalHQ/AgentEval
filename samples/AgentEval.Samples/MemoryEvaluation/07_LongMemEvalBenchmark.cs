// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using AgentEval.Core;
using AgentEval.Memory.DataLoading;
using AgentEval.Memory.Engine;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentEval.Samples;

/// <summary>
/// LongMemEval Cross-Platform Benchmark (ICLR 2025, MIT License)
///
/// Runs real research-grade memory evaluation using the LongMemEval dataset.
/// Each question injects ~120K tokens of conversation history, then queries the agent.
/// Uses efficient history injection — only 2 LLM calls per question (query + judge).
///
/// Prerequisites:
/// - Download longmemeval_s_cleaned.json from:
///   https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main
/// - Place it in: src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json
/// - AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT
///
/// Attribution: LongMemEval by Di Wu et al. (ICLR 2025)
/// https://github.com/xiaowu0162/LongMemEval
/// </summary>
public static class LongMemEvalBenchmark
{
    // Path to the downloaded LongMemEval dataset
    private static readonly string DatasetPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "AgentEval.Memory", "Data", "longmemeval", "longmemeval_s_cleaned.json");

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        var resolvedPath = Path.GetFullPath(DatasetPath);
        if (!File.Exists(resolvedPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("   LongMemEval dataset not found!");
            Console.ResetColor();
            Console.WriteLine($"   Expected at: {resolvedPath}");
            Console.WriteLine();
            Console.WriteLine("   Download from:");
            Console.WriteLine("   https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main");
            Console.WriteLine("   Place longmemeval_s_cleaned.json in:");
            Console.WriteLine("   src/AgentEval.Memory/Data/longmemeval/");
            return;
        }

        // ──────────────────────────────────────────────────────────
        //  Step 1: Load a subset of LongMemEval questions
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 1: Loading LongMemEval dataset...\n");

        // Load just 10 questions to keep it manageable (500 total in dataset)
        var scenarios = LongMemEvalAdapter.LoadFromFile(resolvedPath, maxQuestions: 10);

        Console.WriteLine($"   Loaded {scenarios.Count} questions from LongMemEval");

        // Show question type distribution
        var typeCounts = scenarios
            .GroupBy(s => s.Metadata?["question_type"]?.ToString() ?? "unknown")
            .OrderBy(g => g.Key);

        foreach (var group in typeCounts)
        {
            var mapped = LongMemEvalAdapter.MapQuestionType(group.Key);
            Console.WriteLine($"   {group.Key,-30} → {mapped,-20} ({group.Count()} questions)");
        }

        // Show haystack sizes
        var haystackSizes = scenarios.Select(s =>
            LongMemEvalAdapter.ExtractHaystackFromSteps(s.Steps).Count).ToList();
        Console.WriteLine($"\n   Haystack sizes: min={haystackSizes.Min()}, max={haystackSizes.Max()}, avg={haystackSizes.Average():F0} turns");
        Console.WriteLine($"   Estimated tokens: ~{haystackSizes.Average() * 400:F0} per question\n");

        // ──────────────────────────────────────────────────────────
        //  Step 2: Create agent and judge
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 2: Creating agent with history injection support...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var agent = chatClient.AsEvaluableAgent(
            name: "LongMemEval-Agent",
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        var judge = new MemoryJudge(chatClient, NullLogger<MemoryJudge>.Instance);

        Console.WriteLine($"   Model: {AIConfig.ModelDeployment}");
        Console.WriteLine($"   Agent implements IHistoryInjectableAgent: {agent is IHistoryInjectableAgent}");
        Console.WriteLine("   Mode: Efficient (inject haystack as history, 2 LLM calls per question)\n");

        // ──────────────────────────────────────────────────────────
        //  Step 3: Run each question efficiently
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 3: Running LongMemEval questions...\n");

        var results = new List<(string QuestionId, string Type, string Question, string Answer, double Score)>();
        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (int i = 0; i < scenarios.Count; i++)
        {
            var scenario = scenarios[i];
            var questionId = scenario.Metadata?["question_id"]?.ToString() ?? $"q{i}";
            var questionType = scenario.Metadata?["question_type"]?.ToString() ?? "unknown";
            var haystackSize = LongMemEvalAdapter.ExtractHaystackFromSteps(scenario.Steps).Count;

            Console.Write($"   [{i + 1}/{scenarios.Count}] {questionType,-30} ({haystackSize} turns) ... ");

            var score = await LongMemEvalAdapter.RunEfficientAsync(agent, scenario, judge);

            if (score.HasValue)
            {
                var expectedAnswer = scenario.Queries[0].ExpectedFacts.FirstOrDefault()?.Content ?? "—";
                results.Add((questionId, questionType, scenario.Queries[0].Question, expectedAnswer, score.Value));

                Console.ForegroundColor = score.Value >= 80 ? ConsoleColor.Green :
                                          score.Value >= 50 ? ConsoleColor.Yellow : ConsoleColor.Red;
                Console.Write($"{score.Value:F0}%");
                Console.ResetColor();
                Console.WriteLine();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("SKIP (agent doesn't support history injection)");
                Console.ResetColor();
            }
        }

        totalStopwatch.Stop();

        // ──────────────────────────────────────────────────────────
        //  Step 4: Print results summary
        // ──────────────────────────────────────────────────────────

        Console.WriteLine($"\n   Completed in {totalStopwatch.Elapsed.TotalSeconds:F1}s\n");

        Console.WriteLine("Step 4: Results Summary\n");

        var overallAvg = results.Count > 0 ? results.Average(r => r.Score) : 0;
        Console.Write($"   Overall: ");
        Console.ForegroundColor = overallAvg >= 80 ? ConsoleColor.Green :
                                  overallAvg >= 50 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{overallAvg:F1}%");
        Console.ResetColor();
        Console.WriteLine($" ({results.Count} questions)\n");

        // Per-type breakdown
        var byType = results.GroupBy(r => r.Type).OrderBy(g => g.Key);
        Console.WriteLine("   +----------------------------------+--------+-------+");
        Console.WriteLine("   | Question Type                    | Count  | Avg   |");
        Console.WriteLine("   +----------------------------------+--------+-------+");
        foreach (var group in byType)
        {
            var avg = group.Average(r => r.Score);
            Console.Write($"   | {group.Key,-32} | {group.Count(),6} | ");
            Console.ForegroundColor = avg >= 80 ? ConsoleColor.Green :
                                      avg >= 50 ? ConsoleColor.Yellow : ConsoleColor.Red;
            Console.Write($"{avg,4:F0}%");
            Console.ResetColor();
            Console.WriteLine(" |");
        }
        Console.WriteLine("   +----------------------------------+--------+-------+\n");

        // ──────────────────────────────────────────────────────────
        //  Step 5: Save as baseline for reporting
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 5: Saving baseline...\n");

        // Convert to MemoryBenchmarkResult for reporting
        var categoryResults = byType.Select(g => new BenchmarkCategoryResult
        {
            CategoryName = g.Key,
            Score = g.Average(r => r.Score),
            Weight = 1.0 / byType.Count(),
            ScenarioType = LongMemEvalAdapter.MapQuestionType(g.Key),
            Duration = TimeSpan.FromSeconds(totalStopwatch.Elapsed.TotalSeconds / byType.Count())
        }).ToList();

        var benchmarkResult = new MemoryBenchmarkResult
        {
            BenchmarkName = "LongMemEval",
            CategoryResults = categoryResults,
            Duration = totalStopwatch.Elapsed
        };

        var config = new AgentBenchmarkConfig
        {
            AgentName = "LongMemEval-Agent",
            ModelId = AIConfig.ModelDeployment,
            ReducerStrategy = "None",
            MemoryProvider = "InMemoryChatHistory (history injection)"
        };

        var baseline = benchmarkResult.ToBaseline(
            $"LongMemEval {scenarios.Count}q",
            config,
            tags: ["longmemeval", "cross-platform"]);

        var store = new JsonFileBaselineStore();
        await store.SaveAsync(baseline);

        Console.WriteLine($"   Saved baseline: {baseline.Name}");
        Console.WriteLine($"   Overall: {benchmarkResult.OverallScore:F1}% Grade: {benchmarkResult.Grade}\n");

        // Open report
        Console.WriteLine("Step 6: Opening HTML Report...\n");
        var serverProcess = store.OpenReport("LongMemEval-Agent");

        if (serverProcess != null)
        {
            Console.WriteLine("   Press Enter to stop the server and exit...");
            Console.ReadLine();
            serverProcess.Kill();
            serverProcess.Dispose();
        }

        PrintKeyTakeaways();
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("AgentEval Memory — LongMemEval Cross-Platform Benchmark");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Research-grade memory evaluation using LongMemEval (ICLR 2025, MIT).");
        Console.WriteLine("Each question injects ~120K tokens of real conversation history.");
        Console.WriteLine("Only 2 LLM calls per question (query + judge) via history injection.");
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine("   * LongMemEval: 500 questions, 6 types, ~120K tokens per haystack");
        Console.WriteLine("   * History injection: 2 LLM calls/question instead of 300-600");
        Console.WriteLine("   * Research-grade: used in ICLR 2025 paper for LLM memory eval");
        Console.WriteLine("   * MIT licensed: free to use, modify, redistribute");
        Console.WriteLine("   * Cross-platform: compare your agent against published results");
        Console.WriteLine("   * GPT-4o reference: 57.7% in online interactive mode (LongMemEval paper)");
    }
}
