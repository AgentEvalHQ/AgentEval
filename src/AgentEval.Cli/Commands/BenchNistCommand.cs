// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench nist</c> subcommand — parity with <see cref="BenchMitreCommand"/> /
/// <see cref="BenchOwaspCommand"/>. Runs the NIST AI RMF red-team scan against the named target (without one it
/// refuses, see <see cref="MockTarget"/>), persists the composite <see cref="EvalResult"/> through the unified output-store, and emits the rich
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
        var (exitCode, _) = await RunAsync(preset, subject, rootOverride, inputText, evaluatorOverride: null, agentOverride: null, azureFromEnv, mock: false, ct: ct).ConfigureAwait(false);
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
        bool mock = false,
        IChatClient? judgeClientOverride = null,
        string? agentModel = null,
        CancellationToken ct = default)
    {
        if (mock && (agentOverride is not null || azureFromEnv))
        {
            return (MockTarget.RefuseMockWithRealTarget(), null);
        }
        if (agentOverride is null && !azureFromEnv && !mock)
        {
            return (MockTarget.RefuseWithoutTarget("bench nist", MockTarget.AgentTargets), null);
        }

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
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init-workspace` first.");
            return (1, null);
        }

        // ── Judge ────────────────────────────────────────────────────────────
        // The attacks are graded judge first, as `agenteval redteam --judge` grades them: the judge model the
        // environment configures (AZURE_OPENAI_JUDGE_*, else the provider AI_INFERENCE_PROVIDER selects), with the
        // keyword oracles as the fallback. A mock run reads no environment and grades with the oracles alone, as does
        // a caller that supplies its own evaluator and no judge client (the test seam).
        IChatClient? judgeClient = judgeClientOverride;
        string? judgeModelName = judgeClientOverride is null ? null : "override";
        if (judgeClient is null && !mock && evaluatorOverride is null)
        {
            var (client, model, judgeExit) = JudgeFactory.ResolveChatClient("NIST AI RMF benchmark");
            if (client is null) return (judgeExit, null);
            judgeClient = client;
            judgeModelName = model;
        }

        // ── Select preset ────────────────────────────────────────────────────
        NistBenchmarkRun benchmark;
        try
        {
            benchmark = ResolvePreset(preset, evaluatorOverride);
        }
        catch (ArgumentException ex)
        {
            // An unknown preset or domain pack is a rejected argument: a usage error, not a failed run.
            Console.Error.WriteLine($"Failed to build NIST AI RMF preset '{preset}': {ex.Message}");
            return (ExitCodes.UsageError, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to build NIST AI RMF preset '{preset}': {ex.Message}");
            return (1, null);
        }

        JudgeCallLedger? judgeLedger = null;
        if (judgeClient is not null)
        {
            judgeLedger = new JudgeCallLedger(judgeClient);
            benchmark.WithJudge(judgeLedger, judgeModelName!);
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
            var (azureAgent, envModel, azureExitCode) = AzureChatAgentFactory.TryBuildFromEnvWithModel(subject);
            if (azureAgent is null) return (azureExitCode, null);
            agent = azureAgent;
            agentModel = envModel;
        }
        else
        {
            MockTarget.PrintBanner("bench nist", "a stand-in that refuses every request");
            agent = new MockTarget.RefusingAgent(subject);
        }
        var isMock = agent is MockTarget.RefusingAgent;

        // One cheap call before the scan: a judge that cannot answer (a wrong key, deployment or quota) stops the run
        // here instead of turning every semantic probe into "inconclusive" and the composite into a pass.
        if (judgeLedger is not null && await judgeLedger.PreflightAsync(ct).ConfigureAwait(false) is { } judgeDown)
        {
            Console.Error.WriteLine(
                $"Error: the judge ({judgeModelName}) did not answer a test call, so the attacks cannot be graded: {judgeDown}");
            return (ExitCodes.RuntimeError, null);
        }

        // ── Run benchmark ────────────────────────────────────────────────────
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        FileSystemOutputStore? store = null;
        if (!isMock)
        {
            store = new FileSystemOutputStore(agentEvalDir);
            await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
            await store.EnsureSolutionAsync();
            await store.EnsureSubjectAsync(subjectIdentity);
        }

        Console.WriteLine($"{(isMock ? "MOCK RUN: " : "")}Running NIST AI RMF benchmark ({preset}) for subject '{subject}'...");

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
        if (isMock)
        {
            return (MockTarget.Finish("bench nist",
                $"{compositeEval.Score.Label.ToUpperInvariant()} (score {compositeEval.Score.Value:F3}) for a stand-in that refuses every request"), null);
        }

        var incompleteReasons = new List<string>();
        if (judgeLedger is { Failures: > 0 } ledger)
        {
            incompleteReasons.Add($"the judge failed {ledger.Failures} of {ledger.Calls} grading calls");
        }
        if (redTeamResult.WasTruncated)
        {
            incompleteReasons.Add("the scan ran out of time before every probe ran");
        }
        var incomplete = incompleteReasons.Count > 0;
        // An incomplete run is neither a pass nor a fail: its composite must not be stored or rendered as PASS (B10ak).
        compositeEval = IncompleteRunPolicy.Withhold(compositeEval, incompleteReasons);

        // ── Persist through the unified output-store ─────────────────────────
        string runId;
        try
        {
            var manifest = await store!.StartRunAsync(
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
                scenarioName: $"NIST AI RMF — {preset}",
                subjectModel: agentModel);
            await store!.WriteScenarioResultAsync(runId, scenarioResult);

            var runStats = new[] { compositeEval.Score }.ToRunStats();   // a skipped or errored result is not a failure (B8)
            var verdict = incomplete ? "WARN" : compositeEval.Score.RunVerdict(runStats);   // nor a FAIL verdict (B9b)
            var summary = new RunSummary(
                SchemaVersion: "1.0",
                RunId: runId,
                Verdict: verdict,
                Stats: runStats,
                Metrics: new Dictionary<string, double>
                {
                    ["overallScore"] = compositeEval.Score.Value,
                    ["overallPassRate"] = report.Summary.OverallPassRate / 100.0,
                });
            await store!.CompleteRunAsync(manifest, summary, ct);
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
            await reporter.SaveReportAsync(store!, subjectIdentity, runId, redTeamResult,
                new ComplianceReportOptions { IncompleteReason = incomplete ? string.Join("; ", incompleteReasons) : null });
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

        if (incomplete)
        {
            // A judge that failed, or a scan that ran out of time, leaves categories ungraded; the composite above
            // cannot say pass or fail. Stored as WARN, the schema's indeterminate value.
            Console.WriteLine($"INCOMPLETE: {string.Join("; ", incompleteReasons)}. This run is neither a pass nor a fail.");
            return (ExitCodes.GateIndeterminate, outputDir);
        }

        var finalExit = BenchExitCodes.FromLabel(compositeEval.Score.Label);  // pass → 0, fail → 9 (GateFailed), warn → 10 (GateWarning), skipped → 11 (GateIndeterminate) — BUG-22
        return (finalExit, outputDir);
    }

    /// <summary>Resolves a NIST AI RMF preset spec into a <see cref="NistBenchmarkRun"/>.</summary>
    internal static NistBenchmarkRun ResolvePreset(string presetSpec, IEvaluator? judge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);

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
