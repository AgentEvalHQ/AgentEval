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
/// Implements the <c>agenteval bench owasp</c> subcommand. Runs the OWASP LLM Top 10
/// red-team scan against an agent (a stub safe-refusal agent is used by default —
/// supply an agent override on the internal overload for real targets in tests),
/// persists the resulting <see cref="EvalResult"/> through the unified output-store,
/// and additionally emits the rich <see cref="OWASPComplianceReport"/> as JSON +
/// Markdown alongside.
/// </summary>
public static class BenchOwaspCommand
{
    /// <summary>Runs the bench owasp command using auto-discovered workspace root.</summary>
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

    /// <summary>
    /// Runs the bench owasp command with optional overrides (used in tests).
    /// Returns the exit code plus the absolute path of the timestamped report
    /// directory (where <c>report.md</c> / <c>report.json</c> are written), or
    /// <c>null</c> if the command exited before reaching the report-write step.
    /// Tests use the returned path directly instead of enumerating timestamped
    /// directories by name — that name-based lookup races on second-precision
    /// timestamps when two operations land in the same second.
    /// When <paramref name="azureFromEnv"/> is true AND <paramref name="agentOverride"/>
    /// is null, builds an Azure OpenAI chat agent from <c>AZURE_OPENAI_*</c> env vars
    /// via <see cref="AzureChatAgentFactory"/>. When neither is provided, falls back to
    /// the built-in <c>SafeRefusalAgent</c> stub with a prominent warning banner
    /// explaining that results do not reflect a real agent.
    /// </summary>
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

        // ── Judge / evaluator ────────────────────────────────────────────────
        // The OWASP attack pipeline uses heuristic per-attack evaluators today;
        // the judge is accepted for API symmetry with other bench commands and
        // reserved for future LLM-graded probes. JudgeFactory.Resolve still runs
        // to honour the AZURE_OPENAI_* env gate (CI parity).
        var (resolvedJudge, judgeModelName, exitCode) = JudgeFactory.Resolve(
            evaluatorOverride,
            judgeKind: "OWASP benchmark");
        if (resolvedJudge is null) return (exitCode, null);

        // ── Select preset ────────────────────────────────────────────────────
        OwaspBenchmarkRun benchmark;
        try
        {
            benchmark = ResolvePreset(preset, resolvedJudge);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to build OWASP preset '{preset}': {ex.Message}");
            return (1, null);
        }

        // ── Resolve target agent ─────────────────────────────────────────────
        // The OWASP pipeline drives the agent itself (it generates and sends its
        // own probes); --input is accepted for symmetry with the other bench
        // commands and is recorded for provenance only — it is not consumed by
        // the scan flow. We reference it to keep the parameter meaningful.
        _ = inputText;
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
                benchmarkName: "OWASP LLM Top 10",
                stubAgentDescription: "SafeRefusalAgent stub",
                sampleFileName: "06_OwaspBenchmark.cs");
            agent = new SafeRefusalAgent(subject);
        }

        // ── Run benchmark ────────────────────────────────────────────────────
        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Agent, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running OWASP benchmark ({preset}) for subject '{subject}'...");

        EvalResult compositeEval;
        RedTeamResult redTeamResult;
        try
        {
            // Run the scan exactly once. The Phase-6 BuildEvalResult(RedTeamResult)
            // overload lets us shape the EvalResult composite from the existing
            // RedTeamResult and lets GenerateReport project the rich compliance
            // report from the same result — both derived from a single pipeline
            // execution. Closes the Phase-5 double-scan smell flagged in
            // lastreview/13-phase5-gate-review.md §6 (Anti-patterns).
            redTeamResult = await benchmark.ScanAsync(agent, ct);
            compositeEval = benchmark.BuildEvalResult(redTeamResult);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"OWASP scan failed: {ex.Message}");
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
                    Harness: "BenchOwaspCommand",
                    Seed: null,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            runId = manifest.Run.RunId;

            // Write the 10-leaf composite as the run's "scenario result" — the
            // recursive tree is preserved as JSON inside ScenarioResult.Output, so
            // a downstream reader can rehydrate the full EvalResult via
            // EvalResultPersistence.FromScenarioResult.
            var scenarioResult = EvalResultPersistence.ToScenarioResult(
                compositeEval,
                scenarioId: $"owasp-{preset.ToLowerInvariant()}",
                scenarioName: $"OWASP LLM Top 10 — {preset}");
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
            Console.Error.WriteLine($"Failed to persist OWASP run to output store: {ex.Message}");
            return (1, null);
        }

        // ── Persist the rich OWASP compliance evidence (sibling to GDPR/EU AI Act) ─
        try
        {
            var reporter = new OWASPComplianceReporter();
            await reporter.SaveReportAsync(store, subjectIdentity, runId, redTeamResult);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to persist OWASP compliance evidence: {ex.Message}");
        }

        // ── Emit JSON + Markdown reports alongside ───────────────────────────
        var sanitizedSubject = FileSystemLayout.Sanitize(subject);
        var ts = report.GeneratedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var outputDir = Path.Combine(agentEvalDir, "compliance", "OWASP-LLM-Top10", sanitizedSubject, ts);
        Directory.CreateDirectory(outputDir);

        try
        {
            var jsonPath = Path.Combine(outputDir, "report.json");
            await File.WriteAllTextAsync(jsonPath, report.ToJson());
            Console.WriteLine($"JSON report:     {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write OWASP JSON report: {ex.Message}");
        }

        try
        {
            var mdPath = Path.Combine(outputDir, "report.md");
            await File.WriteAllTextAsync(mdPath, report.ToMarkdown());
            Console.WriteLine($"Markdown report: {mdPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: failed to write OWASP Markdown report: {ex.Message}");
        }

        // T0.5 (v1.1): emit generic HTML + PDF alongside JSON/MD for parity with the
        // compliance commands (GDPR / EU AI Act / Agentic). Both are best-effort.
        var (htmlPath, pdfPath) = await GenericReportRenderer.WriteHtmlAndPdfAsync(
            compositeEval,
            outputDir,
            subjectIdentity,
            benchmarkLabel: "OWASP LLM Top 10",
            runId: runId);
        if (htmlPath is not null) Console.WriteLine($"HTML report:     {htmlPath}");
        if (pdfPath is not null)  Console.WriteLine($"PDF report:      {pdfPath}");

        // ── Exit code ─────────────────────────────────────────────────────────
        Console.WriteLine($"Overall result: pass rate {report.Summary.OverallPassRate:F1}% " +
            $"({report.Summary.CriticalFindings} critical / {report.Summary.HighFindings} high findings); " +
            $"composite verdict {compositeEval.Score.Label.ToUpperInvariant()}");

        var finalExit = compositeEval.Score.Label.ToLowerInvariant() switch
        {
            "pass" => 0,
            "fail" => 2,
            _      => 2  // WARN/skipped also returns non-zero for CI strictness
        };
        return (finalExit, outputDir);
    }

    /// <summary>
    /// Resolves an OWASP preset specification into an <see cref="OwaspBenchmarkRun"/>.
    /// </summary>
    /// <param name="presetSpec">
    /// Preset identifier: <c>top10</c>, <c>smoke</c>, <c>audit</c>/<c>auditgrade</c>, or
    /// <c>top10-rag</c>/<c>top10forrag</c>.
    /// </param>
    /// <param name="judge">LLM evaluator passed through to the preset factory.</param>
    internal static OwaspBenchmarkRun ResolvePreset(string presetSpec, IEvaluator judge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetSpec);
        ArgumentNullException.ThrowIfNull(judge);

        return presetSpec.Trim().ToLowerInvariant() switch
        {
            "top10"                          => OwaspBenchmark.Top10(judge),
            "smoke"                          => OwaspBenchmark.Smoke(judge),
            "audit" or "auditgrade"          => OwaspBenchmark.AuditGrade(judge),
            "top10-rag" or "top10forrag"     => OwaspBenchmark.Top10ForRag(judge),
            _ => throw new ArgumentException(
                $"Unknown OWASP preset '{presetSpec}'. " +
                "Known presets: top10, smoke, audit (=auditgrade), top10-rag (=top10forrag).",
                nameof(presetSpec))
        };
    }

    // SafeRefusalAgent hoisted to a shared internal type — see SafeRefusalAgent.cs (MNT-02).
}
