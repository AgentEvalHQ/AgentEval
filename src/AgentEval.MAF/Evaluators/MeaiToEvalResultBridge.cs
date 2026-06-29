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
    private static readonly Regex s_scoreMarker =
        new(@"AgentEval score:\s*(\d+(?:\.\d+)?)/100", RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            var leaves = meai.Metrics.Values.Select(m => MetricToLeaf(m, judgeModel)).ToList();
            queryNodes.Add(Composite(
                key: $"maf.eval.query{i}",
                name: $"Query: {Truncate(query, 80)}",
                category: "agentic",
                subs: leaves));
        }

        return Composite("maf.eval", evalName, "agentic", queryNodes);
    }

    private static EvalResult MetricToLeaf(EvaluationMetric metric, string? judgeModel)
    {
        var reason = metric.Interpretation?.Reason ?? metric.Reason;
        var passed = metric.Interpretation?.Failed != true;

        double score0To100;
        var marker = reason is null ? Match.Empty : s_scoreMarker.Match(reason);
        if (marker.Success)
        {
            score0To100 = double.Parse(marker.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        else if (metric is NumericMetric { Value: { } v })
        {
            score0To100 = Math.Clamp((v - 1) / 4.0 * 100.0, 0, 100);
        }
        else
        {
            score0To100 = passed ? 100 : 0;
        }

        // AgentEval metric names are prefixed code_* (deterministic, no LLM) or llm_* (LLM-as-judge).
        var isLlm = metric.Name.StartsWith("llm_", StringComparison.OrdinalIgnoreCase);
        var category = metric.Name.Contains("tool", StringComparison.OrdinalIgnoreCase)
            ? "agentic-process"
            : "quality";

        var evidence = string.IsNullOrWhiteSpace(reason)
            ? null
            : new[] { new EvalEvidence(Source: isLlm ? "judge" : "code", Reference: metric.Name, Message: reason!) };

        return new EvalResult(
            Metric: new EvalMetadata(metric.Name, Prettify(metric.Name), category, "1.0.0"),
            Score: new EvalScore(
                Value: score0To100 / 100.0,
                Ordinal: null,
                Label: passed ? "pass" : "fail",
                Passed: passed,
                Threshold: 0.70,
                Severity: passed ? "none" : "high",
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

    private static string Prettify(string metricName)
    {
        var bare = metricName;
        foreach (var prefix in new[] { "code_", "llm_" })
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
