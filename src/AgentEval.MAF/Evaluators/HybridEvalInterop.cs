// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Internal helpers for the hybrid (Foundry ⊕ AgentEval-local) evaluator path: build a visible
/// "skipped" result for a source that failed/timed out, and merge per-source results into one.
/// </summary>
internal static class HybridEvalInterop
{
    /// <summary>
    /// One neutral <see cref="EvaluationResult"/> per item, so a down/timed-out or intentionally-skipped
    /// source is VISIBLE in the merged output (not a silently lost run) WITHOUT masquerading as a real
    /// low-scoring failure.
    /// </summary>
    /// <param name="label">
    /// <c>"error"</c> for an infrastructure failure (timeout/exception — the default), or <c>"skipped"</c>
    /// for an intentional skip (e.g. circuit breaker open). Renders neutral (severity none) either way; the
    /// distinction drives the rendered label and the <see cref="UnifiedEvalReport"/> branch.
    /// </param>
    public static AgentEvaluationResults SkippedResults(
        IReadOnlyList<EvalItem> items, string source, string reason, string label = "error")
    {
        // The score marker is what MeaiToEvalResultBridge parses to recover the label/severity.
        var marker = $"AgentEval score: 0/100 ({label}, severity none) — {reason}";
        var results = new List<EvaluationResult>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var r = new EvaluationResult();
            // UNPREFIXED key: Merge() adds the "{source}:" prefix exactly once (no double-prefixing).
            r.Metrics["status"] = new NumericMetric("status", 0.0, marker)
            {
                Interpretation = new EvaluationMetricInterpretation(
                    rating: EvaluationRating.Inconclusive, failed: true, reason: marker),
            };
            results.Add(r);
        }
        // ProviderName carries the neutral label so UnifiedEvalReport renders the matching neutral branch.
        return new AgentEvaluationResults($"{source} ({label})", results, inputItems: items) { Error = reason };
    }

    /// <summary>Convenience overload for an exception — surfaced as a neutral <c>"error"</c> branch.</summary>
    public static AgentEvaluationResults SkippedResults(IReadOnlyList<EvalItem> items, string source, Exception ex)
        => SkippedResults(items, source, $"{ex.GetType().Name}: {ex.Message}", label: "error");

    /// <summary>
    /// Per query <c>i</c>, unions each source's metrics into one <see cref="EvaluationResult"/> with a
    /// <c>"{source}:"</c> key prefix so metrics from different sources never collide.
    /// </summary>
    public static AgentEvaluationResults Merge(
        IReadOnlyList<(string Source, AgentEvaluationResults Result)> perSource,
        IReadOnlyList<EvalItem> items, string evalName)
    {
        var merged = new List<EvaluationResult>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var e = new EvaluationResult();
            foreach (var (source, res) in perSource)
                if (i < res.Items.Count)
                    foreach (var kv in res.Items[i].Metrics)
                    {
                        // Prefix exactly once: guard against an inner that already returns source-prefixed keys.
                        var mkey = kv.Key.StartsWith($"{source}:", StringComparison.Ordinal) ? kv.Key : $"{source}:{kv.Key}";
                        e.Metrics[mkey] = kv.Value;   // -> no cross-source collisions, no double-prefix
                    }
            merged.Add(e);
        }
        return new AgentEvaluationResults(evalName, merged, inputItems: items);
    }
}
