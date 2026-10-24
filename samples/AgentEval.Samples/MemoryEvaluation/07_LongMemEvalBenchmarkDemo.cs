// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Core;
using AgentEval.Memory.External;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentEval.Memory.Reporting;
using AgentEval.Samples.Benchmarks;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>
/// Sample G8: LongMemEval Cross-Platform Benchmark Demo (ICLR 2025, MIT License)
///
/// Demonstrates research-grade memory evaluation using the official LongMemEval methodology:
/// - Stratified sampling across all 6 question types
/// - Type-specific judge prompts matching the official evaluation
/// - The paper's prompt format: every haystack session, with its date, in the user message (TextBlob)
/// - Binary scoring (0/1), the paper's metric
/// - 2 LLM calls per question (answer + judge)
/// - The answer prompts measured on their way to the provider, so the run shows the haystack reached the model
///
/// Preset (<c>--preset</c>, default Smoke): Smoke 10 questions, Standard 50, Audit-Grade all 500. S-mode haystacks
/// are about 115K tokens each, so every question sends that much.
///
/// <para>
/// Renamed from <c>LongMemEvalBenchmark</c> to <c>LongMemEvalBenchmarkDemo</c> in
/// v0.10.1 to eliminate the name shadow against the production factory
/// <c>AgentEval.Benchmarks.LongMemEvalBenchmark</c>. C# name resolution prefers
/// types reached by walking out the enclosing namespace over types pulled in via
/// <c>using</c>, so the bare identifier in Samples code silently resolved to this
/// demo class instead of the real factory. The <c>Demo</c> suffix conveys
/// "demonstration of how to use the benchmark", not the benchmark factory itself.
/// </para>
///
/// Prerequisites:
/// - Download longmemeval_s_cleaned.json from:
///   https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/tree/main
/// - Place it in: src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json
/// - AZURE_OPENAI_* | BITDEER_API_KEY | OPENAI_COMPATIBLE_*
///
/// Attribution: LongMemEval by Di Wu et al. (ICLR 2025)
/// https://github.com/xiaowu0162/LongMemEval
/// </summary>
public static class LongMemEvalBenchmarkDemo
{
    private const string AgentName = "LongMemEval-Agent";

    private static string DatasetPath => LongMemEvalSampleSupport.DatasetPath;

    public static async Task RunAsync()
    {
        PrintHeader();

        if (!AIConfig.IsConfigured)
        {
            AIConfig.PrintMissingCredentialsWarning();
            SampleOutcome.Skip("no chat provider configured");
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
            SampleOutcome.Skip($"the LongMemEval dataset is not at {DatasetPath}");
            return;
        }

        // ──────────────────────────────────────────────────────────
        //  Step 1: Create runner and agent
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 1: Creating LongMemEval runner and agent...\n");

        var model = AIConfig.ModelDeployment;

        // The answer model's client is metered: each prompt's size as sent, and as the provider counted it. The judge
        // gets its own client so only answer prompts are measured.
        var answerClient = new PromptSizeMeter(AIConfig.CreateChatClient(model));
        var runner = LongMemEvalBenchmarkRunner.Create(AIConfig.CreateChatClient(model), DatasetPath);

        var agent = answerClient.AsEvaluableAgent(
            name: AgentName,
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        var config = new AgentBenchmarkConfig
        {
            AgentName = AgentName,
            ModelId = model,
            ReducerStrategy = "None",
            // The options below leave HistoryInjectionMode at its default, TextBlob. This used to say
            // "InMemoryChatHistory (history injection)", which is not how the history reached the model.
            MemoryProvider = "TextBlob (every session in the user message)"
        };

        var preset = BenchmarkSampleHelpers.ResolvePreset();
        var options = new ExternalBenchmarkOptions
        {
            MaxQuestions = preset switch
            {
                SamplePreset.AuditGrade => null,   // all 500
                SamplePreset.Standard => 50,       // ~8 per type via stratified sampling
                _ => 10,
            },
            StratifiedSampling = true,       // proportional across all 6 types
            PreserveSessionBoundaries = true, // applies to structured injection; TextBlob always marks sessions
            IncludeTimestamps = true,        // temporal context preserved
            DatasetMode = "S",               // ~115K tokens per question
            RandomSeed = 42,                 // reproducible
            // The model judges its own answers, and on a reasoning model the budget pays for its thinking too.
            JudgeMaxOutputTokens = LongMemEvalSampleSupport.JudgeMaxOutputTokens,
        };

        Console.WriteLine($"   Model:     {model} ({AIConfig.ProviderName})");
        Console.WriteLine($"   Judge:     {model} — the model judges its own answers; the paper's judge was GPT-4o");
        Console.WriteLine($"   History:   {options.HistoryInjectionMode} — every session, with its date, in the user message (the paper's format)");
        Console.WriteLine("   Scoring:   Binary (0/1), the official LongMemEval metric\n");

        // ──────────────────────────────────────────────────────────
        //  Step 2: Configure and run benchmark
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("Step 2: Running LongMemEval benchmark...\n");
        Console.WriteLine($"   Preset:    {preset} — {(options.MaxQuestions?.ToString() ?? "all")} questions (stratified from 500, seed {options.RandomSeed})");
        Console.WriteLine($"   Mode:      {options.DatasetMode} (~115K tokens of history per question)");
        Console.WriteLine($"   Judge:     4 task-specific prompt templates covering 6 dataset labels; " +
                          $"{options.JudgeMaxOutputTokens} output tokens, thinking included\n");

        var result = await runner.RunAsync(agent, config, options);

        // ──────────────────────────────────────────────────────────
        //  Step 3: Print results
        // ──────────────────────────────────────────────────────────

        Console.WriteLine($"\n   Completed in {result.Duration.TotalSeconds:F1}s ({result.TotalLlmCalls} LLM calls)\n");

        Console.WriteLine("Step 3: Results Summary\n");

        Console.Write($"   Overall accuracy:      ");
        LongMemEvalSampleSupport.PrintScore(result.OverallAccuracy);
        Console.WriteLine($" ({result.CorrectQuestions}/{result.ScoredQuestions} scored correct)");
        LongMemEvalSampleSupport.PrintAccounting(result);

        Console.Write($"   Task-averaged accuracy: ");
        LongMemEvalSampleSupport.PrintScore(result.TaskAveragedAccuracy);
        Console.WriteLine(" (macro-average of per-type accuracies)\n");

        LongMemEvalSampleSupport.PrintPerTypeTable(result);

        // Did the haystack reach the model? Measured on the answer calls, not assumed from the dataset.
        var truncated = LongMemEvalSampleSupport.PrintHaystackDelivery(answerClient.Calls);

        // Reference scores: context, not a comparison.
        Console.WriteLine("\n   Published reference (LongMemEval Fig. 3b, full-history S, no Chain-of-Note):");
        Console.WriteLine("   https://arxiv.org/html/2410.10813v2#S3.F3");
        foreach (var (referenceModel, score) in LongMemEvalReferenceScores.OverallAccuracy)
            Console.WriteLine($"     {referenceModel,-25} {score:F1}%");
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine($"   Not like-for-like: the paper scored 500 questions with GPT-4o as the judge; this run scored {result.ScoredQuestions}");
        Console.WriteLine($"   with {model} judging its own answers. G10 reproduces the paper's GPT-4o setup.");
        Console.ResetColor();

        LongMemEvalSampleSupport.RecordVerdict(result, truncated);

        // ──────────────────────────────────────────────────────────
        //  Step 4: Save baseline
        // ──────────────────────────────────────────────────────────

        Console.WriteLine("\nStep 4: Saving baseline...\n");

        if (result.ScoredQuestions == 0)
        {
            Console.WriteLine("   Not saved: no question was scored, so there is no accuracy to keep.");
            return;
        }

        var baseline = result.ToBaseline(
            $"LongMemEval-S {result.SelectedQuestions}q (stratified)",
            config,
            description: $"{result.ScoredQuestions} of {result.SelectedQuestions} questions scored; judged by {model}",
            tags: ["longmemeval", "cross-platform", "binary-scoring", model, AIConfig.ProviderTag],
            pentagonMapperFull: LongMemEvalPentagonMapper.Consolidate);

        var store = new JsonFileBaselineStore();
        await store.SaveAsync(baseline);

        Console.WriteLine($"   Saved: {baseline.Name}");
        Console.WriteLine($"   Score: {baseline.OverallScore:F1}%  Grade: {baseline.Grade}  Stars: {new string('*', baseline.Stars)}{new string('o', 5 - baseline.Stars)}");
        Console.WriteLine($"   Pentagon dimensions: {string.Join(", ", baseline.DimensionScores.Select(kv => $"{kv.Key}={kv.Value:F0}%"))}");

        PrintKeyTakeaways();
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine("AgentEval Memory — Sample G8: LongMemEval Cross-Platform Benchmark");
        Console.WriteLine("===================================================================");
        Console.WriteLine("Research-grade memory evaluation using LongMemEval (ICLR 2025, MIT).");
        Console.WriteLine("Uses the new External Benchmark framework with:");
        Console.WriteLine("  - Stratified sampling across all 6 question types");
        Console.WriteLine("  - 4 task-specific judge prompt templates (official methodology)");
        Console.WriteLine("  - Session boundary + timestamp preservation");
        Console.WriteLine("  - Binary scoring, the paper's metric");
        Console.WriteLine();
    }

    private static void PrintKeyTakeaways()
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 70));
        Console.WriteLine("KEY TAKEAWAYS:");
        Console.WriteLine("   * LongMemEval: 500 questions, 6 types, ~115K tokens per haystack (S mode)");
        Console.WriteLine("   * TextBlob: the whole history in the user message, 2 LLM calls/question");
        Console.WriteLine("   * Haystack delivery is measured: prompt tokens as the provider counted them");
        Console.WriteLine("   * Stratified sampling: all 6 types tested proportionally");
        Console.WriteLine("   * Type-specific judges: temporal tolerance, preference flexibility");
        Console.WriteLine("   * Binary scoring; a published figure compares only under its judge and question set");
        Console.WriteLine("   * Every selected question is accounted for: scored, inconclusive or errored");
        Console.WriteLine("   * Use ExternalBaselineExtensions.ToBaseline() for reporting");
    }
}
