// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentEval.Benchmarks;                       // → production factory LongMemEvalBenchmark (the shadow against the Group-G demo was removed in v0.10.1)
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks H9: LongMemEval — cross-platform memory evaluation
/// (ICLR 2025 paper, MIT-licensed dataset).
///
/// LongMemEval is a Shape B / runner-style benchmark (ADR-017 Convention 3).
/// This sample wires the preset-driven (Smoke / Standard / AuditGrade) running-
/// sample shape used by H2–H8 over the LongMemEval runner: real Azure OpenAI
/// agent (history-injectable) + LLM judge + canonical <c>.agenteval/</c> store
/// + sidecar JSON / HTML / PDF. The native <c>ExternalBenchmarkResult</c> is
/// also written to <c>report-native.json</c> for power users who need the
/// unaltered per-question / per-type shape.
///
/// v0.10.1-beta: all presets read the REAL <c>longmemeval_s_cleaned.json</c>
/// dataset on disk — the previously-bundled "embedded subset" was a hand-
/// authored approximation and produced misleading scores. No fake fallback
/// data exists in this version. When the dataset cannot be located the sample
/// catches <see cref="LongMemEvalDatasetNotFoundException"/> and prints a
/// friendly download-instructions box, then returns cleanly to the menu.
///
/// Preset mapping (all against the real ~500-question dataset):
/// <list type="bullet">
///   <item><see cref="SamplePreset.Smoke"/> → <c>Subset(chatClient)</c> with override MaxQuestions=10 (~5–10 min)</item>
///   <item><see cref="SamplePreset.Standard"/> → <c>Subset(chatClient)</c> with baked SubsetOptions (default 50 questions)</item>
///   <item><see cref="SamplePreset.AuditGrade"/> → <c>Full(chatClient)</c> all ~500 questions (requires <c>LONGMEMEVAL_DATASET_PATH</c>, ~30–60 min)</item>
/// </list>
/// </summary>
/// <remarks>
/// Requires Azure OpenAI credentials. Skips gracefully when missing.
/// Smoke + Standard work without any env var when the canonical dataset file
/// is present at <c>&lt;workspace-root&gt;/src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json</c>.
/// AuditGrade additionally requires the <c>LONGMEMEVAL_DATASET_PATH</c>
/// environment variable (deliberate audit-grade gate); when unset, the sample
/// exits cleanly with a clear message rather than throwing.
/// </remarks>
public static class LongMemEvalBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks H9: LongMemEval — Memory benchmark (ICLR 2025)",
            "Smoke=10Q / Standard=50Q / AuditGrade=~500Q — all real dataset");

        // ── Force-load AgentEval.Memory so [ModuleInitializer] registers "longmemeval".
        // The FQN is used consistently across all force-load anchors here (and in
        // 01_RegistryDiscovery) so a future name shadow elsewhere in the Samples
        // assembly can't silently break the load. (The original Group-G class shadow
        // that motivated this was removed in v0.10.1 — the demo class is now
        // `LongMemEvalBenchmarkDemo`.)
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
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS H9 — LongMemEval");
            return;
        }

        var preset = BenchmarkSampleHelpers.ResolvePreset();
        BenchmarkSampleHelpers.PrintPreset(preset);

        // ── AuditGrade pre-flight: fail early with a friendly message when the
        // dataset env var is unset, rather than letting Full() throw a raw
        // InvalidOperationException up the menu.
        if (preset == SamplePreset.AuditGrade &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LongMemEvalDataLoader.DatasetPathEnvVar)))
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   AuditGrade preset requires {LongMemEvalDataLoader.DatasetPathEnvVar} (the full ~500Q dataset is");
            Console.WriteLine("   gated to opt-in env-var to keep audit runs deliberate). Set it to the absolute");
            Console.WriteLine($"   path of {LongMemEvalDataLoader.CanonicalDatasetFileName} and re-run:");
            Console.WriteLine($"     setx {LongMemEvalDataLoader.DatasetPathEnvVar} \"C:\\path\\to\\{LongMemEvalDataLoader.CanonicalDatasetFileName}\"   (Windows)");
            Console.WriteLine($"     export {LongMemEvalDataLoader.DatasetPathEnvVar}=/path/to/{LongMemEvalDataLoader.CanonicalDatasetFileName}        (Unix)");
            Console.WriteLine("   For development / CI use --preset smoke (10Q) or --preset standard (50Q) —");
            Console.WriteLine("   both pick up the canonical local dataset automatically.");
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
        // v0.10.1-beta: all presets use the real ~500Q dataset on disk.
        // Smoke caps to 10 questions. Standard uses the baked SubsetOptions (default 50).
        // AuditGrade switches to Full() which mandates LONGMEMEVAL_DATASET_PATH.
        AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner runner;
        ExternalBenchmarkOptions options;
        string presetName;
        try
        {
            switch (preset)
            {
                case SamplePreset.AuditGrade:
                    runner = LongMemEvalBenchmark.Full(chatClient);
                    options = LongMemEvalBenchmark.FullOptions;
                    presetName = "audit-grade";
                    break;
                case SamplePreset.Standard:
                    runner = LongMemEvalBenchmark.Subset(chatClient);
                    options = LongMemEvalBenchmark.SubsetOptions;
                    presetName = "standard";
                    break;
                default: // Smoke
                    runner = LongMemEvalBenchmark.Subset(chatClient);
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
        }
        catch (InvalidOperationException ex)
        {
            // AuditGrade's Full() factory throws when LONGMEMEVAL_DATASET_PATH is unset.
            // The pre-flight above already handles this, but catch defensively so a future
            // semantics change can't crash the menu.
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   LongMemEval preset selection failed: {ex.Message}");
            Console.ResetColor();
            return;
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
        catch (LongMemEvalDatasetNotFoundException ex)
        {
            PrintDatasetMissingBox(ex);
            return;
        }
        catch (FileNotFoundException ex) when (ex.Message.Contains("LongMemEval", StringComparison.OrdinalIgnoreCase))
        {
            // Defensive: any other LongMemEval-flavoured file-not-found that isn't the
            // typed exception (e.g. from a custom DatasetPath override pointing at a
            // missing file) — still render the friendly box rather than the menu crash.
            PrintDatasetMissingBox(ex);
            return;
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
        Console.WriteLine("   - v0.10.1+: Smoke=10Q, Standard=50Q, AuditGrade=~500Q — all real dataset.");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Friendly download-instructions box for missing dataset
    // ──────────────────────────────────────────────────────────────────────

    private static void PrintDatasetMissingBox(Exception ex)
    {
        var canonical = (ex as LongMemEvalDatasetNotFoundException)?.CanonicalLocalPath
            ?? Path.Combine("src", "AgentEval.Memory", "Data", "longmemeval", LongMemEvalDataLoader.CanonicalDatasetFileName);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("   +----------------------------------------------------------------------------+");
        Console.WriteLine("   |  LongMemEval dataset not found.                                            |");
        Console.WriteLine("   |                                                                            |");
        Console.WriteLine("   |  H9 LongMemEval requires the real LongMemEval dataset — no fake data is    |");
        Console.WriteLine("   |  bundled (so scores stay paper-comparable).                                |");
        Console.WriteLine("   |                                                                            |");
        Console.WriteLine("   |  To run this sample:                                                       |");
        Console.WriteLine("   |    1. Download from:                                                       |");
        Console.WriteLine($"   |       {LongMemEvalDataLoader.DatasetDownloadUrl,-69}|");
        Console.WriteLine("   |    2. Place the file at:                                                   |");
        Console.WriteLine($"   |       {canonical,-69}|");
        Console.WriteLine($"   |       (or set {LongMemEvalDataLoader.DatasetPathEnvVar} to point at the file)              |");
        Console.WriteLine("   |    3. Re-run sample H9.                                                    |");
        Console.WriteLine("   |                                                                            |");
        Console.WriteLine("   |  Returning to the menu — no run executed.                                  |");
        Console.WriteLine("   +----------------------------------------------------------------------------+");
        Console.ResetColor();
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
