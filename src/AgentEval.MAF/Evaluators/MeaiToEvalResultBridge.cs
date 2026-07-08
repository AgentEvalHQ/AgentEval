// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Bridges the result of MAF's <c>agent.EvaluateAsync(...)</c> — an
/// <see cref="AgentEvaluationResults"/> wrapping per-query MEAI <see cref="EvaluationResult"/>s — into
/// AgentEval's unified <see cref="EvalResult"/> tree so it can be rendered by any
/// <c>IEvalResultRenderer</c> (HTML, PDF, …).
/// </summary>
/// <remarks>
/// This is the inverse direction of <see cref="ResultConverter"/> (AgentEval → MEAI). It lets the
/// MAF-native evaluation path produce the same report artefacts the AgentEval benchmark engine does.
/// AgentEval's original 0–100 score is recovered from the marker
/// <c>ResultConverter</c> embeds in each metric's reason ("AgentEval score: N/100 …"), falling back to
/// the MEAI 1–5 → 0–100 linear map when the marker is absent.
/// </remarks>
public static class MeaiToEvalResultBridge
{
    // Captures the score and, when present, the original label + severity that AgentEvalCompositeEvaluator
    // embeds — e.g. "AgentEval score: 50/100 (fail, severity critical)". Groups: 1=score, 2=label, 3=severity.
    private static readonly Regex s_scoreMarker =
        new(@"AgentEval score:\s*(\d+(?:\.\d+)?)/100(?:\s*\(([^,]+),\s*severity\s+([^)]+)\))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Builds a composite <see cref="EvalResult"/> tree: a root over one node per query, each query
    /// node holding one leaf per evaluated metric.
    /// </summary>
    /// <param name="evalName">Display name for the root node.</param>
    /// <param name="queries">The queries, in the same order as <see cref="AgentEvaluationResults.Items"/>.</param>
    /// <param name="results">The results returned by <c>agent.EvaluateAsync</c>.</param>
    /// <param name="judgeModel">Optional judge model id, surfaced as per-leaf provenance for LLM metrics.</param>
    public static EvalResult Build(
        string evalName,
        IReadOnlyList<string> queries,
        AgentEvaluationResults results,
        string? judgeModel = null)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(results);

        var items = results.Items; // IReadOnlyList<EvaluationResult>, one entry per query
        var queryNodes = new List<EvalResult>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            var meai = items[i];
            var query = i < queries.Count ? queries[i] : $"query[{i}]";

            // Iterate the dictionary (not just .Values): the key carries the disambiguation suffix
            // (e.g. "Relevance #2") that AgentEvalCompositeEvaluator.AddMetric adds when two leaves
            // share a metric name — using it as the leaf key keeps the EvalResult tree keys unique.
            var leaves = meai.Metrics.Select(kv => MetricToLeaf(kv.Key, kv.Value, judgeModel)).ToList();
            queryNodes.Add(Composite(
                key: $"maf.eval.query{i}",
                name: $"Query: {Truncate(query, 80)}",
                category: "agentic",
                subs: leaves));
        }

        return Composite("maf.eval", evalName, "agentic", queryNodes);
    }

    private static EvalResult MetricToLeaf(string key, EvaluationMetric metric, string? judgeModel)
    {
        var reason = metric.Interpretation?.Reason ?? metric.Reason;

        double score0To100;
        string? markerLabel = null, markerSeverity = null;
        var marker = reason is null ? Match.Empty : s_scoreMarker.Match(reason);
        if (marker.Success)
        {
            score0To100 = Math.Clamp(double.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100);
            if (marker.Groups[2].Success) markerLabel = marker.Groups[2].Value.Trim();
            if (marker.Groups[3].Success) markerSeverity = marker.Groups[3].Value.Trim();
        }
        else if (metric is NumericMetric { Value: { } v })
        {
            // Foundry uses mixed scales across evaluators:
            //   0–1  for agent/binary evaluators (task_adherence, intent_resolution, …)
            //   1–5  for quality evaluators       (relevance, coherence, fluency, …)
            // AgentEval's own metrics always embed the score marker above, so they never reach here.
            // Primary: v clearly above 1.0 → must be 1–5 scale.
            // Edge case: v ≈ 1.0 is ambiguous (best on 0–1 OR worst on 1–5). Use Interpretation.Failed
            // to disambiguate — Failed=true → 1–5 worst (0%); Failed=false/null → 0–1 best (100%).
            // For all other v < 1.0 − ε (e.g. task_adherence=0.5 with Failed=true), treat as 0–1.
            // Use an epsilon to handle floating-point imprecision in deserialized values (e.g. a
            // JSON 1.0 that arrives as 0.9999999998 or 1.0000000002 must still hit the same branch).
            const double nearOneEpsilon = 1e-9;
            bool likelyAbove1 = v > 1.0 + nearOneEpsilon;
            bool nearOne = !likelyAbove1 && Math.Abs(v - 1.0) <= nearOneEpsilon;
            score0To100 = (likelyAbove1 || (nearOne && metric.Interpretation?.Failed == true))
                ? Math.Clamp((v - 1) / 4.0 * 100.0, 0, 100)
                : Math.Clamp(v * 100.0, 0, 100);
        }
        else
        {
            score0To100 = metric.Interpretation?.Failed == true ? 0 : 100;
        }

        // Prefer the original AgentEval verdict embedded in the marker (label + severity, so a "critical"
        // leaf isn't flattened to "high"); else honour an explicit MEAI Failed flag; else derive pass/fail
        // from the recovered score (threshold 70) — a low score can never render as a green "pass".
        var passed = markerLabel is not null
            ? string.Equals(markerLabel, "pass", StringComparison.OrdinalIgnoreCase)
            : metric.Interpretation?.Failed is bool failed ? !failed : score0To100 >= 70.0;
        var label = markerLabel ?? (passed ? "pass" : "fail");
        var severity = markerSeverity ?? (passed ? "none" : "high");

        // AgentEval metric names are prefixed code_* (deterministic, no LLM) or llm_* (LLM-as-judge).
        var isLlm = metric.Name.StartsWith("llm_", StringComparison.OrdinalIgnoreCase);
        var category = CategoryFor(metric.Name);

        var evidence = string.IsNullOrWhiteSpace(reason)
            ? null
            : new[] { new EvalEvidence(Source: isLlm ? "judge" : "code", Reference: metric.Name, Message: reason!) };

        return new EvalResult(
            Metric: new EvalMetadata(key, Prettify(metric.Name), category, "1.0.0"),
            Score: new EvalScore(
                Value: score0To100 / 100.0,
                Ordinal: null,
                Label: label,
                Passed: passed,
                Threshold: 0.70,
                Severity: severity,
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: evidence,
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new EvalProvenance(
                Type: isLlm ? "atomic-llm" : "atomic-code",
                JudgeModel: isLlm ? judgeModel : null,
                PromptId: null,
                PromptHash: null,
                TokensUsed: null,
                EstimatedCost: 0,
                CacheHit: false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static EvalResult Composite(string key, string name, string category, IReadOnlyList<EvalResult> subs)
    {
        var avg = subs.Count == 0 ? 0 : subs.Average(s => s.Score.Value);
        var passed = subs.Count > 0 && subs.All(s => s.Score.Passed);
        return new EvalResult(
            Metric: new EvalMetadata(key, name, category, "1.0.0"),
            Score: new EvalScore(
                Value: avg,
                Ordinal: null,
                Label: passed ? "pass" : "fail",
                Passed: passed,
                Threshold: 0.70,
                Severity: passed ? "none" : "high",
                Confidence: null),
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: null,
                Recommendations: null,
                SubResults: subs,
                AggregationStrategy: "mean"),
            Provenance: new EvalProvenance("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // Map an AgentEval metric name onto a coarse report category, preserving the common families
    // (rag / safety-security / agentic-process) instead of collapsing every non-tool metric to "quality".
    private static string CategoryFor(string name)
    {
        bool Has(string s) => name.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("tool")) return "agentic-process";
        if (Has("relevance") || Has("groundedness") || Has("retrieval") || Has("context") || Has("faithful")) return "rag";
        if (Has("safety") || Has("harm") || Has("toxic") || Has("bias") || Has("hate") || Has("violence")) return "safety-security";
        return "quality";
    }

    private static string Prettify(string metricName)
    {
        var bare = metricName;
        foreach (var prefix in new[] { "code_", "llm_", "embed_" })
        {
            if (bare.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                bare = bare[prefix.Length..];
                break;
            }
        }

        var words = bare.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}
