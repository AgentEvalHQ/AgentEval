// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks B8: LongMemEval — cross-platform memory evaluation
/// (ICLR 2025 paper, MIT-licensed dataset).
///
/// LongMemEval is a Shape B / runner-style benchmark (ADR-017 Convention 3).
/// This sample wires the preset-driven (Smoke / Standard / AuditGrade) running-
/// sample shape used by B2–B7 over the LongMemEval runner: real Azure OpenAI
/// agent (history-injectable) + LLM judge + canonical <c>.agenteval/</c> store
/// + sidecar JSON / HTML / PDF. The native <c>ExternalBenchmarkResult</c> is
/// also written to <c>report-native.json</c> for power users who need the
/// unaltered per-question / per-type shape.
///
/// Preset mapping:
/// <list type="bullet">
///   <item><see cref="SamplePreset.Smoke"/> → <c>Subset(chatClient)</c> with MaxQuestions=10 (embedded, ~3–5 min)</item>
///   <item><see cref="SamplePreset.Standard"/> → <c>Subset(chatClient)</c> full 30Q embedded (~10–15 min)</item>
///   <item><see cref="SamplePreset.AuditGrade"/> → <c>Full(chatClient)</c> ~500Q (requires <c>LONGMEMEVAL_DATASET_PATH</c>, ~30–60 min)</item>
/// </list>
/// </summary>
/// <remarks>
/// Requires Azure OpenAI credentials. Skips gracefully when missing.
/// AuditGrade additionally requires the <c>LONGMEMEVAL_DATASET_PATH</c>
/// environment variable to point at <c>longmemeval_s.json</c> (or equivalent);
/// when unset, the sample exits cleanly with a clear error rather than
/// throwing an unhandled exception.
/// </remarks>
public static class LongMemEvalBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks B8: LongMemEval — Memory benchmark (ICLR 2025)",
            "Subset (30Q embedded) vs Full (~500Q, requires LONGMEMEVAL_DATASET_PATH)");

        // ── Force-load AgentEval.Memory so [ModuleInitializer] registers "longmemeval".
        // The reference is FULLY QUALIFIED because the bare identifier
        // `LongMemEvalBenchmark` is shadowed by the Group-G sample class
        // `AgentEval.Samples.LongMemEvalBenchmark` — C# name resolution picks the
        // sample (parent-namespace) type and the real factory is never loaded.
        _ = typeof(AgentEval.Benchmarks.LongMemEvalBenchmark).Assembly;

        // ── Registry metadata header (pedagogical — keep it short).
        var family = BenchmarkFamilyRegistry.TryGet("longmemeval");
        if (family is null)
        {
            Console.WriteLine("   LongMemEval family is not registered. Did the umbrella package include AgentEval.Memory?");
            return;
        }

        Console.WriteLine($"   Family:      {family.Name}");
        Console.WriteLine($"   Description: {family.Description}");
        Console.WriteLine($"   Cost tier:   {family.DefaultCostTier}");
        if (!string.IsNullOrEmpty(family.DocLinkUrl))
            Console.WriteLine($"   Docs:        {family.DocLinkUrl}");
        Console.WriteLine($"   Presets:     {string.Join(", ", family.Presets.Select(p => p.Name))}");
        Console.WriteLine();

        if (!AIConfig.IsConfigured)
        {
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS B8 — LongMemEval");
            return;
        }

        var preset = BenchmarkSampleHelpers.ResolvePreset();
        BenchmarkSampleHelpers.PrintPreset(preset);

        // ── AuditGrade pre-flight: fail early with a friendly message when the
        // dataset env var is unset, rather than letting Full() throw a raw
        // InvalidOperationException up the menu.
        if (preset == SamplePreset.AuditGrade &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LONGMEMEVAL_DATASET_PATH")))
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("   AuditGrade preset requires LONGMEMEVAL_DATASET_PATH (the full ~500Q dataset");
            Console.WriteLine("   is NOT bundled). Download longmemeval_s.json from the official repo and set:");
            Console.WriteLine("     setx LONGMEMEVAL_DATASET_PATH \"C:\\path\\to\\longmemeval_s.json\"   (Windows)");
            Console.WriteLine("     export LONGMEMEVAL_DATASET_PATH=/path/to/longmemeval_s.json         (Unix)");
            Console.WriteLine("   For development / CI use --preset smoke (10Q embedded) or --preset standard (30Q embedded).");
            Console.ResetColor();
            return;
        }

        // ── Build the agent (history-injectable; same shape Group-G uses).
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var agent = chatClient.AsEvaluableAgent(
            name: "LongMemEvalSampleAgent",
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        var benchmarkConfig = new AgentBenchmarkConfig
        {
            AgentName = "LongMemEvalSampleAgent",
            ModelId = AIConfig.ModelDeployment,
            ReducerStrategy = "None",
            MemoryProvider = "InMemoryChatHistory (history injection)"
        };

        // ── Resolve runner + options for the chosen preset.
        // Embedded subset is 30 questions. Smoke truncates further (10) for fast CI.
        // Standard runs the full embedded 30 (uses the baked-in SubsetOptions).
        // AuditGrade switches to Full() which reads LONGMEMEVAL_DATASET_PATH.
        AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner runner;
        ExternalBenchmarkOptions options;
        string presetName;
        switch (preset)
        {
            case SamplePreset.AuditGrade:
                runner = AgentEval.Benchmarks.LongMemEvalBenchmark.Full(chatClient);
                options = AgentEval.Benchmarks.LongMemEvalBenchmark.FullOptions;
                presetName = "audit-grade";
                break;
            case SamplePreset.Standard:
                runner = AgentEval.Benchmarks.LongMemEvalBenchmark.Subset(chatClient);
                options = AgentEval.Benchmarks.LongMemEvalBenchmark.SubsetOptions;
                presetName = "standard";
                break;
            default: // Smoke
                runner = AgentEval.Benchmarks.LongMemEvalBenchmark.Subset(chatClient);
                options = new ExternalBenchmarkOptions
                {
                    MaxQuestions = 10,
                    StratifiedSampling = true,
                    RandomSeed = 42,
                    PreserveSessionBoundaries = true,
                    IncludeTimestamps = true,
                    DatasetMode = "Subset"
                };
                presetName = "smoke";
                break;
        }

        Console.WriteLine();
        Console.WriteLine($"   Model:               {AIConfig.ModelDeployment}");
        Console.WriteLine($"   History injection:   {(agent is IHistoryInjectableAgent ? "structured (IHistoryInjectableAgent)" : "text blob fallback")}");
        Console.WriteLine($"   Max questions:       {(options.MaxQuestions?.ToString() ?? "all in dataset")}");
        Console.WriteLine($"   Stratified sampling: {options.StratifiedSampling}");
        Console.WriteLine($"   Scoring:             binary (0/1) — official LongMemEval methodology");
        Console.WriteLine();
        Console.WriteLine($"   Running LongMemEval ({presetName})... this makes ~2 LLM calls per question.");
        Console.WriteLine();

        // ── Run the benchmark.
        ExternalBenchmarkResult result;
        try
        {
            result = await runner.RunAsync(agent, benchmarkConfig, options);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   LongMemEval run failed: {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
            return;
        }

        // ── Per-type breakdown (mirrors Group-G G8 output).
        Console.WriteLine($"   Completed in {result.Duration.TotalSeconds:F1}s ({result.TotalLlmCalls} LLM calls)");
        Console.WriteLine();
        Console.Write("   Overall accuracy:      ");
        PrintScore(result.OverallAccuracy);
        Console.WriteLine($"   ({result.QuestionResults.Count(q => q.Correct)}/{result.QuestionResults.Count} correct)");
        Console.Write("   Task-averaged accuracy: ");
        PrintScore(result.TaskAveragedAccuracy);
        Console.WriteLine("   (macro-average across question types)");
        Console.WriteLine();
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

        // ── Synthesise an EvalResult tree from the Shape-B result so we get
        // canonical .agenteval/ persistence + sidecar JSON / HTML / PDF for free.
        var evalTree = SynthesizeEvalResult(result, presetName);

        var subject = new SubjectIdentity(
            Kind: SubjectKind.Agent,
            Name: agent.Name,
            ModelId: AIConfig.ModelDeployment,
            Framework: "MAF");

        var paths = await BenchmarkSampleHelpers.WriteReportsViaStoreAsync(
            evalTree, subject,
            benchmarkName: "longmemeval",
            regulationOrBenchmark: $"LongMemEval — {presetName} preset (ICLR 2025)",
            includePdf: true,
            regulationCodeForEvidence: null, // not a compliance benchmark
            presetLabel: presetName,
            judgeModel: AIConfig.ModelDeployment);

        // ── Side-write the native ExternalBenchmarkResult alongside report.json
        // so power users keep the unaltered shape (per-question + per-type
        // breakdown the synthesised tree flattens slightly).
        try
        {
            var nativeJsonPath = Path.Combine(paths.SidecarDir, "report-native.json");
            await File.WriteAllTextAsync(
                nativeJsonPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"   Warning: failed to write report-native.json: {ex.GetType().Name}: {ex.Message}");
        }

        BenchmarkSampleHelpers.PrintReportPaths(evalTree, paths);
        BenchmarkSampleHelpers.OfferToOpenReports(paths);

        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - LongMemEval is a Shape B family — runner returns ExternalBenchmarkResult,");
        Console.WriteLine("     synthesised into an EvalResult tree (root + per-type composites + per-question leaves).");
        Console.WriteLine("   - report.json / report.html / report.pdf reflect the synthesised tree;");
        Console.WriteLine("     report-native.json carries the unaltered ExternalBenchmarkResult shape.");
        Console.WriteLine("   - Reference (paper, S mode): GPT-4o = 57.7%; small samples are high variance.");
        Console.WriteLine("   - Smoke = 10Q (embedded); Standard = 30Q (embedded); AuditGrade = full dataset (env var required).");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Synthesis: ExternalBenchmarkResult → EvalResult tree
    //  Root composite (overall accuracy) → per-question-type composites
    //  → per-question atomic leaves (0/1 score).
    // ──────────────────────────────────────────────────────────────────────

    private static EvalResult SynthesizeEvalResult(ExternalBenchmarkResult result, string presetName)
    {
        var now = DateTimeOffset.UtcNow;
        const double passThreshold = 0.5; // 50 % — generous; calibrate per-deployment

        // Build per-type composites.
        var perTypeNodes = new List<EvalResult>();
        foreach (var (typeName, typeResult) in result.PerTypeResults.OrderBy(kv => kv.Key))
        {
            var typeQuestions = result.QuestionResults.Where(q => q.QuestionType == typeName).ToList();

            // Per-question atomic leaves.
            var leaves = new List<EvalResult>(typeQuestions.Count);
            foreach (var q in typeQuestions)
            {
                var leafScoreValue = q.Correct ? 1.0 : 0.0;
                var leafLabel = q.Correct ? "pass" : "fail";
                var leafSeverity = q.Correct ? "none" : "medium";

                var evidence = new List<EvalEvidence>(3)
                {
                    new("question", q.QuestionId, q.Question),
                    new("gold-answer", q.QuestionId, q.GoldAnswer),
                    new("agent-response", q.QuestionId, q.AgentResponse),
                };
                if (!string.IsNullOrEmpty(q.JudgeExplanation))
                    evidence.Add(new EvalEvidence("judge", q.QuestionId, q.JudgeExplanation));

                leaves.Add(new EvalResult(
                    Metric: new EvalMetadata(
                        Key: $"longmemeval.{q.QuestionType}.{q.QuestionId}",
                        Name: $"{q.QuestionType} — {q.QuestionId}",
                        Category: "memory",
                        Version: "1.0.0"),
                    Score: new EvalScore(
                        Value: leafScoreValue,
                        Ordinal: null,
                        Label: leafLabel,
                        Passed: q.Correct,
                        Threshold: 1.0,
                        Severity: leafSeverity,
                        Confidence: null),
                    Details: new EvalDetails(
                        Dimensions: new Dictionary<string, double> { ["rawScore"] = q.RawScore },
                        Evidence: evidence,
                        Recommendations: null,
                        SubResults: null,
                        AggregationStrategy: null),
                    Provenance: new EvalProvenance(
                        Type: "external-benchmark",
                        JudgeModel: AIConfig.ModelDeployment,
                        PromptId: null,
                        PromptHash: null,
                        TokensUsed: null,
                        EstimatedCost: 0,
                        CacheHit: false),
                    EvaluatedAt: now));
            }

            // Per-type composite (accuracy in 0..1 range — TypeResult.Accuracy is 0..100).
            var typeAccuracy01 = typeResult.Accuracy / 100.0;
            var typePassed = typeAccuracy01 >= passThreshold;
            var typeLabel = typeAccuracy01 >= 0.7 ? "pass" : typeAccuracy01 >= passThreshold ? "warn" : "fail";
            var typeSeverity = typeAccuracy01 >= 0.7 ? "none" : typeAccuracy01 >= passThreshold ? "low" : "medium";

            perTypeNodes.Add(new EvalResult(
                Metric: new EvalMetadata(
                    Key: $"longmemeval.{typeName}",
                    Name: $"LongMemEval — {typeName}",
                    Category: "memory",
                    Version: "1.0.0"),
                Score: new EvalScore(
                    Value: typeAccuracy01,
                    Ordinal: null,
                    Label: typeLabel,
                    Passed: typePassed,
                    Threshold: passThreshold,
                    Severity: typeSeverity,
                    Confidence: null),
                Details: new EvalDetails(
                    Dimensions: new Dictionary<string, double>
                    {
                        ["correct"] = typeResult.CorrectQuestions,
                        ["total"] = typeResult.TotalQuestions,
                    },
                    Evidence: null,
                    Recommendations: null,
                    SubResults: leaves,
                    AggregationStrategy: "mean"),
                Provenance: new EvalProvenance(
                    Type: "composite",
                    JudgeModel: null,
                    PromptId: null,
                    PromptHash: null,
                    TokensUsed: null,
                    EstimatedCost: 0,
                    CacheHit: false),
                EvaluatedAt: now));
        }

        // Root composite (overall accuracy in 0..1).
        var overall01 = result.OverallAccuracy / 100.0;
        var overallPassed = overall01 >= passThreshold;
        var overallLabel = overall01 >= 0.7 ? "pass" : overall01 >= passThreshold ? "warn" : "fail";
        var overallSeverity = overall01 >= 0.7 ? "none" : overall01 >= passThreshold ? "low" : "medium";

        return new EvalResult(
            Metric: new EvalMetadata(
                Key: "longmemeval",
                Name: $"LongMemEval — {presetName} preset (ICLR 2025)",
                Category: "memory",
                Version: "1.0.0"),
            Score: new EvalScore(
                Value: overall01,
                Ordinal: null,
                Label: overallLabel,
                Passed: overallPassed,
                Threshold: passThreshold,
                Severity: overallSeverity,
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: new Dictionary<string, double>
                {
                    ["overallAccuracy"] = result.OverallAccuracy,
                    ["taskAveragedAccuracy"] = result.TaskAveragedAccuracy,
                    ["totalQuestions"] = result.QuestionResults.Count,
                    ["correctQuestions"] = result.QuestionResults.Count(q => q.Correct),
                    ["totalLlmCalls"] = result.TotalLlmCalls,
                    ["durationSeconds"] = result.Duration.TotalSeconds,
                },
                Evidence: null,
                Recommendations: new[]
                {
                    $"Reference: paper-reported GPT-4o = 57.7 % on LongMemEval-S (full ~500Q). " +
                    "Small samples are high variance — interpret accordingly."
                },
                SubResults: perTypeNodes,
                AggregationStrategy: "mean"),
            Provenance: new EvalProvenance(
                Type: "composite",
                JudgeModel: AIConfig.ModelDeployment,
                PromptId: null,
                PromptHash: null,
                TokensUsed: null,
                EstimatedCost: 0,
                CacheHit: false),
            EvaluatedAt: now);
    }

    private static void PrintScore(double scorePercent)
    {
        Console.ForegroundColor = scorePercent >= 70 ? ConsoleColor.Green :
                                  scorePercent >= 40 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{scorePercent,5:F1}%");
        Console.ResetColor();
    }
}
