// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals.Agentic.Calibration;

namespace AgentEval.Evals.Agentic.JudgeQuality;

/// <summary>
/// Meta-evaluator that computes inter-rater agreement (Cohen's kappa) across a set of
/// judge results for the same input. Pure-code — no LLM invocation.
/// <para>
/// <b>Input contract</b> (<see cref="EvalInput.Metadata"/> keys):
/// <list type="bullet">
///   <item>
///     <c>"judge_results"</c> (required) — a <see cref="IReadOnlyList{EvalResult}"/> instance
///     or a JSON array string of objects with at minimum a <c>label</c> string field
///     (e.g. <c>"pass"</c>, <c>"fail"</c>, <c>"warn"</c>).
///     Accepted value types: <see cref="IReadOnlyList{EvalResult}"/>,
///     <see cref="IEnumerable{EvalResult}"/>, or a JSON <c>string</c>.
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Score semantics</b>: kappa value in [0, 1] (negative kappas clamped to 0).
/// Passes when kappa &gt;= <c>passThreshold</c>. Severity: <c>low</c>
/// (this is a meta-metric, not a real-world output evaluator).
/// </para>
/// </summary>
public sealed class JudgeAgreementEval : IEval
{
    private readonly double _passThreshold;

    /// <inheritdoc/>
    public string Key => "judge_agreement";

    /// <inheritdoc/>
    public string Name => "Judge Agreement";

    /// <inheritdoc/>
    public string Category => "judge-quality";

    /// <inheritdoc/>
    public string Version => "1.0.0";

    /// <summary>
    /// Initialises a new <see cref="JudgeAgreementEval"/>.
    /// </summary>
    /// <param name="passThreshold">
    /// Kappa value at or above which the eval passes. Defaults to 0.60
    /// (moderate agreement threshold, per standard kappa interpretation).
    /// </param>
    public JudgeAgreementEval(double passThreshold = 0.60)
    {
        if (passThreshold is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(passThreshold), passThreshold, "Pass threshold must be in [0, 1].");
        _passThreshold = passThreshold;
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var labels = ExtractLabels(input);

        if (labels.Count < 2)
        {
            // Cannot compute agreement with fewer than 2 judges — return skipped.
            return Task.FromResult(EvalResult.Skipped(this,
                "JudgeAgreementEval requires at least 2 judge results in EvalInput.Metadata[\"judge_results\"]."));
        }

        // Build all pairwise (rater-i, rater-j) label pairs and compute kappa
        var pairs = BuildPairs(labels);
        var kappa = CalibrationMetrics.CohensKappa(pairs);
        // F-004 split semantics (2026-05-24): at the calibration-dataset level (judge vs
        // golden labels) NaN from CohensKappa means "single-class dataset → undefined,
        // surface for operator review". At the panel-agreement level (judge-to-judge on
        // a single observation) NaN means "all raters returned the same label" — which IS
        // perfect agreement, not undefined. Normalise here to preserve the historical
        // pair-agreement semantics; the honest-NaN signal still fires for calibration runs.
        if (double.IsNaN(kappa)) kappa = 1.0;
        // Clamp negative kappa to 0
        var score = Math.Max(0.0, kappa);

        var passed = score >= _passThreshold;
        var label = passed ? "pass" : "fail";
        var severity = passed ? "none" : "low";

        return Task.FromResult(new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(score, null, label, passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double> { ["kappa"] = kappa, ["judge_count"] = labels.Count },
                Evidence: null,
                Recommendations: passed
                    ? null
                    : new[] { $"Inter-rater agreement (kappa={kappa:F3}) is below threshold ({_passThreshold:F3}). Review judge calibration." },
                SubResults: null,
                AggregationStrategy: "cohens_kappa"),
            Provenance: new("code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts a list of label strings from input metadata.
    /// Accepts: <see cref="IEnumerable{EvalResult}"/> or a JSON array string of objects
    /// with a <c>label</c> property.
    /// </summary>
    private static IReadOnlyList<string> ExtractLabels(EvalInput input)
    {
        if (input.Metadata is null || !input.Metadata.TryGetValue("judge_results", out var raw))
            return Array.Empty<string>();

        return raw switch
        {
            IEnumerable<EvalResult> results => results.Select(r => r.Score.Label).ToList(),
            IEnumerable<string> labels => labels.ToList(),
            string json => ParseJsonLabels(json),
            _ => Array.Empty<string>()
        };
    }

    private static IReadOnlyList<string> ParseJsonLabels(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            return doc.RootElement
                .EnumerateArray()
                .Select(el =>
                {
                    if (el.ValueKind == JsonValueKind.String)
                        return el.GetString() ?? "unknown";
                    if (el.TryGetProperty("label", out var lp))
                        return lp.GetString() ?? "unknown";
                    if (el.TryGetProperty("score", out var sp) &&
                        sp.TryGetProperty("label", out var slp))
                        return slp.GetString() ?? "unknown";
                    return "unknown";
                })
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Builds all pairwise (raterA, raterB) tuples from a list of labels.
    /// Each pair represents one observation for Cohen's kappa computation.
    /// </summary>
    private static IReadOnlyList<(string Expected, string Actual)> BuildPairs(IReadOnlyList<string> labels)
    {
        var pairs = new List<(string, string)>();
        for (int i = 0; i < labels.Count - 1; i++)
            for (int j = i + 1; j < labels.Count; j++)
                pairs.Add((labels[i], labels[j]));
        return pairs;
    }
}
