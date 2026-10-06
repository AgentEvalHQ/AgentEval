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
/// Implements the <c>agenteval bench mitre</c> subcommand. Runs the MITRE ATLAS
/// red-team scan against the named target (<c>--sut</c>, <c>--endpoint</c>/<c>--model</c> or
/// <c>--azure-from-env</c>; without one it refuses, see <see cref="MockTarget"/>),
/// persists the resulting <see cref="EvalResult"/> through the unified output-store,
/// and additionally emits the rich <see cref="MITREATLASReport"/> as JSON +
/// Markdown alongside.
/// </summary>
/// <remarks>
/// Symmetric to <see cref="BenchOwaspCommand"/>. Uses the Phase-6 single-scan
/// pattern: one <see cref="MitreBenchmarkRun.ScanAsync"/> per CLI invocation;
/// the resulting <see cref="RedTeamResult"/> is fed into both
/// <see cref="MitreBenchmarkRun.BuildEvalResult"/> and
/// <see cref="MitreBenchmarkRun.GenerateReport"/>.
/// </remarks>
public static class BenchMitreCommand
{
    /// <summary>Runs the bench mitre command using auto-discovered workspace root.</summary>
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

    /// <summary>
    /// Runs the bench mitre command with optional overrides (used in tests).
    /// Returns the exit code plus the absolute path of the timestamped report
    /// directory (where <c>report.md</c> / <c>report.json</c> are written), or
    /// <c>null</c> if the command exited before reaching the report-write step.
    /// Tests use the returned path directly instead of enumerating timestamped
    /// directories by name — that name-based lookup races on second-precision
    /// timestamps when two operations land in the same second.
    /// When <paramref name="azureFromEnv"/> is true AND <paramref name="agentOverride"/>
    /// is null, builds a chat agent from the configured provider via
    /// <see cref="AzureChatAgentFactory"/>. With neither, the command refuses (usage error)
    /// unless <paramref name="mock"/> asks for the stand-in by name (<c>--sut mock</c>);
    /// a mock run is labelled and not stored (see <see cref="MockTarget"/>).
    /// </summary>
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
            return (MockTarget.RefuseWithoutTarget("bench mitre", MockTarget.AgentTargets), null);
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
            var (client, model, judgeExit) = JudgeFactory.ResolveChatClient("MITRE ATLAS benchmark");
            if (client is null) return (judgeExit, null);
            judgeClient = client;
            judgeModelName = model;
        }

        // ── Select preset ────────────────────────────────────────────────────
        MitreBenchmarkRun benchmark;
        try
        {
            benchmark = ResolvePreset(preset, evaluatorOverride);
        }
        catch (ArgumentException ex)
        {
            // An unknown preset or domain pack is a rejected argument: a usage error, not a failed run.
            Console.Error.WriteLine($"Failed to build MITRE ATLAS preset '{preset}': {ex.Message}");
            return (ExitCodes.UsageError, null);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to build MITRE ATLAS preset '{preset}': {ex.Message}");
            return (1, null);
        }

        JudgeCallLedger? judgeLedger = null;
        if (judgeClient is not null)
        {
            judgeLedger = new JudgeCallLedger(judgeClient);
            benchmark.WithJudge(judgeLedger, judgeModelName!);
        }

        // ── Resolve target agent ─────────────────────────────────────────────
        // The MITRE pipeline drives the agent itself (it generates and sends its
        // own probes); --input is recorded for provenance only — not consumed by
        // the scan flow. Reference it so the parameter stays meaningful.
        _ = inputText;
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
            MockTarget.PrintBanner("bench mitre", "a stand-in that refuses every request");
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

        Console.WriteLine($"{(isMock ? "MOCK RUN: " : "")}Running MITRE ATLAS benchmark ({preset}) for subject '{subject}'...");

        EvalResult compositeEval;
        RedTeamResult redTeamResult;
        try
        {
            // Single-scan pattern (Phase 6): one pipeline execution; the
            // RedTeamResult feeds both BuildEvalResult and GenerateReport.
            redTeamResult = await benchmark.ScanAsync(agent, ct);
            compositeEval = benchmark.BuildEvalResult(redTeamResult);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MITRE ATLAS scan failed: {ex.Message}");
            return (1, null);
        }

        var report = benchmark.GenerateReport(redTeamResult);
        if (isMock)
        {
            return (MockTarget.Finish("bench mitre",
                $"{compositeEval.Score.Label.ToUpperInvariant()} (score {compositeEval.Score.Value:F3}) for a stand-in that refuses every request"), null);
        }

        var incompleteReasons = new List<string>();
        if (judgeLedger is { Failures: > 0 } ledger)
        {
            incompleteReasons.Add($"the judge failed {ledger.Failures} of {ledger.Calls} grading calls");
        }
        if (redTeamResult.WasTruncated)
        {
            incompleteReasons.Add(ComplianceReportOptions.TruncatedIncompleteReason);
        }
        var incomplete = incompleteReasons.Count > 0;
        if (incomplete)   // report.md / report.json say so too, not "✅ Strong security posture" (B10ay)
            report = benchmark.GenerateReport(redTeamResult, string.Join("; ", incompleteReasons));
        // An incomplete run is never a pass: its composite must not be stored or rendered as PASS (B10ak). It is
        // indeterminate unless what it measured already fails it (B10ap).
        compositeEval = IncompleteRunPolicy.Withhold(compositeEval, incompleteReasons);
        var indeterminate = IncompleteRunPolicy.IsIndeterminate(compositeEval, incompleteReasons);

        // ── Persist through the unified output-store ─────────────────────────
        string runId;
        try
        {
            var manifest = await store!.StartRunAsync(
                subjectIdentity,
                new RunContext(
                    EvalProject: "AgentEval.RedTeam",
                    EvalProjectPath: "src/AgentEval.RedTeam/",
                    Harness: "BenchMitreCommand",
                    Seed: null,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            runId = manifest.Run.RunId;

            // Write the composite as the run's "scenario result" — the recursive
            // tree is preserved as JSON inside ScenarioResult.Output, so a
            // downstream reader can rehydrate the full EvalResult via
            // EvalResultPersistence.FromScenarioResult.
            var scenarioResult = EvalResultPersistence.ToScenarioResult(
                compositeEval,
                scenarioId: $"mitre-{preset.ToLowerInvariant()}",
                scenarioName: $"MITRE ATLAS — {preset}",
                subjectModel: agentModel);
            await store!.WriteScenarioResultAsync(runId, scenarioResult);

            var runStats = new[] { compositeEval.Score }.ToRunStats();   // a skipped or errored result is not a failure (B8)
            var verdict = indeterminate ? "WARN" : compositeEval.Score.RunVerdict(runStats);   // nor a FAIL verdict (B9b)
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
            Console.Error.WriteLine($"Failed to persist MITRE ATLAS run to output store: {ex.Message}");
            return (1, null);
        }

        // ── Persist the rich MITRE compliance evidence (sibling to GDPR/EU AI Act/OWASP) ─
        try
        {
            var reporter = new MITREATLASReporter();
            await reporter.SaveReportAsync(store!, subjectIdentity, runId, redTeamResult,
                new ComplianceReportOptions { IncompleteReason = incomplete ? string.Join("; ", incompleteReasons) : null });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to persist MITRE ATLAS compliance evidence: {ex.Message}");
        }

        // ── Emit JSON + Markdown reports alongside ───────────────────────────
        var sanitizedSubject = FileSystemLayout.Sanitize(subject);
        var ts = report.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var outputDir = Path.Combine(agentEvalDir, "compliance", "MITRE-ATLAS", sanitizedSubject, ts);
        Directory.CreateDirectory(outputDir);

        try
        {
            var jsonPath = Path.Combine(outputDir, "report.json");
            await File.WriteAllTextAsync(jsonPath, report.ToJson());
            Console.WriteLine($"JSON report:     {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write MITRE ATLAS JSON report: {ex.Message}");
        }

        try
        {
            var mdPath = Path.Combine(outputDir, "report.md");
            await File.WriteAllTextAsync(mdPath, report.ToMarkdown());
            Console.WriteLine($"Markdown report: {mdPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write MITRE ATLAS Markdown report: {ex.Message}");
        }

        // T0.5 (v1.1): emit generic HTML + PDF alongside JSON/MD for parity.
        var (htmlPath, pdfPath) = await GenericReportRenderer.WriteHtmlAndPdfAsync(
            compositeEval,
            outputDir,
            subjectIdentity,
            benchmarkLabel: "MITRE ATLAS",
            runId: runId);
        if (htmlPath is not null) Console.WriteLine($"HTML report:     {htmlPath}");
        if (pdfPath is not null)  Console.WriteLine($"PDF report:      {pdfPath}");

        // ── Exit code ─────────────────────────────────────────────────────────
        Console.WriteLine($"Overall result: pass rate {report.Summary.OverallPassRate:F1}% " +
            $"({report.Summary.CriticalFindings} critical / {report.Summary.HighFindings} high findings); " +
            $"composite verdict {compositeEval.Score.Label.ToUpperInvariant()}");

        if (indeterminate)
        {
            // A judge that failed, or a scan that ran out of time, leaves categories ungraded; the composite above
            // cannot say pass or fail. Stored as WARN, the schema's indeterminate value.
            Console.WriteLine($"INCOMPLETE: {string.Join("; ", incompleteReasons)}. This run is neither a pass nor a fail.");
            return (ExitCodes.GateIndeterminate, outputDir);
        }
        if (incomplete)   // a measured failure stands whatever the unmeasured part would show (B10ap): FAIL, exit 9
            Console.WriteLine($"INCOMPLETE: {string.Join("; ", incompleteReasons)}. What was measured already fails the run.");

        var finalExit = BenchExitCodes.FromLabel(compositeEval.Score.Label);  // pass → 0, fail → 9 (GateFailed), warn → 10 (GateWarning), skipped → 11 (GateIndeterminate) — BUG-22
        return (finalExit, outputDir);
    }

    /// <summary>
    /// Resolves a MITRE ATLAS preset specification into a <see cref="MitreBenchmarkRun"/>.
    /// </summary>
    /// <param name="presetSpec">
    /// Preset identifier (case-insensitive): <c>atlas-baseline</c>/<c>atlasbaseline</c>/<c>baseline</c>,
    /// <c>atlas-smoke</c>/<c>atlassmoke</c>/<c>smoke</c>, or
    /// <c>atlas-audit-grade</c>/<c>atlas-audit</c>/<c>atlasauditgrade</c>/<c>audit</c>/<c>auditgrade</c>.
    /// </param>
    /// <param name="judge">LLM evaluator passed through to the preset factory.</param>
    internal static MitreBenchmarkRun ResolvePreset(string presetSpec, IEvaluator? judge = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);

        return presetSpec.Trim().ToLowerInvariant() switch
        {
            "atlas-baseline" or "atlasbaseline" or "baseline"                     => MitreBenchmark.AtlasBaseline(judge),
            "atlas-smoke" or "atlassmoke" or "smoke"                              => MitreBenchmark.AtlasSmoke(judge),
            "atlas-audit-grade" or "atlas-audit" or "atlasauditgrade"
                or "audit" or "auditgrade"                                        => MitreBenchmark.AtlasAuditGrade(judge),
            _ => throw new ArgumentException(
                $"Unknown MITRE ATLAS preset '{presetSpec}'. " +
                "Known presets: atlas-baseline (=baseline), atlas-smoke (=smoke), " +
                "atlas-audit-grade (=atlas-audit / audit / auditgrade).",
                nameof(presetSpec))
        };
    }
}
