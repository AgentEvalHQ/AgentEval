// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Tracing;

// AgentEval.Output also declares an AgentTrace record; this handler works with the Tracing class model.
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Implements <c>agenteval bench workflow-trace-fidelity</c> (Glass Box, Phase 3) — reconciles each
/// executor's framework-reported per-executor ledger (summed tokens + finish reason) against chat-boundary
/// truth carried in the workflow trace's <c>ExecutorTraces</c>, and writes the EvalResult tree through the
/// canonical output store (manifest + report-native.json), like the other Shape-B bench handlers.
/// </summary>
public static class BenchWorkflowTraceFidelityCommand
{
    /// <summary>Runs the reconciliation. Returns 0 (clean, PASS), 10 (minor discrepancies or partly checked, WARN), 9
    /// (discrepancies, FAIL), 11 (nothing checked: no verdict, stored PENDING) or 1 (setup/IO error).</summary>
    public static async Task<int> RunAsync(
        string workflowTraceFile, string preset, string subject, string? rootOverride, CancellationToken ct = default)
    {
        // ── Workspace setup (mirrors BenchTraceFidelityCommand) ──
        if (rootOverride is not null)
        {
            var canonical = WorkspaceRootValidator.CanonicaliseOrNull(rootOverride);
            if (canonical is null)
            {
                return 1;
            }

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
            Console.Error.WriteLine($".agenteval/ not found at {agentEvalDir}. Run `agenteval init-workspace` first.");
            return 1;
        }

        // ── Load the workflow trace and replay it into a WorkflowExecutionResult ──
        if (!File.Exists(workflowTraceFile))
        {
            Console.Error.WriteLine($"Workflow trace not found: {workflowTraceFile}");
            return 1;
        }

        WorkflowTrace wfTrace;
        Models.WorkflowExecutionResult wfResult;
        try
        {
            var replayer = await WorkflowTraceReplayingAgent.FromFileAsync(workflowTraceFile);
            wfTrace = replayer.Trace;
            wfResult = await replayer.ExecuteWorkflowAsync(wfTrace.OriginalPrompt ?? string.Empty, ct);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Failed to load/replay workflow trace: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        // Per-executor chat truth comes from the trace's ExecutorTraces (populated MAF-side before persistence).
        // When absent, every executor is NoTruth: nothing can be checked, so the run has no verdict (B10y).
        IReadOnlyDictionary<string, AgentTrace>? chatTraces = wfTrace.ExecutorTraces;
        if (chatTraces is null || chatTraces.Count == 0)
        {
            Console.Error.WriteLine(
                "[bench workflow-trace-fidelity] NOTE: the workflow trace carries no per-executor ExecutorTraces; "
                + "no executor can be checked, so the run has no verdict (stored PENDING, exit 11). Capture per-executor chat "
                + "traces to get real reconciliation.");
        }

        var result = new WorkflowTraceFidelityReconciler(ParsePreset(preset)).ReconcileToEvalResult(wfResult, chatTraces);

        // ── Persist via the canonical output store (manifest + native report) ──
        var store = new FileSystemOutputStore(agentEvalDir);
        await store.SweepStaleSentinelsAsync(TimeSpan.FromHours(24), ct);
        var subjectIdentity = new SubjectIdentity(SubjectKind.Workflow, subject);
        await store.EnsureSolutionAsync();
        await store.EnsureSubjectAsync(subjectIdentity);

        Console.WriteLine($"Running workflow-trace-fidelity ({preset}) for subject '{subject}'...");
        try
        {
            var manifest = await store.StartRunAsync(
                subjectIdentity,
                new RunContext(
                    EvalProject: "AgentEval.Core",
                    EvalProjectPath: "src/AgentEval.Core/",
                    Harness: "BenchWorkflowTraceFidelityCommand",
                    Seed: null,
                    ParentInvocationId: null,
                    Kind: "benchmark"));
            var runId = manifest.Run.RunId;

            var subResults = result.Details.SubResults ?? (IReadOnlyList<EvalResult>)Array.Empty<EvalResult>();
            // The root's verdict and the label's exit code, as every other bench command (B10w): a warn (0.80-0.99) read
            // Passed, so it printed and stored PASS and exited 0.
            var stats = subResults.Select(s => s.Score).ToRunStats();   // one bucket per check (B8)
            var verdict = result.Score.RunVerdict(stats);
            var summary = new RunSummary(
                SchemaVersion: "1.0",
                RunId: runId,
                Verdict: verdict,
                Stats: stats,
                Metrics: new Dictionary<string, double> { ["workflow_trace_fidelity_score100"] = result.Score.Value * 100 });
            await store.CompleteRunAsync(manifest, summary, ct);

            var runDir = store.ResolveRunDirectory(subjectIdentity, runId);
            await File.WriteAllTextAsync(
                Path.Combine(runDir, "report-native.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }),
                ct);

            Console.WriteLine();
            // Nothing checked has no score to print (B10am L6: it read "0.0%"); the stored metric keeps its placeholder so
            // a baseline comparison still reads the run as worse than a PASS (fail-closed).
            Console.WriteLine(result.Score.Label == "skipped"
                ? $"   Fidelity score: — (nothing checked)   Verdict: {verdict}"
                : $"   Fidelity score: {result.Score.Value * 100:F1}%   Verdict: {verdict}");
            foreach (var sub in subResults)
            {
                Console.WriteLine(sub.Score.Label == "skipped"
                    ? $"   {sub.Metric.Name,-28}   —    (not checked: no chat-boundary trace)"
                    : $"   {sub.Metric.Name,-28} {sub.Score.Value * 100,5:F0}%  ({sub.Score.Severity})");
            }

            Console.WriteLine();
            Console.WriteLine($"   Run ID: {runId}");
            Console.WriteLine($"   Canonical: {runDir}");
            return BenchExitCodes.FromLabel(result.Score.Label);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to persist workflow-trace-fidelity run: {ex.Message}");
            return 1;
        }
    }

    private static SamplePreset ParsePreset(string preset) => preset.Trim().ToLowerInvariant() switch
    {
        "smoke" => SamplePreset.Smoke,
        "standard" => SamplePreset.Standard,
        "audit-grade" or "auditgrade" => SamplePreset.AuditGrade,
        _ => SamplePreset.Standard,
    };
}
