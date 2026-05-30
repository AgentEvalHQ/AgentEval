// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentEval.Memory.Models;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench longmemeval</c> subcommand (plan-13 T0.6).
/// Closes the CLI ↔ <see cref="BenchmarkFamilyRegistry"/> gap where <c>bench --list</c>
/// advertised <c>longmemeval</c> but no command existed to invoke it.
/// </summary>
/// <remarks>
/// LongMemEval is a Shape B / runner-style benchmark (ADR-017 Convention 3) — its native
/// result (<see cref="ExternalBenchmarkResult"/>) does not fit the
/// <c>EvaluateAsync(EvalInput) → EvalResult</c> shape that <c>bench perf</c> uses, so this
/// command writes the native shape directly to <c>report-native.json</c> alongside the
/// canonical run manifest. A human-readable summary lands in <c>report.md</c>.
///
/// <para>
/// REQUIRES Azure OpenAI (the runner makes ~2 LLM calls per question). All three
/// <c>AZURE_OPENAI_*</c> env vars must be set; there is no stub fallback because
/// LongMemEval's correctness signal IS the LLM grader.
/// </para>
///
/// <para>
/// The AuditGrade / Full preset additionally requires <c>LONGMEMEVAL_DATASET_PATH</c>
/// pointing at the full ~500-question dataset (the canonical bundled file ships only the
/// Subset; cf. <see cref="LongMemEvalDataLoader.DatasetPathEnvVar"/>). When unset, the
/// command exits cleanly with a clear download-instructions message.
/// </para>
/// </remarks>
public static class BenchLongMemEvalCommand
{
    /// <summary>Runs the bench longmemeval command using auto-discovered workspace root.</summary>
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
            Console.Error.WriteLine("Could not find a solution root (.sln, .slnx, or .git). " +
                "Provide --root or run from within a solution directory.");
            return 1;
        }

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        if (!Directory.Exists(agentEvalDir))
        {
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init` first.");
            return 1;
        }

        // ── Anchor module init so the family is discoverable. ────────────────
        _ = typeof(LongMemEvalBenchmark).Assembly;

        var family = BenchmarkFamilyRegistry.TryGet("longmemeval");
        if (family is null)
        {
            Console.Error.WriteLine("longmemeval family is not registered. Ensure AgentEval.Memory is referenced.");
            return 1;
        }
        if (family.Presets.All(p => !string.Equals(p.Name, preset, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Unknown longmemeval preset '{preset}'. Known: " +
                string.Join(", ", family.Presets.Select(p => p.Name)));
            return 1;
        }

        // ── Resolve chat client ──────────────────────────────────────────────
        Microsoft.Extensions.AI.IChatClient chatClient;
        string deployment;
        if (chatClientOverride is not null)
        {
            chatClient = chatClientOverride;
            deployment = "override";
        }
        else
        {
            var (resolved, resolvedDeployment, exitCode) = AzureChatAgentFactory.TryBuildChatClientFromEnv();
            if (resolved is null) return exitCode;
            chatClient = resolved;
            deployment = resolvedDeployment!;
        }

        // ── Resolve runner + options for preset ──────────────────────────────
        LongMemEvalBenchmarkRunner runner;
        ExternalBenchmarkOptions options;
        try
        {
            if (string.Equals(preset, "full", StringComparison.OrdinalIgnoreCase))
            {
                runner = LongMemEvalBenchmark.Full(chatClient);
                options = LongMemEvalBenchmark.FullOptions;
            }
            else
            {
                // Default to "subset"
                runner = LongMemEvalBenchmark.Subset(chatClient);
                options = LongMemEvalBenchmark.SubsetOptions;
            }
        }
        catch (InvalidOperationException ex)
        {
            // Full() throws when LONGMEMEVAL_DATASET_PATH is unset.
            Console.Error.WriteLine($"LongMemEval preset selection failed: {ex.Message}");
            Console.Error.WriteLine($"Set {LongMemEvalDataLoader.DatasetPathEnvVar} or use --preset subset.");
            return 1;
        }

        // ── Build the agent-under-test ───────────────────────────────────────
        var agent = chatClient.AsEvaluableAgent(
            name: subject,
            systemPrompt: "You are a helpful assistant. Answer questions based on our conversation history.",
            includeHistory: true);

        var benchmarkConfig = new AgentBenchmarkConfig
        {
            AgentName = subject,
            ModelId = deployment,
            ReducerStrategy = "None",
            MemoryProvider = "InMemoryChatHistory (history injection)"
        };

        // ── Persist setup ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running LongMemEval ({preset}) for subject '{subject}' against {deployment}...");
        Console.WriteLine($"   Max questions:       {(options.MaxQuestions?.ToString() ?? "all in dataset")}");
        Console.WriteLine($"   Scoring:             binary (0/1) — official LongMemEval methodology");
        Console.WriteLine();

        // ── Run ──────────────────────────────────────────────────────────────
        ExternalBenchmarkResult result;
        try
        {
            result = await runner.RunAsync(agent, benchmarkConfig, options, ct);
        }
        catch (LongMemEvalDatasetNotFoundException ex)
        {
            Console.Error.WriteLine($"✖ LongMemEval dataset not found: {ex.Message}");
            Console.Error.WriteLine($"  Download longmemeval_s_cleaned.json and place at the canonical location or set {LongMemEvalDataLoader.DatasetPathEnvVar}.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LongMemEval run failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        // ── Persist via output store ─────────────────────────────────────────
        string runId;
        try
        {
            var manifest = await store.StartRunAsync(
                subjectIdentity,
                new RunContext(
                    EvalProject: "AgentEval.Memory.LongMemEval",
                    EvalProjectPath: "src/AgentEval.Memory/External/LongMemEval/",
                    Harness: "BenchLongMemEvalCommand",
                    Seed: options.RandomSeed,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            runId = manifest.Run.RunId;

            // Shape B / Convention 3 (ADR-017): LongMemEval's native ExternalBenchmarkResult
            // is "N questions → accuracy", which doesn't fit the per-scenario EvalResult shape.
            // Skip per-scenario writes; persist the native result as report-native.json beside
            // the manifest below. The summary still captures pass/fail totals.

            var verdict = result.OverallAccuracy >= 0.5 ? "PASS" : "FAIL";
            var summary = new RunSummary(
                SchemaVersion: "1.0",
                RunId: runId,
                Verdict: verdict,
                Stats: new RunStats(
                    Total: result.QuestionResults.Count,
                    Passed: result.QuestionResults.Count(q => q.Correct),
                    Failed: result.QuestionResults.Count(q => !q.Correct),
                    Warnings: 0),
                Metrics: new Dictionary<string, double>
                {
                    ["overall_accuracy"] = result.OverallAccuracy,
                    ["task_averaged_accuracy"] = result.TaskAveragedAccuracy,
                });
            await store.CompleteRunAsync(manifest, summary, ct);

            // Write the native ExternalBenchmarkResult JSON alongside the manifest.
            // Resolve via the store so the report path matches the manifest exactly; hand-building
            // from the RAW subject diverges when FileSystemLayout.Sanitize rewrites the name (BUG-17).
            var runDir = store.ResolveRunDirectory(subjectIdentity, runId);
            await File.WriteAllTextAsync(
                Path.Combine(runDir, "report-native.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

            Console.WriteLine();
            Console.WriteLine($"   Completed in {result.Duration.TotalSeconds:F1}s ({result.TotalLlmCalls} LLM calls)");
            Console.WriteLine($"   Overall accuracy:        {result.OverallAccuracy:P1}  ({result.QuestionResults.Count(q => q.Correct)}/{result.QuestionResults.Count})");
            Console.WriteLine($"   Task-averaged accuracy:  {result.TaskAveragedAccuracy:P1}");
            Console.WriteLine($"   Verdict:                 {verdict}");
            Console.WriteLine();
            Console.WriteLine($"   Run ID: {runId}");
            Console.WriteLine($"   Canonical: {runDir}");
            Console.WriteLine($"   Native:    {Path.Combine(runDir, "report-native.json")}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist LongMemEval run: {ex.Message}");
            return 1;
        }

        return result.OverallAccuracy >= 0.5 ? 0 : 2;
    }
}
