// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Models;
using AgentEval.Tracing;

namespace AgentEval.Benchmarks;

/// <summary>How an executor's framework-reported ledger compares to chat-boundary truth.</summary>
public enum WorkflowFidelityDiff
{
    /// <summary>Framework tokens and finish reason match chat-boundary truth.</summary>
    Agree,

    /// <summary>Only the summed token total diverges.</summary>
    TokenMismatch,

    /// <summary>Only the finish reason diverges.</summary>
    FinishMismatch,

    /// <summary>Both tokens and finish reason diverge.</summary>
    Both,

    /// <summary>No chat-boundary trace was supplied for this executor (live-workflow Path-4 / ledger-only).</summary>
    NoTruth,
}

/// <summary>Per-executor reconciliation record.</summary>
public sealed record WorkflowExecutorFidelity(
    string ExecutorId,
    int TokensFramework,
    int? TokensChatTruth,
    string? FinishFramework,
    string? FinishChatTruth,
    WorkflowFidelityDiff DiffKind)
{
    /// <summary>0–1 fidelity: Agree/NoTruth = 1.0; a single-axis mismatch = 0.5; both = 0.0.</summary>
    public double Score => DiffKind switch
    {
        WorkflowFidelityDiff.Agree => 1.0,
        WorkflowFidelityDiff.NoTruth => 1.0,
        WorkflowFidelityDiff.TokenMismatch => 0.5,
        WorkflowFidelityDiff.FinishMismatch => 0.5,
        WorkflowFidelityDiff.Both => 0.0,
        _ => 1.0,
    };
}

/// <summary>The whole-workflow reconciliation result.</summary>
public sealed record WorkflowTraceFidelityReport(IReadOnlyList<WorkflowExecutorFidelity> Executors)
{
    /// <summary>
    /// Mean fidelity over executors that HAVE chat-boundary truth (<see cref="WorkflowFidelityDiff"/> !=
    /// <see cref="WorkflowFidelityDiff.NoTruth"/>); 1.0 when none is verifiable. NoTruth executors are
    /// deliberately EXCLUDED from the aggregate — otherwise, in a partial-truth workflow, a genuine deception
    /// on one traced executor would be averaged away by many untraced (NoTruth = 1.0) executors and pass the
    /// gate. Coverage is reported separately via <see cref="VerifiedCount"/> / <see cref="Executors"/>.Count.
    /// </summary>
    public double OverallScore
    {
        get
        {
            var verified = Executors.Where(e => e.DiffKind != WorkflowFidelityDiff.NoTruth).ToList();
            return verified.Count == 0 ? 1.0 : verified.Average(e => e.Score);
        }
    }

    /// <summary>Number of executors that had chat-boundary truth to reconcile against (not NoTruth).</summary>
    public int VerifiedCount => Executors.Count(e => e.DiffKind != WorkflowFidelityDiff.NoTruth);
}

/// <summary>
/// Glass Box Part 2 (P2.B3): the workflow analogue of <see cref="TraceFidelityRunner"/>. Reconciles each
/// executor's framework-reported per-executor ledger (tokens + finish reason, from P2.B1) against
/// chat-boundary truth when pre-wired traces are available, and reports the ledger itself otherwise
/// (the live-workflow Path-4 case). Pure code — no LLM tokens (CostTier.Free).
/// </summary>
/// <remarks>
/// Reconciliation is at EXECUTOR granularity, not per-step: <c>WorkflowChatRecording</c> collects one
/// chat trace per executor for the whole run, so a loop workflow that produces multiple
/// <see cref="ExecutorStep"/>s for one executor must have their framework tokens SUMMED before comparison
/// — otherwise every looped executor falsely reports a TokenMismatch (fix C5).
/// <para>
/// <b>Chat-truth availability caveat.</b> The <paramref name="chatTraces"/> dict must contain per-executor
/// traces that carry <see cref="TraceEntryScope.ChatTurn"/> <b>Response</b> entries. Inside a <i>live</i> MAF
/// <c>InProcessExecution</c> workflow those are NOT available today — <c>WorkflowChatRecording</c> documents
/// that executor responses are not routed back through the instrumented client, so per-executor traces come
/// back without Response entries (the Glass Box Path-2 upstream-MAF-hook gap). Until that hook lands, every
/// executor in a live run is <see cref="WorkflowFidelityDiff.NoTruth"/> (ledger-only, score 1.0); the
/// reconciler produces real reconciliation only for <b>direct-agent / pre-wired / hand-built</b> traces.
/// This is a library primitive: it has no registered benchmark family or CLI wiring yet (follow-up P2.B4).
/// </para>
/// </remarks>
public sealed class WorkflowTraceFidelityReconciler
{
    private readonly SamplePreset _preset;

    /// <summary>Creates a reconciler. The preset is informational (it does not change scoring).</summary>
    public WorkflowTraceFidelityReconciler(SamplePreset preset = SamplePreset.Standard) => _preset = preset;

    /// <summary>The capture preset this reconciler was configured with.</summary>
    public SamplePreset Preset => _preset;

    /// <summary>Reconciles a workflow result against optional per-executor chat-boundary traces.</summary>
    public WorkflowTraceFidelityReport Reconcile(
        WorkflowExecutionResult result,
        IReadOnlyDictionary<string, AgentTrace>? chatTraces = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var records = new List<WorkflowExecutorFidelity>();

        // Probe chat traces case-insensitively so a framework-reported executor id ("Writer") matches a
        // captured-trace key ("writer") — consistent with the case-insensitive executor-id matching used
        // across the workflow-assertion surface. Last-write-wins on a (benign) case collision.
        Dictionary<string, AgentTrace>? chatByExecutor = null;
        if (chatTraces is not null)
        {
            chatByExecutor = new Dictionary<string, AgentTrace>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in chatTraces)
            {
                chatByExecutor[kv.Key] = kv.Value;
            }
        }

        // Group case-insensitively too (not just the chat-trace lookup below): a workflow that emits an
        // executor id with inconsistent casing across steps is ONE executor, and a case-sensitive GroupBy
        // would split it into multiple groups that each match the same chat trace (double-counting).
        foreach (var group in result.Steps.GroupBy(s => s.ExecutorId, StringComparer.OrdinalIgnoreCase))
        {
            var executorId = group.Key;
            var tokensFramework = group.Sum(s => s.TokenUsage?.TotalTokens ?? 0);
            var finishFramework = group.Select(s => s.FinishReason).LastOrDefault(f => f is not null);

            var responses = chatByExecutor is not null
                && chatByExecutor.TryGetValue(executorId, out var chatTrace) && chatTrace is not null
                ? chatTrace.Entries
                    .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
                    .ToList()
                : null;

            // Only reconcile when there is actual chat-boundary truth (≥1 ChatTurn response). A supplied
            // trace with no responses (tool-only executor, or a partial/failed capture) is NoTruth, not a
            // comparison against a zero total (which would raise a spurious TokenMismatch).
            if (responses is { Count: > 0 })
            {
                var tokensChatTruth = responses.Sum(e => e.TokenUsage?.TotalTokens ?? 0);
                var finishChatTruth = responses.Select(e => e.FinishReason).LastOrDefault(f => f is not null);

                // Flag only meaningful UNDER-reporting (framework hides tokens the model actually consumed),
                // with a tolerance — mirrors TraceFidelityRunner.TokenUnderReporting. The framework ledger
                // (MAF's aggregate AgentResponse.Usage) and chat-boundary truth (summed per-turn) are distinct
                // capture points, so exact equality would false-positive on rounding or benign over-reporting.
                var shortfall = (tokensChatTruth - tokensFramework) / (double)Math.Max(tokensChatTruth, 1);
                var tokenMismatch = shortfall > TraceFidelityRubric.TokenToleranceFraction;

                // Suppressed finish reason is the headline deception: chat truth reports a terminal reason
                // (e.g. content_filter / length) while the framework ledger reports null. So a mismatch fires
                // whenever chat truth HAS a finish reason and the framework's differs — INCLUDING when the
                // framework's is null (suppression). Mirrors TraceFidelityRunner.SuppressedFinishReason.
                var finishMismatch = finishChatTruth is not null
                    && !string.Equals(finishFramework, finishChatTruth, StringComparison.Ordinal);

                var diff = (tokenMismatch, finishMismatch) switch
                {
                    (true, true) => WorkflowFidelityDiff.Both,
                    (true, false) => WorkflowFidelityDiff.TokenMismatch,
                    (false, true) => WorkflowFidelityDiff.FinishMismatch,
                    _ => WorkflowFidelityDiff.Agree,
                };

                records.Add(new WorkflowExecutorFidelity(
                    executorId, tokensFramework, tokensChatTruth, finishFramework, finishChatTruth, diff));
            }
            else
            {
                records.Add(new WorkflowExecutorFidelity(
                    executorId, tokensFramework, null, finishFramework, null, WorkflowFidelityDiff.NoTruth));
            }
        }

        return new WorkflowTraceFidelityReport(records);
    }

    /// <summary>
    /// Reconciles and projects onto the unified <see cref="EvalResult"/> tree — one leaf per executor,
    /// root score = mean of the leaves. Mirrors <see cref="TraceFidelityRunner.ReconcileToEvalResult"/>.
    /// </summary>
    public EvalResult ReconcileToEvalResult(
        WorkflowExecutionResult result,
        IReadOnlyDictionary<string, AgentTrace>? chatTraces = null)
    {
        var report = Reconcile(result, chatTraces);

        var subResults = report.Executors.Select(e => new EvalResult(
            Metric: new EvalMetadata(Key: $"workflow_trace_fidelity.executor.{e.ExecutorId}", Name: e.ExecutorId, Category: "TraceFidelity", Version: "1.0"),
            Score: new EvalScore(
                Value: e.Score, Ordinal: null,
                Label: e.Score >= 0.99 ? "pass" : e.Score >= 0.8 ? "warn" : "fail",
                Passed: e.Score >= 0.8, Threshold: 0.8,
                Severity: e.Score >= 0.99 ? "none" : e.Score >= 0.8 ? "low" : e.Score >= 0.5 ? "medium" : "high", Confidence: null),
            Details: new EvalDetails(
                Dimensions: BuildLeafDimensions(e),
                Evidence: new List<EvalEvidence>
                {
                    new(Source: "workflow-ledger-vs-chat", Reference: e.DiffKind.ToString(),
                        // Render a null finish reason explicitly as <null> (not '') — a suppressed/null finish
                        // is a key fidelity signal, so null must not read as an empty string to consumers.
                        Message: $"executor '{e.ExecutorId}': framework={e.TokensFramework} tokens / {FinishLabel(e.FinishFramework)}, "
                            + (e.TokensChatTruth is null
                                ? "no chat truth (ledger-only)"
                                : $"chat-truth={e.TokensChatTruth} tokens / {FinishLabel(e.FinishChatTruth)}")),
                },
                Recommendations: null, SubResults: null, AggregationStrategy: null),
            Provenance: new EvalProvenance(Type: "code", JudgeModel: null, PromptId: null, PromptHash: null, TokensUsed: null, EstimatedCost: 0.0, CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow)).ToList();

        return new EvalResult(
            Metric: new EvalMetadata(Key: "workflow_trace_fidelity", Name: "Workflow Trace Fidelity", Category: "TraceFidelity", Version: "1.0"),
            Score: new EvalScore(
                Value: report.OverallScore, Ordinal: null,
                Label: report.OverallScore >= 0.99 ? "pass" : report.OverallScore >= 0.8 ? "warn" : "fail",
                Passed: report.OverallScore >= 0.8, Threshold: 0.8,
                Severity: report.OverallScore >= 0.99 ? "none" : report.OverallScore >= 0.8 ? "low" : report.OverallScore >= 0.5 ? "medium" : "high", Confidence: null),
            Details: new EvalDetails(
                Dimensions: new Dictionary<string, double>
                {
                    ["score100"] = report.OverallScore * 100,
                    ["executors"] = report.Executors.Count,
                    ["verified"] = report.VerifiedCount, // executors with chat-boundary truth; the rest are NoTruth
                },
                Evidence: null, Recommendations: null, SubResults: subResults, AggregationStrategy: "per-executor"),
            Provenance: new EvalProvenance(Type: "code", JudgeModel: null, PromptId: null, PromptHash: null, TokensUsed: null, EstimatedCost: 0.0, CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // Renders a finish reason for evidence, distinguishing a genuinely absent (null/suppressed) reason from
    // an empty string — the null case is a load-bearing fidelity signal.
    private static string FinishLabel(string? finishReason) =>
        finishReason is null ? "<null>" : $"'{finishReason}'";

    // Per-executor leaf dimensions. chatTruthTokens/delta are OMITTED (not encoded as a -1/0 sentinel in the
    // numeric map) when there is no chat truth, so a downstream aggregator can't mistake absence for a value.
    private static Dictionary<string, double> BuildLeafDimensions(WorkflowExecutorFidelity e)
    {
        var dimensions = new Dictionary<string, double>
        {
            ["frameworkTokens"] = e.TokensFramework,
            ["score100"] = e.Score * 100,
        };
        if (e.TokensChatTruth is int chatTruth)
        {
            dimensions["chatTruthTokens"] = chatTruth;
            dimensions["delta"] = e.TokensFramework - chatTruth;
        }

        return dimensions;
    }
}
