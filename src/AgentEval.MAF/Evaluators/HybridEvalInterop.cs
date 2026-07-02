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
    /// One failed <see cref="EvaluationResult"/> per item, so a down/timed-out source is VISIBLE in the
    /// merged output (not a silently lost run). The metric key is prefixed with the source name.
    /// </summary>
    public static AgentEvaluationResults SkippedResults(IReadOnlyList<EvalItem> items, string source, string reason)
    {
        var results = new List<EvaluationResult>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var r = new EvaluationResult();
            // NumericMetric(name, value, reason) + EvaluationMetricInterpretation(rating, failed, reason):
            // the exact ctor shapes AgentEvalCompositeEvaluator / FoundryEvals.ParseOutputItem use.
            r.Metrics[$"{source}:status"] = new NumericMetric($"{source}:status", 0.0, reason)
            {
                Interpretation = new EvaluationMetricInterpretation(
                    rating: EvaluationRating.Unacceptable, failed: true, reason: reason),
            };
            results.Add(r);
        }
        return new AgentEvaluationResults($"{source} (skipped)", results, inputItems: items) { Error = reason };
    }

    /// <summary>Convenience overload that formats an exception into the skip reason.</summary>
    public static AgentEvaluationResults SkippedResults(IReadOnlyList<EvalItem> items, string source, Exception ex)
        => SkippedResults(items, source, $"{ex.GetType().Name}: {ex.Message}");

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
                        e.Metrics[$"{source}:{kv.Key}"] = kv.Value;   // prefix -> no cross-source collisions
            merged.Add(e);
        }
        return new AgentEvaluationResults(evalName, merged, inputItems: items);
    }
}
