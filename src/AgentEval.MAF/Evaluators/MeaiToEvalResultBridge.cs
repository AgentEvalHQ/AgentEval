// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentEval.Evals;
using AgentEval.Evals.Meta;

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
            // The chance-floor declaration is a statement about the tree, not a score: rendering it as a
            // leaf used to show a "100/100 pass" node that measured nothing.
            var leaves = meai.Metrics
                .Where(kv => !string.Equals(kv.Key, AgentEvalCompositeEvaluator.FloorDeclarationMetricName, StringComparison.Ordinal))
                .Select(kv => MetricToLeaf(kv.Key, kv.Value, judgeModel)).ToList();
            // An AgentEvalCompositeEvaluator item carries its verdict on the "(overall)" metric and marks every leaf
            // informational, so MAF passes the item on that verdict alone. The report must agree with MAF: the query
            // node takes its verdict from "(overall)" when present, not from "every leaf passed".
            var overall = meai.Metrics.Keys.Any(k => k.EndsWith(" (overall)", StringComparison.Ordinal))
                ? leaves.FirstOrDefault(l => l.Metric.Key.EndsWith(" (overall)", StringComparison.Ordinal))
                : null;
            queryNodes.Add(Composite(
                key: $"maf.eval.query{i}",
                name: $"Query: {Truncate(query, 80)}",
                category: "agentic",
                subs: leaves,
                verdictFrom: overall));
        }

        return Composite("maf.eval", evalName, "agentic", queryNodes);
    }

    private static EvalResult MetricToLeaf(string key, EvaluationMetric metric, string? judgeModel)
    {
        var reason = metric.Interpretation?.Reason ?? metric.Reason;

        double score0To100;
        string? markerLabel = null, markerSeverity = null;
        var marker = reason is null ? Match.Empty : s_scoreMarker.Match(reason);

        // Without AgentEval's marker, a metric MEAI could not score is no verdict (#203 review round 4, B10q): an error
        // diagnostic (MEAI's quality evaluators record "Failed to parse ... score" or a missing evaluator context that
        // way and leave the value empty), an Inconclusive rating, or no value at all. All of them fell to the last branch
        // below and read PASS 100 — a judge whose reply did not parse passed. A metric that is neither numeric nor
        // boolean and carries no interpretation states no verdict either: not measured, never a pass.
        if (!marker.Success)
        {
            var errors = metric.Diagnostics?
                .Where(d => d.Severity == EvaluationDiagnosticSeverity.Error)
                .Select(d => d.Message)
                .ToList() ?? [];
            var noValue = metric switch
            {
                NumericMetric n => n.Value is null,
                BooleanMetric b => b.Value is null,
                _ => false,
            };
            if (errors.Count > 0 || noValue || metric.Interpretation?.Rating == EvaluationRating.Inconclusive)
                return NoVerdictLeaf(key, metric, "error",
                    errors.Count > 0 ? string.Join(" ", errors) : reason ?? "The metric has no usable value.", judgeModel);
            if (metric is not (NumericMetric or BooleanMetric) && metric.Interpretation is null)
                return NoVerdictLeaf(key, metric, "skipped",
                    reason ?? "The metric carries no score and no interpretation, so it states no verdict.", judgeModel);
        }

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
        else if (metric is BooleanMetric { Value: { } b })
        {
            // A boolean is its own verdict: false read 100 (the last branch) when there was no interpretation.
            score0To100 = b ? 100 : 0;
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

    // A leaf with no verdict: "error" (MEAI could not score it) or "skipped" (it states none). Never a 0 and never a pass.
    private static EvalResult NoVerdictLeaf(string key, EvaluationMetric metric, string label, string message, string? judgeModel)
    {
        var isLlm = metric.Name.StartsWith("llm_", StringComparison.OrdinalIgnoreCase);
        return new EvalResult(
            Metric: new EvalMetadata(key, Prettify(metric.Name), CategoryFor(metric.Name), "1.0.0"),
            Score: new EvalScore(0.0, null, label, false, 0.70, "none", null)
            {
                Measurement = label == "skipped" ? MeasurementState.NotMeasured : MeasurementState.Measured,
            },
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: [new EvalEvidence(Source: isLlm ? "judge" : "code", Reference: metric.Name, Message: message)],
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null) { Summary = message },
            Provenance: new EvalProvenance(isLlm ? "atomic-llm" : "atomic-code", isLlm ? judgeModel : null,
                null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // Every metric is a required part (B10q): the score is the mean of the measured ones; a metric with no verdict is
    // never averaged in as a 0, never lets the node pass (an error is "error" unless a measured failure decides it; one
    // that did not run withholds a pass), and nothing measured is no verdict. Before, the mean took the placeholders
    // and the label was pass/fail only, so an errored metric made its query FAIL.
    private static EvalResult Composite(
        string key, string name, string category, IReadOnlyList<EvalResult> subs, EvalResult? verdictFrom = null)
    {
        var measured = subs.Where(s => s.Score.CountsTowardAggregate()).ToList();
        var failing = measured.Where(s => s.Score.Label is "fail").ToList();
        var errored = subs.Any(s => s.Score.Label == "error");
        var notRun = subs.Any(s => !s.Score.CountsTowardAggregate() && s.Score.Label != "error"
                                   && s.Score.CensusBucket() == MeasurementState.NotMeasured);
        var derivedLabel =
            measured.Count == 0 ? (errored ? "error" : "skipped")
            : failing.Count > 0 ? "fail"
            : errored ? "error"
            : notRun || measured.Any(s => s.Score.Label == "warn") ? "warn"
            : "pass";
        var label = verdictFrom?.Score.Label ?? derivedLabel;
        var passed = verdictFrom?.Score.Passed ?? derivedLabel == "pass";
        var avg = verdictFrom?.Score.Value ?? (measured.Count == 0 ? 0 : measured.Average(s => s.Score.Value));
        var severity = verdictFrom?.Score.Severity ?? derivedLabel switch
        {
            "fail" => SeverityRollup.Max(failing.Select(s => s.Score.Severity).Append("medium")),
            "warn" => notRun && !measured.Any(s => s.Score.Label == "warn") ? "none" : "medium",
            _ => "none",
        };
        var measurement = verdictFrom is null && derivedLabel == "warn" && notRun && !measured.Any(s => s.Score.Label == "warn")
            ? MeasurementState.NotMeasured
            : MeasurementState.Measured;
        return new EvalResult(
            Metric: new EvalMetadata(key, name, category, "1.0.0"),
            Score: new EvalScore(
                Value: avg,
                Ordinal: null,
                Label: label,
                Passed: passed,
                Threshold: 0.70,
                Severity: severity,
                Confidence: null) { Measurement = verdictFrom?.Score.Measurement ?? measurement },
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: null,
                Recommendations: null,
                SubResults: subs,
                AggregationStrategy: "mean-of-measured"),
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
