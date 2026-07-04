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
    /// <summary>Mean of the per-executor fidelity scores (1.0 when there are no executors).</summary>
    public double OverallScore => Executors.Count == 0 ? 1.0 : Executors.Average(e => e.Score);
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

        foreach (var group in result.Steps.GroupBy(s => s.ExecutorId, StringComparer.Ordinal))
        {
            var executorId = group.Key;
            var tokensFramework = group.Sum(s => s.TokenUsage?.TotalTokens ?? 0);
            var finishFramework = group.Select(s => s.FinishReason).LastOrDefault(f => f is not null);

            var responses = chatTraces is not null
                && chatTraces.TryGetValue(executorId, out var chatTrace) && chatTrace is not null
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

                var tokenMismatch = tokensFramework != tokensChatTruth;

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
                Severity: e.Score >= 0.8 ? "Low" : e.Score >= 0.5 ? "Medium" : "High", Confidence: null),
            Details: new EvalDetails(
                Dimensions: BuildLeafDimensions(e),
                Evidence: new List<EvalEvidence>
                {
                    new(Source: "workflow-ledger-vs-chat", Reference: e.DiffKind.ToString(),
                        Message: $"executor '{e.ExecutorId}': framework={e.TokensFramework} tokens / '{e.FinishFramework}', "
                            + (e.TokensChatTruth is null
                                ? "no chat truth (ledger-only)"
                                : $"chat-truth={e.TokensChatTruth} tokens / '{e.FinishChatTruth}'")),
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
                Severity: report.OverallScore >= 0.8 ? "Low" : report.OverallScore >= 0.5 ? "Medium" : "High", Confidence: null),
            Details: new EvalDetails(
                Dimensions: new Dictionary<string, double> { ["score100"] = report.OverallScore * 100, ["executors"] = report.Executors.Count },
                Evidence: null, Recommendations: null, SubResults: subResults, AggregationStrategy: "per-executor"),
            Provenance: new EvalProvenance(Type: "code", JudgeModel: null, PromptId: null, PromptHash: null, TokensUsed: null, EstimatedCost: 0.0, CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

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
