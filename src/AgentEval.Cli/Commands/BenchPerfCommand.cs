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
        string? rootOverride,
        bool azureFromEnv = false,
        CancellationToken ct = default) =>
        RunAsync(preset, subject, prompt, rootOverride, agentOverride: null, azureFromEnv, mock: false, ct: ct);

    /// <summary>
    /// Internal overload exposed for tests; allows agent injection. <paramref name="azureFromEnv"/>
    /// builds a chat agent from the configured provider when <paramref name="agentOverride"/> is null.
    /// With neither, the command refuses (usage error) unless <paramref name="mock"/> asks for the
    /// stand-in by name (<c>--sut mock</c>); a mock run is labelled and not stored (see <see cref="MockTarget"/>).
    /// </summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? prompt,
        string? rootOverride,
        IEvaluableAgent? agentOverride,
        bool azureFromEnv = false,
        bool mock = false,
        string? agentModel = null,
        CancellationToken ct = default)
    {
        if (mock && (agentOverride is not null || azureFromEnv))
        {
            return MockTarget.RefuseMockWithRealTarget();
        }
        if (agentOverride is null && !azureFromEnv && !mock)
        {
            return MockTarget.RefuseWithoutTarget($"bench perf {preset}", MockTarget.AgentTargets);
        }

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
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init-workspace` first.");
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
            return ExitCodes.UsageError;
        }

        // ── Resolve target agent ─────────────────────────────────────────────
        // --sut <target> / --endpoint (agentOverride) > --azure-from-env > the stand-in, only when asked for.
        IEvaluableAgent agent;
        if (agentOverride is not null)
        {
            agent = agentOverride;
        }
        else if (azureFromEnv)
        {
            var (azureAgent, envModel, azureExitCode) = AzureChatAgentFactory.TryBuildFromEnvWithModel(subject);
            if (azureAgent is null) return azureExitCode;
            agent = azureAgent;
            agentModel = envModel;
        }
        else
        {
            MockTarget.PrintBanner($"bench perf {preset}", "a stand-in that echoes the prompt after 50 ms");
            agent = new MockTarget.EchoingAgent(subject);
        }

        // ── Build EvalInput from prompt(s) ───────────────────────────────────
        // P0-1: price the model the agent actually used: --model for an --endpoint target, the model the provider
        // resolved for --azure-from-env. Without it the cost leaf falls back to agent.Name, which never matches a
        // pricing entry.
        var resolvedPrompt = string.IsNullOrWhiteSpace(prompt) ? "Hello!" : prompt;
        var metadata = new Dictionary<string, object>
        {
            // 7.1: the WRITE side of the legacy convention, keyed off the same constant the four
            // families read, so a rename moves both ends together instead of compiling on one.
            [AgentEval.Evals.EvalInputAgentBinding.AgentMetadataKey] = agent,
            ["preset"] = preset,
        };
        if (!string.IsNullOrWhiteSpace(agentModel))
        {
            metadata["costModelName"] = agentModel;
        }
        var input = new EvalInput(
            Query: resolvedPrompt,
            Metadata: metadata);

        // ── Run via the registry's EvaluateAsync adapter ─────────────────────
        if (agent is MockTarget.EchoingAgent)
        {
            EvalResult mockResult;
            try
            {
                mockResult = await family.EvaluateAsync!(input, null, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Performance benchmark failed: {ex.Message}");
                return 1;
            }

            return MockTarget.Finish($"bench perf {preset}",
                $"{mockResult.Score.Label.ToUpperInvariant()} (score {mockResult.Score.Value:F3}) for the echo stand-in");
        }

        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running perf benchmark ({preset}) for subject '{subject}'...");

        EvalResult result;
        try
        {
            result = await family.EvaluateAsync!(input, null, ct);
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
                Stats: new[] { result.Score }.ToRunStats(),   // a skipped or errored result is not a failure (B8)
                Metrics: new Dictionary<string, double>
                {
                    ["overallScore"] = result.Score.Value,
                });
            await store.CompleteRunAsync(manifest, summary, ct);
            Console.WriteLine($"Persisted run {runId} to {agentEvalDir}");

            // T0.5 (v1.1): emit JSON + HTML + PDF reports alongside the canonical run.
            // Perf previously emitted nothing beyond the store; this brings it to parity
            // with the compliance commands. Reports land in the canonical run dir.
            // Resolve via the store so the path matches the manifest exactly — hand-building
            // it from the RAW subject diverges whenever FileSystemLayout.Sanitize rewrites the
            // name (BUG-17), orphaning the report or throwing on a non-existent directory.
            var runDir = store.ResolveRunDirectory(subjectIdentity, runId);
            try
            {
                var jsonPath = Path.Combine(runDir, "report.json");
                await File.WriteAllTextAsync(jsonPath, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"JSON report:     {jsonPath}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: failed to write perf JSON report: {ex.Message}");
            }
            var (htmlPath, pdfPath) = await GenericReportRenderer.WriteHtmlAndPdfAsync(
                result,
                runDir,
                subjectIdentity,
                benchmarkLabel: $"Performance — {preset}",
                runId: runId);
            if (htmlPath is not null) Console.WriteLine($"HTML report:     {htmlPath}");
            if (pdfPath is not null)  Console.WriteLine($"PDF report:      {pdfPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist perf run to output store: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"Overall result: composite verdict {result.Score.Label.ToUpperInvariant()} " +
            $"(score {result.Score.Value:F3})");

        // Reuse fix (BUG-22 follow-up): was an inlined duplicate of BenchExitCodes.FromLabel
        // (identical pass=>0/fail=>2/_=>2 mapping, now split 9/10/11 — see that class's own remarks).
        return BenchExitCodes.FromLabel(result.Score.Label);
    }
}
