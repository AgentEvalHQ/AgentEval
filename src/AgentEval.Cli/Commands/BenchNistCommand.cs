// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench nist</c> subcommand — parity with <see cref="BenchMitreCommand"/> /
/// <see cref="BenchOwaspCommand"/>. Runs the NIST AI RMF red-team scan against an agent (a stub safe-refusal agent by
/// default), persists the composite <see cref="EvalResult"/> through the unified output-store, and emits the rich
/// <see cref="NistAiRmfComplianceReport"/> as JSON + Markdown (+ HTML/PDF).
/// </summary>
public static class BenchNistCommand
{
    /// <summary>Runs the bench nist command using auto-discovered workspace root.</summary>
    public static async Task<int> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        bool azureFromEnv = false,
        CancellationToken ct = default)
    {
        var (exitCode, _) = await RunAsync(preset, subject, rootOverride, inputText, evaluatorOverride: null, agentOverride: null, azureFromEnv, ct).ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>Runs the bench nist command with optional overrides (used in tests). Returns the exit code plus the
    /// absolute path of the timestamped report directory (or null if it exited before the report-write step).</summary>
    internal static async Task<(int ExitCode, string? ReportDir)> RunAsync(
        string preset,
        string subject,
        string? rootOverride,
        string? inputText,
        IEvaluator? evaluatorOverride,
        IEvaluableAgent? agentOverride,
        bool azureFromEnv = false,
        CancellationToken ct = default)
    {
        // ── Workspace setup ──────────────────────────────────────────────────
        if (rootOverride is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(rootOverride);
            if (canonical is null) return (1, null);
            rootOverride = canonical;
        }
        var workspaceRoot = rootOverride ?? WorkspaceRootDiscovery.Find(Directory.GetCurrentDirectory());
        if (workspaceRoot is null)
        {
            Console.Error.WriteLine("Could not find a solution root (.sln, .slnx, or .git). " +
                "Provide --root or run from within a solution directory.");
            return (1, null);
        }

        var agentEvalDir = Path.Combine(workspaceRoot, ".agenteval");
        if (!Directory.Exists(agentEvalDir))
        {
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init` first.");
            return (1, null);
        }

        // ── Judge / evaluator (accepted for API symmetry; heuristic evaluators today) ──
        var (resolvedJudge, _, exitCode) = JudgeFactory.Resolve(evaluatorOverride, judgeKind: "NIST AI RMF benchmark");
        if (resolvedJudge is null) return (exitCode, null);

        // ── Select preset ────────────────────────────────────────────────────
        NistBenchmarkRun benchmark;
        try
        {
            benchmark = ResolvePreset(preset, resolvedJudge);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to build NIST AI RMF preset '{preset}': {ex.Message}");
            return (1, null);
        }

        // ── Resolve target agent ─────────────────────────────────────────────
        _ = inputText;   // recorded for provenance only — the pipeline generates its own probes
        IEvaluableAgent agent;
        if (agentOverride is not null)
        {
            agent = agentOverride;
        }
        else if (azureFromEnv)
        {
            var (azureAgent, azureExitCode) = AzureChatAgentFactory.TryBuildFromEnv(subject);
            if (azureAgent is null) return (azureExitCode, null);
            agent = azureAgent;
        }
        else
        {
            AzureChatAgentFactory.PrintStubAgentWarning(
                benchmarkName: "NIST AI RMF",
                stubAgentDescription: "SafeRefusalAgent stub",
                sampleFileName: "08_NistBenchmark.cs");
            agent = new SafeRefusalAgent(subject);
        }

        // ── Run benchmark ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running NIST AI RMF benchmark ({preset}) for subject '{subject}'...");

        EvalResult compositeEval;
        RedTeamResult redTeamResult;
        try
        {
            redTeamResult = await benchmark.ScanAsync(agent, ct);
            compositeEval = benchmark.BuildEvalResult(redTeamResult);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NIST AI RMF scan failed: {ex.Message}");
            return (1, null);
        }

        var report = benchmark.GenerateReport(redTeamResult);

        // ── Persist through the unified output-store ─────────────────────────
        string runId;
        try
        {
            var manifest = await store.StartRunAsync(
                subjectIdentity,
                new RunContext(
                    EvalProject: "AgentEval.RedTeam",
                    EvalProjectPath: "src/AgentEval.RedTeam/",
                    Harness: "BenchNistCommand",
                    Seed: null,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            runId = manifest.Run.RunId;

            var scenarioResult = EvalResultPersistence.ToScenarioResult(
                compositeEval,
                scenarioId: $"nist-{preset.ToLowerInvariant()}",
                scenarioName: $"NIST AI RMF — {preset}");
            await store.WriteScenarioResultAsync(runId, scenarioResult);

            var verdict = compositeEval.Score.Label.ToUpperInvariant() switch
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
                    Passed: compositeEval.Score.Passed ? 1 : 0,
                    Failed: !compositeEval.Score.Passed && compositeEval.Score.Label != "warn" ? 1 : 0,
                    Warnings: compositeEval.Score.Label == "warn" ? 1 : 0),
                Metrics: new Dictionary<string, double>
                {
                    ["overallScore"] = compositeEval.Score.Value,
                    ["overallPassRate"] = report.Summary.OverallPassRate / 100.0,
                });
            await store.CompleteRunAsync(manifest, summary, ct);
            Console.WriteLine($"Persisted run {runId} to {agentEvalDir}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist NIST AI RMF run to output store: {ex.Message}");
            return (1, null);
        }

        // ── Persist the rich NIST compliance evidence ────────────────────────
        try
        {
            var reporter = new NistAiRmfComplianceReporter();
            await reporter.SaveReportAsync(store, subjectIdentity, runId, redTeamResult);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to persist NIST AI RMF compliance evidence: {ex.Message}");
        }

        // ── Emit JSON + Markdown reports alongside ───────────────────────────
        var sanitizedSubject = FileSystemLayout.Sanitize(subject);
        var ts = report.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var outputDir = Path.Combine(agentEvalDir, "compliance", "NIST-AI-RMF", sanitizedSubject, ts);
        Directory.CreateDirectory(outputDir);

        try
        {
            var jsonPath = Path.Combine(outputDir, "report.json");
            await File.WriteAllTextAsync(jsonPath, report.ToJson());
            Console.WriteLine($"JSON report:     {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write NIST AI RMF JSON report: {ex.Message}");
        }

        try
        {
            var mdPath = Path.Combine(outputDir, "report.md");
            await File.WriteAllTextAsync(mdPath, report.ToMarkdown());
            Console.WriteLine($"Markdown report: {mdPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write NIST AI RMF Markdown report: {ex.Message}");
        }

        var (htmlPath, pdfPath) = await GenericReportRenderer.WriteHtmlAndPdfAsync(
            compositeEval, outputDir, subjectIdentity, benchmarkLabel: "NIST AI RMF", runId: runId);
        if (htmlPath is not null) Console.WriteLine($"HTML report:     {htmlPath}");
        if (pdfPath is not null)  Console.WriteLine($"PDF report:      {pdfPath}");

        // ── Exit code ─────────────────────────────────────────────────────────
        Console.WriteLine($"Overall result: pass rate {report.Summary.OverallPassRate:F1}% " +
            $"({report.Summary.CriticalFindings} needs-improvement / {report.Summary.HighFindings} partially-effective); " +
            $"composite verdict {compositeEval.Score.Label.ToUpperInvariant()}");

        var finalExit = BenchExitCodes.FromLabel(compositeEval.Score.Label);  // pass → 0, fail/warn/skipped → 2
        return (finalExit, outputDir);
    }

    /// <summary>Resolves a NIST AI RMF preset spec into a <see cref="NistBenchmarkRun"/>.</summary>
    internal static NistBenchmarkRun ResolvePreset(string presetSpec, IEvaluator judge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);
        ArgumentNullException.ThrowIfNull(judge);

        return presetSpec.Trim().ToLowerInvariant() switch
        {
            "rmf-baseline" or "rmfbaseline" or "baseline"             => NistBenchmark.RmfBaseline(judge),
            "rmf-smoke" or "rmfsmoke" or "smoke"                      => NistBenchmark.RmfSmoke(judge),
            "rmf-audit-grade" or "rmf-audit" or "rmfauditgrade"
                or "audit" or "auditgrade"                            => NistBenchmark.RmfAuditGrade(judge),
            _ => throw new ArgumentException(
                $"Unknown NIST AI RMF preset '{presetSpec}'. " +
                "Known presets: rmf-baseline (=baseline), rmf-smoke (=smoke), rmf-audit-grade (=rmf-audit / audit / auditgrade).",
                nameof(presetSpec))
        };
    }
}
