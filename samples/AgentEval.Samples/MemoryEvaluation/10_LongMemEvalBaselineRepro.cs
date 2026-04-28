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
/// Sample G10: LongMemEval GPT-4o Baseline Reproduction
///
/// Reproduces the GPT-4o long-context baseline from the LongMemEval paper (Fig 3b):
/// - TextBlob injection mode matching the paper's prompt format
/// - Direct reading method (no Chain-of-Note) — matches "direct" in paper
/// - GPT-4o as both reader and judge (matching official evaluation)
/// - S mode (~115K tokens, ~40 sessions per question)
/// - 100 questions (stratified) for practical run time with meaningful statistics
///
/// Paper reference scores (S mode, full-history long-context, 500 questions):
///   GPT-4o direct:  60.6%    (Fig 3b — "direct" = plain question, no intermediate reasoning)
///   GPT-4o + CoN:   64.0%    (Fig 3b — CoN = Chain-of-Note: extract notes first, then reason)
///   ChatGPT online: 57.7%    (Fig 3a, commercial memory system, 97q with 3-6 sessions, human eval)
///
/// "Chain-of-Note" (CoN) by Yu et al. 2023 instructs the LLM to first extract relevant
/// information from each memory item, then reason over those notes to answer. This
/// improves GPT-4o from 60.6% → 64.0%. Our TextBlob format uses the "direct" approach
/// (plain question without intermediate extraction), matching the 60.6% baseline.
///
/// Note: The published 57.7% is from ChatGPT's commercial memory system evaluated
/// interactively with human annotators on only 97 questions with 3-6 sessions —
/// NOT from reading the full S history. The comparable baseline for long-context
/// reading is 60.6% (direct) or 64.0% (CoN).
///
/// Prerequisites:
/// - Download longmemeval_s_cleaned.json from HuggingFace
/// - Place in: src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json
/// - AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY
/// - A "gpt-4o" deployment on your Azure OpenAI resource
///
/// Attribution: LongMemEval by Di Wu et al. (ICLR 2025)
/// https://github.com/xiaowu0162/LongMemEval
/// </summary>
public static class LongMemEvalBaselineRepro
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
        //  Step 1: Create runner with the SAME model as reader + judge
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 1: Creating LongMemEval runner (paper-matching config)...\n");

        // Hardcode gpt-4o to match the paper's exact conditions.
        // Change this if your Azure deployment name differs from "gpt-4o".
        const string modelDeployment = "gpt-4o";
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(modelDeployment).AsIChatClient();

        var runner = LongMemEvalBenchmarkRunner.Create(chatClient, DatasetPath);

        // Use TextBlob injection — history goes into the user message as a text blob,
        // matching the paper's "full-history-session" reading method.
        // This also ensures MAF AIContextProviders see the history content.
        var agent = chatClient.AsEvaluableAgent(
            name: $"LongMemEval-{modelDeployment}",
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        var config = new AgentBenchmarkConfig
        {
            AgentName = $"LongMemEval-{modelDeployment}",
            ModelId = modelDeployment,
            ReducerStrategy = "None",
            MemoryProvider = "TextBlob injection (paper-matching)"
        };

        Console.WriteLine($"   Model (reader): {modelDeployment}");
        Console.WriteLine($"   Model (judge):  {modelDeployment} (same — matches paper)");
        Console.WriteLine($"   Injection mode: TextBlob (paper's prompt format)");
        Console.WriteLine($"   Reading method: direct (plain question, no Chain-of-Note)");
        Console.WriteLine();

        // ──────────────────────────────────────────────────────────
        //  Step 2: Configure — TextBlob mode, 50q, stratified
        // ──────────────────────────────────────────────────────────

        var options = new ExternalBenchmarkOptions
        {
            MaxQuestions = 50,                 // 50q stratified; set null for full 500q reproduction
            StratifiedSampling = true,         // proportional across all 6 types
            PreserveSessionBoundaries = true,  // session markers in text blob
            IncludeTimestamps = true,          // temporal metadata preserved
            DatasetMode = "S",                 // ~115K tokens per question  
            RandomSeed = 42,                   // reproducible
            // HistoryInjectionMode defaults to TextBlob — matches paper format
        };

        Console.WriteLine("Step 2: Running LongMemEval (paper-matching config)...\n");
        Console.WriteLine($"   Questions:  {options.MaxQuestions} (stratified from 500)");
        Console.WriteLine($"   Mode:       {options.DatasetMode} (~115K tokens per question)");
        Console.WriteLine($"   Injection:  TextBlob (history in user message)");
        Console.WriteLine($"   Seed:       {options.RandomSeed}");
        Console.WriteLine();
        Console.WriteLine("   Paper conditions being matched:");
        Console.WriteLine("     - full-history-session (all history as context)");
        Console.WriteLine("     - direct reading method (plain question, no Chain-of-Note)");
        Console.WriteLine("     - GPT-4o judge with type-specific prompts");
        Console.WriteLine("     - binary scoring (0/1)");
        Console.WriteLine();

        var result = await runner.RunAsync(agent, config, options);

        // ──────────────────────────────────────────────────────────
        //  Step 3: Print results with paper comparison
        // ──────────────────────────────────────────────────────────

        Console.WriteLine($"\n   Completed in {result.Duration.TotalSeconds:F1}s ({result.TotalLlmCalls} LLM calls)\n");

        Console.WriteLine("Step 3: Results vs Paper Baselines\n");

        var correct = result.QuestionResults.Count(q => q.Correct);
        var total = result.QuestionResults.Count;

        Console.Write($"   Your result ({modelDeployment}): ");
        PrintScore(result.OverallAccuracy);
        Console.WriteLine($" ({correct}/{total} correct)");

        Console.Write($"   Task-averaged:               ");
        PrintScore(result.TaskAveragedAccuracy);
        Console.WriteLine(" (macro-average of per-type)\n");

        // Paper comparison table
        Console.WriteLine("   ┌─────────────────────────────────────┬──────────┐");
        Console.WriteLine("   │ Configuration                       │ Accuracy │");
        Console.WriteLine("   ├─────────────────────────────────────┼──────────┤");
        Console.Write    ("   │ ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"► YOUR RESULT ({modelDeployment,-15})     ");
        Console.ResetColor();
        Console.Write("│ ");
        PrintScore(result.OverallAccuracy);
        Console.Write(" ");
        Console.WriteLine("│");
        Console.WriteLine("   ├─────────────────────────────────────┼──────────┤");
        Console.WriteLine("   │ Paper: GPT-4o direct (S, 500q)     │  60.6%   │");
        Console.WriteLine("   │ Paper: GPT-4o + CoN  (S, 500q)     │  64.0%   │");
        Console.WriteLine("   │ Paper: ChatGPT online (97q, 3-6s)  │  57.7%   │");
        Console.WriteLine("   │ Paper: Llama-3.1-70B direct (S)    │  33.4%   │");
        Console.WriteLine("   │ Paper: Llama-3.1-8B  direct (S)    │  45.4%   │");
        Console.WriteLine("   └─────────────────────────────────────┴──────────┘");
        Console.WriteLine();

        // Per-type breakdown
        Console.WriteLine("   Per-Type Breakdown:");
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

        // ──────────────────────────────────────────────────────────
        //  Step 4: Save baseline with paper-repro tag
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("\nStep 4: Saving baseline...\n");

        var baseline = result.ToBaseline(
            $"LongMemEval-S {options.MaxQuestions}q TextBlob ({modelDeployment})",
            config,
            tags: ["longmemeval", "paper-repro", "textblob", "direct-reading", modelDeployment],
            pentagonMapperFull: LongMemEvalPentagonMapper.Consolidate);

        var store = new JsonFileBaselineStore();
        await store.SaveAsync(baseline);

        Console.WriteLine($"   Saved: {baseline.Name}");
        Console.WriteLine($"   Score: {baseline.OverallScore:F1}%  Grade: {baseline.Grade}  Stars: {new string('*', baseline.Stars)}{new string('o', 5 - baseline.Stars)}");
        Console.WriteLine($"   Pentagon: {string.Join(", ", baseline.DimensionScores.Select(kv => $"{kv.Key}={kv.Value:F0}%"))}");

        PrintKeyTakeaways(modelDeployment, result.OverallAccuracy);
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
        Console.WriteLine("AgentEval Memory — Sample G10: LongMemEval Baseline Reproduction");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Reproduces GPT-4o long-context baseline from LongMemEval paper.");
        Console.WriteLine("Uses TextBlob injection mode to match the paper's prompt format.");
        Console.WriteLine();
        Console.WriteLine("Paper: 'LongMemEval: Benchmarking Chat Assistants on Long-Term");
        Console.WriteLine("        Interactive Memory' — Di Wu et al., ICLR 2025");
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways(string model, double ourScore)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine($"   * TextBlob injection: history in user message (paper-matching)");
        Console.WriteLine($"   * Your {model}: {ourScore:F1}% vs paper GPT-4o direct: 60.6%");
        Console.WriteLine($"   * Paper's 57.7% = ChatGPT commercial system (NOT comparable)");
        Console.WriteLine($"   * The 60.6% (direct) and 64.0% (CoN) are the right baselines");
        Console.WriteLine($"   * TextBlob mode also ensures AIContextProviders see the history");
        Console.WriteLine($"   * Run with MaxQuestions=null for full 500q reproduction");
    }
}
