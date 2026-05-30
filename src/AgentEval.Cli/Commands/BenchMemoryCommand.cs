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
/// / <c>overflow</c> (deliberately exceeds 128K context). All require Azure OpenAI; no
/// stub fallback because Memory's signal IS the LLM round-trips.
/// </para>
/// </remarks>
public static class BenchMemoryCommand
{
    public static Task<int> RunAsync(string preset, string subject, string? rootOverride)
        => RunAsync(preset, subject, rootOverride, chatClientOverride: null);

    /// <summary>Internal overload exposed for tests; allows chat-client injection.</summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        Microsoft.Extensions.AI.IChatClient? chatClientOverride)
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
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init` first.");
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
                return 1;
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
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24));
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running memory benchmark ({preset}) for subject '{subject}'...");

        // ── Run ──────────────────────────────────────────────────────────────
        var runner = MemoryBenchmarkRunner.Create(chatClient);
        MemoryBenchmarkResult result;
        try
        {
            result = await runner.RunBenchmarkAsync(agent, benchmark);
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
            var verdict = result.OverallScore >= 70 ? "PASS" : result.OverallScore >= 50 ? "WARN" : "FAIL";
            var summary = new RunSummary(
                SchemaVersion: "1.0",
                RunId: runId,
                Verdict: verdict,
                Stats: new RunStats(
                    Total: result.CategoryResults.Count,
                    Passed: result.CategoryResults.Count(c => c.Score >= 70),
                    Failed: result.CategoryResults.Count(c => c.Score < 50),
                    Warnings: result.CategoryResults.Count(c => c.Score >= 50 && c.Score < 70)),
                Metrics: new Dictionary<string, double>
                {
                    ["overall_score"] = result.OverallScore,
                });
            await store.CompleteRunAsync(manifest, summary);

            // Native MemoryBenchmarkResult JSON alongside the manifest (Shape B per ADR-017).
            // Resolve via the store so the report path matches the manifest exactly; hand-building
            // from the RAW subject diverges when FileSystemLayout.Sanitize rewrites the name (BUG-17).
            var runDir = store.ResolveRunDirectory(subjectIdentity, runId);
            await File.WriteAllTextAsync(
                Path.Combine(runDir, "report-native.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine();
            Console.WriteLine($"   Overall score: {result.OverallScore:F1}%  Grade: {result.Grade}");
            Console.WriteLine($"   Verdict:       {verdict}");
            foreach (var cat in result.CategoryResults)
            {
                Console.WriteLine($"   {cat.CategoryName,-30} {cat.Score:F1}%");
            }
            Console.WriteLine();
            Console.WriteLine($"   Run ID: {runId}");
            Console.WriteLine($"   Canonical: {runDir}");
            Console.WriteLine($"   Native:    {Path.Combine(runDir, "report-native.json")}");

            return verdict == "FAIL" ? 2 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist memory benchmark run: {ex.Message}");
            return 1;
        }
    }
}
