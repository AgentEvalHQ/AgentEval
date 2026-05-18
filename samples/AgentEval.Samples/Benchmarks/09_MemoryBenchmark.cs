// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using AgentEval.Benchmarks;                       // → production factory MemoryBenchmark (presets) + the BenchmarkFamilyRegistry shape
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Output;

namespace AgentEval.Samples.Benchmarks;

/// <summary>
/// Benchmarks H9: Memory — comprehensive agent-state-stressing memory benchmark.
///
/// Memory is a Shape B / runner-style benchmark (ADR-017 Convention 3). This sample
/// wires the preset-driven (Smoke / Standard / AuditGrade) running-sample shape used
/// by H2–H8 over the Memory runner: real Azure OpenAI agent + LLM judge + canonical
/// <c>.agenteval/</c> store + sidecar JSON / HTML / PDF. The native
/// <c>MemoryBenchmarkResult</c> is also written to <c>report-native.json</c> for
/// power users who need the unaltered per-category shape (grade, stars, weak
/// categories, recommendations).
///
/// Preset mapping (real agent, real LLM judge — every preset costs real money):
/// <list type="bullet">
///   <item><see cref="SamplePreset.Smoke"/> → <c>MemoryBenchmark.Quick</c> (3 categories — basic retention, temporal, noise)</item>
///   <item><see cref="SamplePreset.Standard"/> → <c>MemoryBenchmark.Standard</c> (8 categories incl. Abstention + Preference Extraction)</item>
///   <item><see cref="SamplePreset.AuditGrade"/> → <c>MemoryBenchmark.Full</c> (12 categories incl. cross-session, conflict, multi-session reasoning)</item>
/// </list>
/// </summary>
/// <remarks>
/// Requires Azure OpenAI credentials. Skips gracefully when missing. Mirrors the
/// shape of <see cref="LongMemEvalBenchmarkSample"/> (H8) — same Shape-B-to-Shape-A
/// bridging via <see cref="SynthesizeEvalResult"/> so the unified canonical store +
/// sidecar JSON / HTML / PDF artefacts come out identical to every other Group-H
/// sample. The unaltered native shape is preserved in <c>report-native.json</c>.
/// </remarks>
public static class MemoryBenchmarkSample
{
    public static async Task RunAsync()
    {
        BenchmarkSampleHelpers.PrintHeader(
            "Benchmarks H9: Memory — comprehensive memory benchmark",
            "Quick (3 cats) / Standard (8 cats) / Full (12 cats) — real agent + LLM judge");

        // ── Force-load AgentEval.Memory so [ModuleInitializer] registers "memory".
        // The FQN is used consistently across all force-load anchors here (and in
        // 01_RegistryDiscovery / 08_LongMemEvalBenchmark) so a future name shadow
        // elsewhere in the Samples assembly can't silently break the load.
        _ = typeof(AgentEval.Benchmarks.MemoryBenchmark).Assembly;

        // ── Registry metadata header (pedagogical — keep it short).
        var family = BenchmarkFamilyRegistry.TryGet("memory");
        if (family is null)
        {
            Console.WriteLine("   Memory family is not registered. Did the umbrella package include AgentEval.Memory?");
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
            BenchmarkSampleHelpers.PrintMissingCredentialsBox("BENCHMARKS H9 — Memory");
            return;
        }

        var preset = BenchmarkSampleHelpers.ResolvePreset();
        BenchmarkSampleHelpers.PrintPreset(preset);

        // ── Build the agent (history-injectable — Memory benchmark depends on it
        // for reach-back / cross-session / reducer scenarios that inject prepared
        // histories into the agent before probing).
        var azureClient = new AzureOpenAIClient(AIConfig.Endpoint, AIConfig.KeyCredential);
        var chatClient = azureClient.GetChatClient(AIConfig.ModelDeployment).AsIChatClient();

        var agent = chatClient.AsEvaluableAgent(
            name: "MemoryBenchmarkSampleAgent",
            systemPrompt: """
                You are a helpful assistant with excellent memory.
                Remember all facts the user tells you and recall them accurately when asked.
                When asked about something you were told, include the specific details in your response.
                Keep responses concise but accurate.
                """,
            includeHistory: true);

        // ── Resolve the Memory preset from the SamplePreset tier.
        // Smoke → Quick (3 categories, fastest)
        // Standard → Standard (8 categories — adds Abstention + Preference Extraction)
        // AuditGrade → Full (12 categories — adds cross-session / conflict / multi-session)
        var memoryPreset = preset switch
        {
            SamplePreset.AuditGrade => MemoryBenchmark.Full,
            SamplePreset.Standard   => MemoryBenchmark.Standard,
            _                       => MemoryBenchmark.Quick,
        };

        Console.WriteLine();
        Console.WriteLine($"   Model:               {AIConfig.ModelDeployment}");
        Console.WriteLine($"   Memory preset:       {memoryPreset.Name} ({memoryPreset.Categories.Count} categories)");
        Console.WriteLine($"   History injection:   {(agent is IHistoryInjectableAgent ? "structured (IHistoryInjectableAgent)" : "text blob fallback")}");
        Console.WriteLine($"   Judge:               same deployment as agent (sample simplicity)");
        Console.WriteLine();
        Console.WriteLine($"   Running Memory benchmark ({memoryPreset.Name})... this fans LLM calls across categories.");
        Console.WriteLine();

        // ── Wire the canonical Memory runner (DI-free Create factory keeps the
        // sample readable — Group-G G2 wires the components by hand for the
        // production-DI demonstration).
        var runner = MemoryBenchmarkRunner.Create(chatClient);

        // ── Progress reporter: print one line per completed category so a 12-cat
        // Full run doesn't look like it's hung. Mirrors the G2 demo's behaviour.
        var progress = new Progress<BenchmarkProgress>(p =>
        {
            var icon = p.Skipped ? "skip" : (p.Score >= 80 ? "pass" : p.Score >= 60 ? "warn" : "fail");
            Console.WriteLine(
                $"   [{p.CompletedCategories,2}/{p.TotalCategories,2}] {p.CategoryName,-30} " +
                $"{p.Score,6:F1}% ({icon}) — elapsed {p.Elapsed.TotalSeconds:F1}s, ~{p.EstimatedRemaining.TotalSeconds:F0}s remaining");
        });

        MemoryBenchmarkResult result;
        try
        {
            result = await runner.RunBenchmarkAsync(agent, memoryPreset, progress);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   Memory benchmark run failed: {ex.GetType().Name}: {ex.Message}");
            Console.ResetColor();
            return;
        }

        // ── Console summary — mirrors the G2 demo so users coming from there feel at home.
        Console.WriteLine();
        Console.Write("   Overall score:    ");
        PrintScore(result.OverallScore);
        Console.WriteLine($"   Grade: {result.Grade}  {new string('*', result.Stars)}{new string('-', 5 - result.Stars)}  ({(result.Passed ? "PASSED" : "FAILED")})");
        Console.WriteLine($"   Duration:         {result.Duration.TotalSeconds:F1}s");
        Console.WriteLine();
        Console.WriteLine("   +----------------------------------+---------+---------+-------+");
        Console.WriteLine("   | Category                         | Score   | Weight  | Stars |");
        Console.WriteLine("   +----------------------------------+---------+---------+-------+");
        foreach (var cat in result.CategoryResults)
        {
            if (cat.Skipped)
            {
                Console.WriteLine($"   | {cat.CategoryName,-32} | SKIPPED | {cat.Weight,7:P0} |       |  ({cat.SkipReason})");
            }
            else
            {
                Console.Write($"   | {cat.CategoryName,-32} | ");
                PrintScore(cat.Score);
                Console.WriteLine($" | {cat.Weight,7:P0} | {new string('*', cat.Stars),-5} |");
            }
        }
        Console.WriteLine("   +----------------------------------+---------+---------+-------+");

        if (result.WeakCategories.Count > 0)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"   Weak categories: {string.Join(", ", result.WeakCategories)}");
            Console.ResetColor();
        }

        // ── Synthesise an EvalResult tree from the Shape-B result so we get
        // canonical .agenteval/ persistence + sidecar JSON / HTML / PDF for free.
        var evalTree = SynthesizeEvalResult(result, memoryPreset.Name);

        var subject = new SubjectIdentity(
            Kind: SubjectKind.Agent,
            Name: agent.Name,
            ModelId: AIConfig.ModelDeployment,
            Framework: "MAF");

        var paths = await BenchmarkSampleHelpers.WriteReportsViaStoreAsync(
            evalTree, subject,
            benchmarkName: "memory",
            regulationOrBenchmark: $"Memory benchmark — {memoryPreset.Name} preset",
            includePdf: true,
            regulationCodeForEvidence: null, // not a compliance benchmark
            presetLabel: memoryPreset.Name.ToLowerInvariant(),
            judgeModel: AIConfig.ModelDeployment);

        // ── Side-write the native MemoryBenchmarkResult alongside report.json so
        // power users keep the unaltered shape (grade, stars, weak categories,
        // recommendations — the synthesised EvalResult tree flattens to score+label).
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
        Console.WriteLine("   - Memory is a Shape B family — runner returns MemoryBenchmarkResult,");
        Console.WriteLine("     synthesised into an EvalResult tree (root + per-category leaves with weights).");
        Console.WriteLine("   - report.json / report.html / report.pdf reflect the synthesised tree;");
        Console.WriteLine("     report-native.json carries the unaltered MemoryBenchmarkResult shape");
        Console.WriteLine("     (grade, stars, weak categories, recommendations).");
        Console.WriteLine("   - Smoke=Quick (3 cats); Standard=8 cats; AuditGrade=Full (12 cats).");
        Console.WriteLine("   - For DI-wired component setup see Group-G G2 (MemoryBenchmarkDemo).");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  Synthesis: MemoryBenchmarkResult → EvalResult tree
    //  Root composite (overall weighted score) → per-category atomic leaves.
    // ──────────────────────────────────────────────────────────────────────

    private static EvalResult SynthesizeEvalResult(MemoryBenchmarkResult result, string presetName)
    {
        var now = DateTimeOffset.UtcNow;
        const double passThresholdPercent = 70.0; // matches MemoryBenchmarkResult.Passed contract (≥70%)
        const double passThreshold01 = 0.7;

        // Build per-category atomic leaves.
        var categoryNodes = new List<EvalResult>(result.CategoryResults.Count);
        foreach (var cat in result.CategoryResults)
        {
            if (cat.Skipped)
            {
                // Honest skipped leaf — does not contribute to the verdict.
                categoryNodes.Add(new EvalResult(
                    Metric: new EvalMetadata(
                        Key: $"memory.{cat.ScenarioType}",
                        Name: cat.CategoryName,
                        Category: "memory",
                        Version: "1.0.0"),
                    Score: new EvalScore(
                        Value: 0,
                        Ordinal: null,
                        Label: "skipped",
                        Passed: false,
                        Threshold: passThreshold01,
                        Severity: "none",
                        Confidence: null),
                    Details: new EvalDetails(
                        Dimensions: new Dictionary<string, double>
                        {
                            ["weight"] = cat.Weight,
                            ["durationSeconds"] = cat.Duration.TotalSeconds,
                        },
                        Evidence: null,
                        Recommendations: cat.SkipReason is null
                            ? null
                            : new[] { $"Skipped: {cat.SkipReason}" },
                        SubResults: null,
                        AggregationStrategy: null),
                    Provenance: new EvalProvenance(
                        Type: "skipped",
                        JudgeModel: null,
                        PromptId: null,
                        PromptHash: null,
                        TokensUsed: null,
                        EstimatedCost: 0,
                        CacheHit: false),
                    EvaluatedAt: now));
                continue;
            }

            // BenchmarkCategoryResult.Score is 0..100; normalise to 0..1 for the EvalResult tree.
            var score01 = cat.Score / 100.0;
            var passed = cat.Score >= passThresholdPercent;
            var label = cat.Score >= 80 ? "pass" : cat.Score >= 60 ? "warn" : "fail";
            var severity = cat.Score >= 80 ? "none" : cat.Score >= 60 ? "low" : "medium";

            categoryNodes.Add(new EvalResult(
                Metric: new EvalMetadata(
                    Key: $"memory.{cat.ScenarioType}",
                    Name: cat.CategoryName,
                    Category: "memory",
                    Version: "1.0.0"),
                Score: new EvalScore(
                    Value: score01,
                    Ordinal: null,
                    Label: label,
                    Passed: passed,
                    Threshold: passThreshold01,
                    Severity: severity,
                    Confidence: null),
                Details: new EvalDetails(
                    Dimensions: new Dictionary<string, double>
                    {
                        ["weight"] = cat.Weight,
                        ["scorePercent"] = cat.Score,
                        ["stars"] = cat.Stars,
                        ["durationSeconds"] = cat.Duration.TotalSeconds,
                    },
                    Evidence: null,
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

        // Root composite (overall weighted score in 0..1).
        var overall01 = result.OverallScore / 100.0;
        var overallPassed = result.Passed;
        var overallLabel = result.OverallScore >= 80 ? "pass" : result.OverallScore >= 60 ? "warn" : "fail";
        var overallSeverity = result.OverallScore >= 80 ? "none" : result.OverallScore >= 60 ? "low" : "medium";

        return new EvalResult(
            Metric: new EvalMetadata(
                Key: "memory",
                Name: $"Memory benchmark — {presetName} preset",
                Category: "memory",
                Version: "1.0.0"),
            Score: new EvalScore(
                Value: overall01,
                Ordinal: null,
                Label: overallLabel,
                Passed: overallPassed,
                Threshold: passThreshold01,
                Severity: overallSeverity,
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: new Dictionary<string, double>
                {
                    ["overallScorePercent"] = result.OverallScore,
                    ["stars"] = result.Stars,
                    ["totalCategories"] = result.CategoryResults.Count,
                    ["weakCategories"] = result.WeakCategories.Count,
                    ["skippedCategories"] = result.SkippedCategories.Count,
                    ["durationSeconds"] = result.Duration.TotalSeconds,
                },
                Evidence: null,
                Recommendations: result.Recommendations.ToArray(),
                SubResults: categoryNodes,
                AggregationStrategy: "weighted-mean"),
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
        Console.ForegroundColor = scorePercent >= 80 ? ConsoleColor.Green :
                                  scorePercent >= 60 ? ConsoleColor.Yellow : ConsoleColor.Red;
        Console.Write($"{scorePercent,6:F1}%");
        Console.ResetColor();
    }
}
