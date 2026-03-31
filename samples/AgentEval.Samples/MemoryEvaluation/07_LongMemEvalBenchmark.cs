// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using Azure.AI.OpenAI;
using AgentEval.Core;
using AgentEval.Memory.External;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// LongMemEval Cross-Platform Benchmark (ICLR 2025, MIT License)
///
/// Runs research-grade memory evaluation using the official LongMemEval methodology:
/// - Stratified sampling across all 6 question types
/// - Type-specific judge prompts matching the official evaluation
/// - Session boundary + timestamp preservation in history injection
/// - Binary scoring (0/1) comparable to published results (GPT-4o = 57.7%)
/// - 2 LLM calls per question (query + judge) via history injection
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
    private static readonly string DatasetPath = Path.GetFullPath(Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "AgentEval.Memory", "Data", "longmemeval", "longmemeval_s_cleaned.json"));

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            return;
        }

        if (!File.Exists(DatasetPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("   LongMemEval dataset not found!");
            Console.ResetColor();
            Console.WriteLine($"   Expected at: {DatasetPath}");
            Console.WriteLine();
            Console.WriteLine("   Download from:");
            Console.WriteLine("   https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main");
            Console.WriteLine("   Place longmemeval_s_cleaned.json in:");
            Console.WriteLine("   src/AgentEval.Memory/Data/longmemeval/");
            return;
        }

        // ──────────────────────────────────────────────────────────
        //  Step 1: Create runner and agent
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 1: Creating LongMemEval runner and agent...\n");

        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var runner = LongMemEvalBenchmarkRunner.Create(chatClient, DatasetPath);

        var agent = chatClient.AsEvaluableAgent(
            name: "LongMemEval-Agent",
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        var config = new AgentBenchmarkConfig
        {
            AgentName = "LongMemEval-Agent",
            ModelId = AIConfig.ModelDeployment,
            ReducerStrategy = "None",
            MemoryProvider = "InMemoryChatHistory (history injection)"
        };

        Console.WriteLine($"   Model: {AIConfig.ModelDeployment}");
        Console.WriteLine($"   History injection: {agent is IHistoryInjectableAgent}");
        Console.WriteLine("   Scoring: Binary (0/1) matching official LongMemEval methodology\n");

        // ──────────────────────────────────────────────────────────
        //  Step 2: Configure and run benchmark
        // ──────────────────────────────────────────────────────────

        var options = new ExternalBenchmarkOptions
        {
            MaxQuestions = 50,               // ~8 per type via stratified sampling
            StratifiedSampling = true,       // proportional across all 6 types
            PreserveSessionBoundaries = true, // session markers in history
            IncludeTimestamps = true,        // temporal context preserved
            DatasetMode = "S",               // ~115K tokens per question
            RandomSeed = 42                  // reproducible
        };

        Console.WriteLine("Step 2: Running LongMemEval benchmark...\n");
        Console.WriteLine($"   Questions: {options.MaxQuestions} (stratified from 500)");
        Console.WriteLine($"   Mode: {options.DatasetMode} (~115K tokens per question)");
        Console.WriteLine($"   Session boundaries: preserved  |  Timestamps: included");
        Console.WriteLine($"   Judge: 5 type-specific prompts matching official eval\n");

        var result = await runner.RunAsync(agent, config, options);

        // ──────────────────────────────────────────────────────────
        //  Step 3: Print results
        // ──────────────────────────────────────────────────────────

        Console.WriteLine($"\n   Completed in {result.Duration.TotalSeconds:F1}s ({result.TotalLlmCalls} LLM calls)\n");

        Console.WriteLine("Step 3: Results Summary\n");

        Console.Write($"   Overall accuracy:      ");
        PrintScore(result.OverallAccuracy);
        Console.WriteLine($" ({result.QuestionResults.Count(q => q.Correct)}/{result.QuestionResults.Count} correct)\n");

        Console.Write($"   Task-averaged accuracy: ");
        PrintScore(result.TaskAveragedAccuracy);
        Console.WriteLine(" (macro-average of per-type accuracies)\n");

        // Per-type breakdown
        Console.WriteLine("   +----------------------------------+--------+----------+");
        Console.WriteLine("   | Question Type                    | Count  | Accuracy |");
        Console.WriteLine("   +----------------------------------+--------+----------+");
        foreach (var (typeName, typeResult) in result.PerTypeResults.OrderBy(kv => kv.Key))
        {
            Console.Write($"   | {typeName,-32} | {typeResult.TotalQuestions,6} | ");
            PrintScore(typeResult.Accuracy);
            Console.WriteLine(" |");
        }
        Console.WriteLine("   +----------------------------------+--------+----------+");

        // Reference scores
        Console.WriteLine("\n   Reference (LongMemEval paper, S mode):");
        foreach (var (model, score) in LongMemEvalReferenceScores.OverallAccuracy)
            Console.WriteLine($"     {model,-25} {score:F1}%");

        // ──────────────────────────────────────────────────────────
        //  Step 4: Save baseline
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("\nStep 4: Saving baseline...\n");

        var baseline = result.ToBaseline(
            $"LongMemEval-S {options.MaxQuestions}q (stratified)",
            config,
            tags: ["longmemeval", "cross-platform", "binary-scoring"],
            pentagonMapperFull: LongMemEvalPentagonMapper.Consolidate);

        var store = new JsonFileBaselineStore();
        await store.SaveAsync(baseline);

        Console.WriteLine($"   Saved: {baseline.Name}");
        Console.WriteLine($"   Score: {baseline.OverallScore:F1}%  Grade: {baseline.Grade}  Stars: {new string('*', baseline.Stars)}{new string('o', 5 - baseline.Stars)}");
        Console.WriteLine($"   Pentagon dimensions: {string.Join(", ", baseline.DimensionScores.Select(kv => $"{kv.Key}={kv.Value:F0}%"))}");

        PrintKeyTakeaways();
    }

    private static void PrintScore(double score)
    {
        Console.ForegroundColor = score >= 70 ? ConsoleColor.Green :
                                  score >= 40 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{score,5:F1}%");
        Console.ResetColor();
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("AgentEval Memory — LongMemEval Cross-Platform Benchmark");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Research-grade memory evaluation using LongMemEval (ICLR 2025, MIT).");
        Console.WriteLine("Uses the new External Benchmark framework with:");
        Console.WriteLine("  - Stratified sampling across all 6 question types");
        Console.WriteLine("  - 5 type-specific judge prompts (official methodology)");
        Console.WriteLine("  - Session boundary + timestamp preservation");
        Console.WriteLine("  - Binary scoring comparable to published results");
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine("   * LongMemEval: 500 questions, 6 types, ~120K tokens per haystack");
        Console.WriteLine("   * History injection: 2 LLM calls/question (0 for context setup)");
        Console.WriteLine("   * Stratified sampling: all 6 types tested proportionally");
        Console.WriteLine("   * Type-specific judges: temporal tolerance, preference flexibility");
        Console.WriteLine("   * Binary scoring: comparable to GPT-4o = 57.7% (published)");
        Console.WriteLine("   * Session boundaries + timestamps preserved in context");
        Console.WriteLine("   * Use ExternalBaselineExtensions.ToBaseline() for reporting");
    }
}
