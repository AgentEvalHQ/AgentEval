// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Benchmarks;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Results.Adapters.Native;
using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements the <c>agenteval bench mitre</c> subcommand. Runs the MITRE ATLAS
/// red-team scan against the named target (<c>--sut</c>, <c>--endpoint</c>/<c>--model</c> or
/// <c>--from-env</c>; without one it refuses, see <see cref="MockTarget"/>),
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
        CancellationToken ct = default,
        DirectoryInfo? aef = null,
        bool noAef = false,
        string? subjectVersion = null,
        bool agentIsLive = false, string? agentEndpoint = null)
    {
        // agentIsLive: the caller resolved agentOverride from --sut or --endpoint, a real target. Any other agentOverride
        // is a stand-in, which the AEF run records as mocked, never live (RUN-7).
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
        string? judgeProvider = null;
        if (judgeClient is null && !mock && evaluatorOverride is null)
        {
            var (client, model, judgeExit, provider) = JudgeFactory.ResolveChatClientWithProvider("MITRE ATLAS benchmark");
            if (client is null) return (judgeExit, null);
            judgeClient = client;
            judgeModelName = model;
            judgeProvider = provider;
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
            var (azureAgent, envModel, azureExitCode, envEndpoint) = AzureChatAgentFactory.TryBuildFromEnvWithEndpoint(subject);
            if (azureAgent is null) return (azureExitCode, null);
            agent = azureAgent;
            agentModel = envModel;
            agentEndpoint = envEndpoint;
        }
        else
        {
            MockTarget.PrintBanner("bench mitre", "a stand-in that refuses every request", aefRequested: aef is not null && !noAef);
            agent = new MockTarget.RefusingAgent(subject);
        }
        var isMock = agent is MockTarget.RefusingAgent;
        Func<IReadOnlyList<AefComplianceArtifact>>? complianceArtifacts = null;

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

        var evidenceFinalized = false;
        string? storeRunId = null;
        AgentEval.Results.Adapters.Native.AefStoreV1Link? StoreLink() =>
            storeRunId is null ? null : new() { RunId = storeRunId };
        EvalResult compositeEval;
        RedTeamResult redTeamResult;
        var partialScan = new RedTeamAef.PartialScan(agent.Name);
        benchmark.Pipeline.OnProbeCompleted(partialScan.Add).BeginProbeScope(RedTeamAef.BeginProbeScope);
        AgentEval.Results.Suites.SuiteDefinitionManifest? suiteDefinition = null;
        if ((!isMock || aef is not null) && !noAef)
            benchmark.Pipeline.OnPlanPrepared(plan => suiteDefinition = AgentEval.Cli.Infrastructure.ProbeSuiteDefinition.ForBenchmark(
                "mitre", preset, benchmark.PresetName, benchmark.GetType(), plan, judgeModelName, judgeProvider));
        try
        {
            // Single-scan pattern (Phase 6): one pipeline execution; the
            // RedTeamResult feeds both BuildEvalResult and GenerateReport.
            redTeamResult = await benchmark.ScanAsync(agent, ct);
            ct.ThrowIfCancellationRequested();
            compositeEval = benchmark.BuildEvalResult(redTeamResult);
        }
        catch (Exception ex)
        {
            var abortReason = ex is OperationCanceledException && ct.IsCancellationRequested ? "cancelled" : "execution-error";
            if (abortReason != "cancelled") Console.Error.WriteLine($"MITRE ATLAS scan failed: {ex.Message}");
            if ((!isMock || aef is not null) && !noAef && BenchAef.WriteScan(aef, agentEvalDir, "mitre", preset, subject, subjectVersion,
                !isMock && (agentOverride is null || agentIsLive) ? AgentEval.Results.Writing.AefTargetMode.Live : AgentEval.Results.Writing.AefTargetMode.Mocked,
                judgeModelName, partialScan.Snapshot(), null, 1, judgeLedger, judgeProvider: judgeProvider, storeV1: StoreLink(), endpoint: agentOverride is null || agentIsLive ? agentEndpoint : null, abortReason: abortReason, definition: suiteDefinition))
                return (ExitCodes.RuntimeError, null);
            return (1, null);
        }

        try
        {
            var report = benchmark.GenerateReport(redTeamResult);
            if (isMock)
            {
                if (aef is not null && !noAef && BenchAef.WriteScan(aef, agentEvalDir, "mitre", preset, subject, subjectVersion,
                    AgentEval.Results.Writing.AefTargetMode.Mocked, judgeModelName, redTeamResult, compositeEval,
                    ExitCodes.GateIndeterminate, judgeLedger, definition: suiteDefinition))
                    return (ExitCodes.RuntimeError, null);
                return (MockTarget.Finish("bench mitre",
                    $"{compositeEval.Score.Label.ToUpperInvariant()} (score {compositeEval.Score.Value:F3}) for a stand-in that refuses every request", aefWritten: aef is not null && !noAef), null);
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
                storeRunId = runId;

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
                // Not measured is never a score (T13, T81): a composite that measured nothing writes no overallScore, and a
                // scan with no conclusive probe no overallPassRate — the verdict above already says PENDING or WARN.
                var metrics = new Dictionary<string, double>();
                if (compositeEval.Score.MeasuredValue() is { } overallScore) metrics["overallScore"] = overallScore;
                if (report.Summary.OverallPassRate is { } overallPassRate) metrics["overallPassRate"] = overallPassRate / 100.0;
                var summary = new RunSummary(
                    SchemaVersion: "1.0",
                    RunId: runId,
                    Verdict: verdict,
                    Stats: runStats,
                    Metrics: metrics);
                await store!.CompleteRunAsync(manifest, summary, ct);
                Console.WriteLine($"Persisted run {runId} to {agentEvalDir}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Failed to persist MITRE ATLAS run to output store: {ex.Message}");
                throw;
            }

            // ── Persist the rich MITRE compliance evidence (sibling to GDPR/EU AI Act/OWASP) ─
            try
            {
                var reporter = new MITREATLASReporter();
                var complianceEvidence = await reporter.SaveReportWithEvidenceAsync(store!, subjectIdentity, runId, redTeamResult,
                    new ComplianceReportOptions { IncompleteReason = incomplete ? string.Join("; ", incompleteReasons) : null });
                complianceArtifacts = () => ComplianceAef.Scan(agentEvalDir, complianceEvidence, redTeamResult);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"Warning: failed to write MITRE ATLAS JSON report: {ex.Message}");
            }

            try
            {
                var mdPath = Path.Combine(outputDir, "report.md");
                await File.WriteAllTextAsync(mdPath, report.ToMarkdown());
                Console.WriteLine($"Markdown report: {mdPath}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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

            ct.ThrowIfCancellationRequested();

            var finalExit = indeterminate ? ExitCodes.GateIndeterminate : BenchExitCodes.FromLabel(compositeEval.Score.Label);

            // ── AEF ───────────────────────────────────────────────────────────────
            // The scan's probes as sealed evidence, lane mitre; a mock run is not stored, so it writes none.
            if ((!isMock || aef is not null) && !noAef && BenchAef.WriteScan(aef, agentEvalDir, "mitre", preset, subject, subjectVersion,
                    !isMock && (agentOverride is null || agentIsLive) ? AgentEval.Results.Writing.AefTargetMode.Live : AgentEval.Results.Writing.AefTargetMode.Mocked,
                    judgeModelName, redTeamResult, compositeEval, finalExit, judgeLedger, judgeProvider: judgeProvider, storeV1: StoreLink(), endpoint: agentOverride is null || agentIsLive ? agentEndpoint : null, complianceArtifacts: complianceArtifacts, definition: suiteDefinition))
                return (ExitCodes.RuntimeError, null);   // an explicit --aef could not be written

            // ── Exit code ─────────────────────────────────────────────────────────
            evidenceFinalized = true;
            Console.WriteLine($"Overall result: pass rate {(report.Summary.OverallPassRate is { } passRate ? $"{passRate:F1}%" : "not measured (no probe reached a verdict)")} " +
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

            return (finalExit, outputDir);
        }
        catch (Exception ex)
        {
            // A later presentation failure must not create a second, aborted run.
            if (evidenceFinalized) return (1, null);
            var abortReason = ex is OperationCanceledException && ct.IsCancellationRequested ? "cancelled" : "execution-error";
            // The scan may finish before persistence or report generation is cancelled.
            if ((!isMock || aef is not null) && !noAef && BenchAef.WriteScan(aef, agentEvalDir, "mitre", preset, subject, subjectVersion,
                !isMock && (agentOverride is null || agentIsLive) ? AgentEval.Results.Writing.AefTargetMode.Live : AgentEval.Results.Writing.AefTargetMode.Mocked,
                judgeModelName, redTeamResult, null, 1, judgeLedger, judgeProvider: judgeProvider, storeV1: StoreLink(), endpoint: agentOverride is null || agentIsLive ? agentEndpoint : null, abortReason: abortReason, complianceArtifacts: complianceArtifacts, definition: suiteDefinition))
                return (ExitCodes.RuntimeError, null);
            return (1, null);
        }
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
