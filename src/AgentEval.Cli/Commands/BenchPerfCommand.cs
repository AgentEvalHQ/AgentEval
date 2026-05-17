// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Core.Benchmarks;
using AgentEval.Evals;
using AgentEval.Output;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench perf</c> sub-command tree (Phase 8 / v0.10.0-beta).
/// Sub-commands: <c>latency</c>, <c>throughput</c>, <c>cost</c>. Each resolves the
/// <c>perf</c> family via <see cref="BenchmarkFamilyRegistry.TryGet(string)"/> and
/// dispatches to its Phase-3 <c>EvaluateAsync</c> adapter, persisting the resulting
/// <see cref="EvalResult"/> through the unified output-store.
/// </summary>
public static class BenchPerfCommand
{
    /// <summary>Runs <c>agenteval bench perf {preset}</c> with auto-discovered workspace root.</summary>
    public static Task<int> RunAsync(
        string preset,
        string subject,
        string? prompt,
        string? rootOverride) =>
        RunAsync(preset, subject, prompt, rootOverride, agentOverride: null);

    /// <summary>Internal overload exposed for tests; allows agent injection.</summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? prompt,
        string? rootOverride,
        IEvaluableAgent? agentOverride)
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

        // ── Resolve perf family from registry ────────────────────────────────
        // Touch the Performance assembly to force its module initializer to run.
        _ = typeof(AgentEval.Benchmarks.PerformanceBenchmark).Assembly;

        var family = BenchmarkFamilyRegistry.TryGet("perf");
        if (family is null)
        {
            Console.Error.WriteLine("Perf benchmark family is not registered. " +
                "This usually means the AgentEval.Evals.Performance assembly failed to load.");
            return 1;
        }

        if (family.Presets.All(p => !string.Equals(p.Name, preset, StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine($"Unknown perf preset '{preset}'. Known presets: " +
                $"{string.Join(", ", family.Presets.Select(p => p.Name))}.");
            return 1;
        }

        // ── Resolve target agent ─────────────────────────────────────────────
        // The CLI uses a deterministic stub agent by default. Real targets override via the
        // internal overload (used by tests).
        var agent = agentOverride ?? new EchoAgent(subject);

        // ── Build EvalInput from prompt(s) ───────────────────────────────────
        var resolvedPrompt = string.IsNullOrWhiteSpace(prompt) ? "Hello!" : prompt;
        var input = new EvalInput(
            Query: resolvedPrompt,
            Metadata: new Dictionary<string, object>
            {
                ["agent"] = agent,
                ["preset"] = preset,
            });

        // ── Run via the registry's EvaluateAsync adapter ─────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24));
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running perf benchmark ({preset}) for subject '{subject}'...");

        EvalResult result;
        try
        {
            result = await family.EvaluateAsync!(input, null, default);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Performance benchmark failed: {ex.Message}");
            return 1;
        }

        // ── Persist through the output store ─────────────────────────────────
        try
        {
            var manifest = await store.StartRunAsync(
                subjectIdentity,
                new RunContext(
                    EvalProject: "AgentEval.Evals.Performance",
                    EvalProjectPath: "src/AgentEval.Evals.Performance/",
                    Harness: "BenchPerfCommand",
                    Seed: null,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            var runId = manifest.Run.RunId;

            var scenarioResult = EvalResultPersistence.ToScenarioResult(
                result,
                scenarioId: $"perf-{preset.ToLowerInvariant()}",
                scenarioName: $"Performance — {preset}");
            await store.WriteScenarioResultAsync(runId, scenarioResult);

            var verdict = result.Score.Label.ToUpperInvariant() switch
            {
                "PASS" => "PASS",
                "WARN" => "WARN",
                _      => "FAIL"
            };
            var summary = new RunSummary(
                SchemaVersion: "1.0",
                RunId: runId,
                Verdict: verdict,
                Stats: new RunStats(
                    Total: 1,
                    Passed: result.Score.Passed ? 1 : 0,
                    Failed: !result.Score.Passed && result.Score.Label != "warn" ? 1 : 0,
                    Warnings: result.Score.Label == "warn" ? 1 : 0),
                Metrics: new Dictionary<string, double>
                {
                    ["overallScore"] = result.Score.Value,
                });
            await store.CompleteRunAsync(manifest, summary);
            Console.WriteLine($"Persisted run {runId} to {agentEvalDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist perf run to output store: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Overall result: composite verdict {result.Score.Label.ToUpperInvariant()} " +
            $"(score {result.Score.Value:F3})");

        return result.Score.Label.ToLowerInvariant() switch
        {
            "pass" => 0,
            "fail" => 2,
            _ => 2
        };
    }

    /// <summary>
    /// Deterministic echo agent used by the CLI when no real target is supplied. Each call
    /// returns the prompt itself with a synthetic 50 ms latency so the perf adapter
    /// produces meaningful (if uninteresting) metrics.
    /// </summary>
    internal sealed class EchoAgent : IEvaluableAgent
    {
        public string Name { get; }
        public EchoAgent(string name) { Name = name; }
        public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        {
            await Task.Delay(50, cancellationToken);
            return new AgentResponse
            {
                Text = prompt,
                TokenUsage = new TokenUsage { PromptTokens = prompt.Length / 4, CompletionTokens = prompt.Length / 4 }
            };
        }
    }
}
