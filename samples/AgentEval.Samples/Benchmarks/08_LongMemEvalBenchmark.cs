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
/// sample shape used by H2–H8 over the LongMemEval runner: real model-backed
/// agent (history-injectable) + LLM judge + canonical <c>.agenteval/</c> store
/// + sidecar JSON / HTML / PDF. Set
/// <c>AGENTEVAL_LONGMEMEVAL_WRITE_NATIVE_REPORT=true</c> to additionally write
/// the unaltered <c>ExternalBenchmarkResult</c> to <c>report-native.json</c>.
/// That opt-in artefact contains questions, answers, responses, and potentially
/// captured evidence content, so it must be access-controlled.
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
///   <item><see cref="SamplePreset.Standard"/> → <c>Subset(chatClient)</c>, the preset's 50 questions</item>
///   <item><see cref="SamplePreset.AuditGrade"/> → <c>Full(chatClient)</c> all ~500 questions (requires <c>LONGMEMEVAL_DATASET_PATH</c>, ~30–60 min)</item>
/// </list>
/// Every selected question is accounted for (scored, judge-inconclusive or an agent error), the per-type table shows
/// what each accuracy is over, and the answer prompts are measured on their way to the provider, as in G8.
/// </summary>
/// <remarks>
/// Requires a model provider (see AIConfig). Skips gracefully when missing.
/// Smoke + Standard work without any env var when the canonical dataset file
/// is present at <c>&lt;workspace-root&gt;/src/AgentEval.Memory/Data/longmemeval/longmemeval_s_cleaned.json</c>.
/// AuditGrade additionally requires the <c>LONGMEMEVAL_DATASET_PATH</c>
/// environment variable (deliberate audit-grade gate); when unset, the sample
/// exits cleanly with a clear message rather than throwing.
/// </remarks>
public static class LongMemEvalBenchmarkSample
{
    private const string WriteNativeReportEnvVar = "AGENTEVAL_LONGMEMEVAL_WRITE_NATIVE_REPORT";

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
            SampleOutcome.Skip("no chat provider configured");
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
            SampleOutcome.Skip($"the audit-grade preset needs {LongMemEvalDataLoader.DatasetPathEnvVar}, which is not set");
            return;
        }

        // ── Build the agent (history-injectable; same shape Group-G uses).
        // The answer model's client is metered: each prompt's size as sent, and as the provider counted it. The judge
        // gets its own client so only answer prompts are measured.
        var answerClient = new PromptSizeMeter(AIConfig.CreateChatClient(AIConfig.ModelDeployment));
        var judgeClient = AIConfig.CreateChatClient(AIConfig.ModelDeployment);

        var agent = answerClient.AsEvaluableAgent(
            name: "LongMemEvalSampleAgent",
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        var benchmarkConfig = new AgentBenchmarkConfig
        {
            AgentName = "LongMemEvalSampleAgent",
            ModelId = AIConfig.ModelDeployment,
            ReducerStrategy = "None",
            // The options below leave HistoryInjectionMode at its default, TextBlob. This used to say
            // "InMemoryChatHistory (history injection)", which is not how the history reached the model.
            MemoryProvider = "TextBlob (every session in the user message)"
        };

        // ── Resolve runner + options for the chosen preset.
        // v0.10.1-beta: all presets use the real ~500Q dataset on disk.
        // Smoke caps to 10 questions. Standard runs the Subset preset's 50. AuditGrade switches to Full(), which
        // mandates LONGMEMEVAL_DATASET_PATH. Each preset's options are the ones LongMemEvalBenchmark.SubsetOptions /
        // FullOptions build, plus a judge output budget a reasoning judge can answer in
        // (LongMemEvalSampleSupport.JudgeMaxOutputTokens); ExternalBenchmarkOptions is a class, so they cannot be
        // copied with `with`.
        static ExternalBenchmarkOptions PresetOptions(int? maxQuestions, string datasetMode) => new()
        {
            MaxQuestions = maxQuestions,
            StratifiedSampling = true,
            RandomSeed = 42,
            PreserveSessionBoundaries = true,
            IncludeTimestamps = true,
            DatasetMode = datasetMode,
            JudgeMaxOutputTokens = LongMemEvalSampleSupport.JudgeMaxOutputTokens,
        };

        AgentEval.Memory.External.LongMemEval.LongMemEvalBenchmarkRunner runner;
        ExternalBenchmarkOptions options;
        string presetName;
        try
        {
            switch (preset)
            {
                case SamplePreset.AuditGrade:
                    runner = LongMemEvalBenchmark.Full(judgeClient);
                    options = PresetOptions(maxQuestions: null, "Full");
                    presetName = "audit-grade";
                    break;
                case SamplePreset.Standard:
                    runner = LongMemEvalBenchmark.Subset(judgeClient);
                    options = PresetOptions(LongMemEvalBenchmark.SubsetMaxQuestions, "Subset");
                    presetName = "standard";
                    break;
                default: // Smoke
                    runner = LongMemEvalBenchmark.Subset(judgeClient);
                    options = PresetOptions(maxQuestions: 10, "Subset");
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
            SampleOutcome.Skip($"LongMemEval preset selection failed: {ex.Message}");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"   Model:               {AIConfig.ModelDeployment} — it also judges its own answers");
        Console.WriteLine($"   History injection:   {options.HistoryInjectionMode} — every session, with its date, in the user message");
        Console.WriteLine($"   Max questions:       {(options.MaxQuestions?.ToString() ?? "all in dataset")}");
        Console.WriteLine($"   Stratified sampling: {options.StratifiedSampling}");
        Console.WriteLine($"   Judge budget:        {options.JudgeMaxOutputTokens} output tokens (a reasoning judge's thinking included)");
        Console.WriteLine($"   Scoring:             binary (0/1) — official LongMemEval methodology");
        Console.WriteLine();
        Console.WriteLine($"   Running LongMemEval ({presetName})... judge retries can increase call count.");
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
            SampleOutcome.Skip("the LongMemEval dataset was not found");
            return;
        }
        catch (FileNotFoundException ex) when (ex.Message.Contains("LongMemEval", StringComparison.OrdinalIgnoreCase))
        {
            // Defensive: any other LongMemEval-flavoured file-not-found that isn't the
            // typed exception (e.g. from a custom DatasetPath override pointing at a
            // missing file) — still render the friendly box rather than the menu crash.
            PrintDatasetMissingBox(ex);
            SampleOutcome.Skip("the LongMemEval dataset was not found");
            return;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   LongMemEval run failed: {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
            SampleOutcome.Error($"the LongMemEval run threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // ── Every selected question accounted for, and the per-type table over scored questions (as G8 prints them).
        Console.WriteLine($"   Completed in {result.Duration.TotalSeconds:F1}s ({result.TotalLlmCalls} LLM calls)");
        Console.WriteLine();
        Console.Write("   Overall accuracy:      ");
        LongMemEvalSampleSupport.PrintScore(result.OverallAccuracy);
        Console.WriteLine($" ({result.CorrectQuestions}/{result.ScoredQuestions} scored correct)");
        LongMemEvalSampleSupport.PrintAccounting(result);
        Console.Write("   Task-averaged accuracy: ");
        LongMemEvalSampleSupport.PrintScore(result.TaskAveragedAccuracy);
        Console.WriteLine(" (macro-average of per-type accuracies)");
        Console.WriteLine();
        LongMemEvalSampleSupport.PrintPerTypeTable(result);

        // Did the haystack reach the model? Measured on the answer calls, not assumed from the dataset.
        var truncated = LongMemEvalSampleSupport.PrintHaystackDelivery(answerClient.Calls);

        // ── Synthesise an EvalResult tree from the Shape-B result so we get
        // canonical .agenteval/ persistence + sidecar JSON / HTML / PDF for free.
        var evalTree = LongMemEvalEvalResultAdapter.ToEvalResult(
            result,
            presetName,
            judgeModel: AIConfig.ModelDeployment);

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

        // The native shape carries raw benchmark/user content and is therefore
        // an explicit opt-in rather than a default sidecar.
        if (string.Equals(
                Environment.GetEnvironmentVariable(WriteNativeReportEnvVar),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var nativeJsonPath = Path.Combine(paths.SidecarDir, "report-native.json");
                await File.WriteAllTextAsync(
                    nativeJsonPath,
                    JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"   Native report (sensitive): {nativeJsonPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   Warning: failed to write report-native.json: {ex.GetType().Name}: {ex.Message}");
            }
        }

        BenchmarkSampleHelpers.PrintReportPaths(evalTree, paths);
        // The tree's root reads the scored questions against the 50% mark; the accounting records what it leaves out:
        // a truncated haystack, every answer call failing, or questions with no verdict.
        SampleOutcome.RecordVerdict("LongMemEval benchmark", evalTree);
        LongMemEvalSampleSupport.RecordVerdict(result, truncated);
        BenchmarkSampleHelpers.OfferToOpenReports(paths);

        Console.WriteLine();
        Console.WriteLine("   KEY TAKEAWAYS:");
        Console.WriteLine("   - LongMemEval is a Shape B family — runner returns ExternalBenchmarkResult,");
        Console.WriteLine("     synthesised into an EvalResult tree (root + per-type composites + per-question leaves).");
        Console.WriteLine("   - Default reports contain status/count diagnostics but no raw question, answer,");
        Console.WriteLine("     response, judge-explanation, or evidence content.");
        Console.WriteLine($"   - Set {WriteNativeReportEnvVar}=true only for access-controlled native diagnostics.");
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
}
