// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Memory;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench memory</c> subcommand (plan-13 T0.6).
/// Closes the CLI ↔ <see cref="BenchmarkFamilyRegistry"/> gap where <c>bench --list</c>
/// advertised <c>memory</c> but no command existed to invoke it.
/// </summary>
/// <remarks>
/// <para>
/// Presets: <c>quick</c> (3 categories) / <c>standard</c> / <c>full</c> / <c>diagnostic</c>
/// / <c>overflow</c> (deliberately exceeds 128K context). All need a real model, from whichever
/// provider <c>AI_INFERENCE_PROVIDER</c> selects (<see cref="AzureChatAgentFactory.TryBuildChatClientFromEnv"/>
/// → <c>ProviderChatClientFactory.TryCreate</c>); there is no stub fallback because
/// Memory's signal IS the LLM round-trips. The one client is both the agent under test and the judge.
/// </para>
/// </remarks>
public static class BenchMemoryCommand
{
    public static Task<int> RunAsync(string preset, string subject, string? rootOverride, CancellationToken ct = default)
        => RunAsync(preset, subject, rootOverride, chatClientOverride: null, ct);

    /// <summary>Internal overload exposed for tests; allows chat-client injection.</summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        Microsoft.Extensions.AI.IChatClient? chatClientOverride,
        CancellationToken ct = default)
    {
        // ── Workspace setup ──────────────────────────────────────────────────
        if (rootOverride is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(rootOverride);
            if (canonical is null) return 1;
            rootOverride = canonical;
        }
        var workspaceRoot = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("Could not find a solution root (.sln, .slnx, or .git).");
            return 1;
        }

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        if (!Directory.Exists(agentEvalDir))
        {
            // `init` is the dataset scaffolder; `init-workspace` is what creates .agenteval/.
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init-workspace` first.");
            return 1;
        }

        // ── Anchor module init ──────────────────────────────────────────────
        _ = typeof(MemoryBenchmark).Assembly;

        var family = BenchmarkFamilyRegistry.TryGet("memory");
        if (family is null)
        {
            Console.Error.WriteLine("memory family is not registered. Ensure AgentEval.Memory is referenced.");
            return 1;
        }

        // ── Resolve benchmark preset ─────────────────────────────────────────
        MemoryBenchmark benchmark;
        switch (preset.ToLowerInvariant())
        {
            case "quick":      benchmark = MemoryBenchmark.Quick; break;
            case "standard":   benchmark = MemoryBenchmark.Standard; break;
            case "full":       benchmark = MemoryBenchmark.Full; break;
            case "diagnostic": benchmark = MemoryBenchmark.Diagnostic; break;
            case "overflow":   benchmark = MemoryBenchmark.Overflow; break;
            default:
                Console.Error.WriteLine($"Unknown memory preset '{preset}'. Known: quick, standard, full, diagnostic, overflow.");
                return ExitCodes.UsageError;
        }

        // ── Resolve chat client ──────────────────────────────────────────────
        Microsoft.Extensions.AI.IChatClient chatClient;
        if (chatClientOverride is not null)
        {
            chatClient = chatClientOverride;
        }
        else
        {
            var (resolved, _, exitCode) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
            if (resolved is null) return exitCode;
            chatClient = resolved;
        }

        // ── Build the agent-under-test ───────────────────────────────────────
        var agent = chatClient.AsEvaluableAgent(
            name: subject,
            systemPrompt: "You are a helpful assistant. Use what you remember from our conversation to answer.",
            includeHistory: true);

        // ── Persist setup ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running memory benchmark ({preset}) for subject '{subject}'...");

        // ── Run ──────────────────────────────────────────────────────────────
        var runner = MemoryBenchmarkRunner.Create(chatClient);
        MemoryBenchmarkResult result;
        try
        {
            result = await runner.RunBenchmarkAsync(agent, benchmark, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Memory benchmark failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        // ── Persist via output store ─────────────────────────────────────────
        try
        {
            var manifest = await store.StartRunAsync(
                subjectIdentity,
                new RunContext(
                    EvalProject: "AgentEval.Memory",
                    EvalProjectPath: "src/AgentEval.Memory/",
                    Harness: "BenchMemoryCommand",
                    Seed: null,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            var runId = manifest.Run.RunId;

            // Align with canonical MemoryBenchmarkResult.Passed semantics
            // (src/AgentEval.Memory/Models/MemoryBenchmarkResult.cs:75 — Passed => OverallScore >= 70).
            // Pre-fix the CLI used 80/50 thresholds which made a canonical Passed=true score
            // of 75 render as "WARN" — confusing for operators reading both the native
            // report-native.json and the CLI summary. Now consistent.
            // An incomplete run (a category that crashed or measured nothing, or questions the judge produced no score
            // for) is neither a pass nor a fail. The stored verdict is WARN, the schema's indeterminate value (as for
            // bench typedmemeval); the console says INCOMPLETE and the exit code is GateIndeterminate (11).
            var verdict = !result.IsComplete ? "INCOMPLETE"
                : result.OverallScore >= 70 ? "PASS" : result.OverallScore >= 50 ? "WARN" : "FAIL";
            var summary = new RunSummary(
                SchemaVersion: "1.0",
                RunId: runId,
                Verdict: verdict == "INCOMPLETE" ? "WARN" : verdict,
                // A skipped, crashed or wholly unmeasured category is neither passed nor failed: it is Skipped.
                Stats: new RunStats(
                    Total: result.CategoryResults.Count,
                    Passed: result.CategoryResults.Count(c => !c.Skipped && c.Score >= 70),
                    Failed: result.CategoryResults.Count(c => !c.Skipped && c.Score < 50),
                    Warnings: result.CategoryResults.Count(c => !c.Skipped && c.Score >= 50 && c.Score < 70),
                    Skipped: result.CategoryResults.Count(c => c.Skipped)),
                Metrics: new Dictionary<string, double>
                {
                    ["overall_score"] = result.OverallScore,
                    ["unmeasured_queries"] = result.UnmeasuredQueries,
                    ["errored_categories"] = result.ErroredCategories.Count,
                });
            // One scenario result per MEASURED category, so `agenteval compare` can diff two memory runs per category
            // (before this, a memory run had no scenarios/ and compare could not read it). A skipped, crashed or wholly
            // unmeasured category measured nothing and is left out: written as a 0 it would read as a regression in
            // compare. Score is on the 0..1 scale every other scenario result uses.
            foreach (var category in result.CategoryResults.Where(c => !c.Skipped))
            {
                var scenarioScore = Math.Clamp(category.Score / 100.0, 0.0, 1.0);
                await store.WriteScenarioResultAsync(runId, new ScenarioResult(
                    Id: "memory-" + category.CategoryName.ToLowerInvariant().Replace(' ', '-'),
                    Name: category.CategoryName,
                    Input: category.ScenarioType.ToString(),
                    Output: $"score {category.Score:F1}",
                    Passed: category.Score >= 70,
                    Score: scenarioScore,
                    Metrics: new Dictionary<string, double>
                    {
                        ["score"] = category.Score,
                        ["weight"] = category.Weight,
                        ["unmeasured_queries"] = category.UnmeasuredQueries,
                    },
                    Assertions: [],
                    Duration: category.Duration,
                    EstimatedCost: 0.0), ct);
            }

            await store.CompleteRunAsync(manifest, summary, ct);

            // Native MemoryBenchmarkResult JSON alongside the manifest (Shape B per ADR-017).
            // Resolve via the store so the report path matches the manifest exactly; hand-building
            // from the RAW subject diverges when FileSystemLayout.Sanitize rewrites the name (BUG-17).
            var runDir = store.ResolveRunDirectory(subjectIdentity, runId);
            await File.WriteAllTextAsync(
                Path.Combine(runDir, "report-native.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine();
            var anythingMeasured = result.CategoryResults.Any(c => !c.Skipped);
            if (result.IsComplete)
            {
                Console.WriteLine($"   Overall score: {result.OverallScore:F1}%  Grade: {result.Grade}");
            }
            else if (anythingMeasured)
            {
                // Incomplete: no grade. The overall counts each unmeasured category as 0; the measured-only figure
                // leaves them out. Neither is the true score, which is why the verdict is INCOMPLETE.
                Console.WriteLine($"   Overall score: {result.OverallScore:F1}% with unmeasured categories at 0; " +
                                  $"{result.CapabilityScore:F1}% over the measured ones only");
            }
            Console.WriteLine($"   Verdict:       {verdict}");
            if (!result.IsComplete)
            {
                if (result.ErroredCategories.Count > 0)
                {
                    Console.WriteLine($"   Not measured:  {string.Join(", ", result.ErroredCategories)} (the run failed, or no question produced a score)");
                }
                if (result.UnmeasuredQueries > 0)
                {
                    Console.WriteLine($"   Unscored:      {result.UnmeasuredQueries} question(s) produced no score; left out of their category");
                }
            }
            foreach (var cat in result.CategoryResults)
            {
                var shown = cat.Errored ? "not measured" : cat.Skipped ? "skipped" : $"{cat.Score:F1}%";
                Console.WriteLine($"   {cat.CategoryName,-30} {shown}");
            }
            Console.WriteLine();
            Console.WriteLine($"   Run ID: {runId}");
            Console.WriteLine($"   Canonical: {runDir}");
            Console.WriteLine($"   Native:    {Path.Combine(runDir, "report-native.json")}");

            // Align with the family convention via the shared BenchExitCodes helper: PASS=>0,
            // WARN=>GateWarning (10), FAIL=>GateFailed (9). Previously WARN returned 0, so a memory
            // run in the 50–69 band silently passed CI while the identical band failed CI for every
            // other benchmark family (BUG-23). An INCOMPLETE run exits GateIndeterminate (11).
            return verdict == "INCOMPLETE" ? ExitCodes.GateIndeterminate : BenchExitCodes.FromLabel(verdict);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist memory benchmark run: {ex.Message}");
            return 1;
        }
    }
}
