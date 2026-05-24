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
        bool azureFromEnv = false) =>
        RunAsync(preset, subject, prompt, rootOverride, agentOverride: null, azureFromEnv);

    /// <summary>
    /// Internal overload exposed for tests; allows agent injection. <paramref name="azureFromEnv"/>
    /// builds an Azure OpenAI chat agent from <c>AZURE_OPENAI_*</c> env vars when
    /// <paramref name="agentOverride"/> is null; otherwise falls back to the <c>EchoAgent</c> stub
    /// with a prominent warning banner.
    /// </summary>
    internal static async Task<int> RunAsync(
        string preset,
        string subject,
        string? prompt,
        string? rootOverride,
        IEvaluableAgent? agentOverride,
        bool azureFromEnv = false)
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
        // Default is the deterministic EchoAgent stub (50ms-delay echo). --azure-from-env
        // builds a real Azure OpenAI agent. Tests inject via agentOverride.
        IEvaluableAgent agent;
        if (agentOverride is not null)
        {
            agent = agentOverride;
        }
        else if (azureFromEnv)
        {
            var (azureAgent, azureExitCode) = AzureChatAgentFactory.TryBuildFromEnv(subject);
            if (azureAgent is null) return azureExitCode;
            agent = azureAgent;
        }
        else
        {
            AzureChatAgentFactory.PrintStubAgentWarning(
                benchmarkName: "Performance",
                stubAgentDescription: "EchoAgent stub",
                sampleFileName: "02_PerformanceBenchmark.cs");
            agent = new EchoAgent(subject);
        }

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

            // T0.5 (v1.1): emit JSON + HTML + PDF reports alongside the canonical run.
            // Perf previously emitted nothing beyond the store; this brings it to parity
            // with the compliance commands. Reports land in the canonical run dir.
            var runDir = Path.Combine(agentEvalDir, "subjects", "agents", subject, "runs", runId);
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
