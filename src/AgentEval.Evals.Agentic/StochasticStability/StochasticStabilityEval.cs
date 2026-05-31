// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Evals.Agentic.StochasticStability;

/// <summary>
/// Pure-code meta-evaluator that measures whether an agent produces consistently reliable
/// results across multiple independent runs on the same input (per master analysis §10.5).
/// <para>
/// This evaluator does not call an LLM. It consumes N prior <see cref="EvalResult"/> objects
/// (from the same agent, same input, across N runs) and produces a single stability score.
/// </para>
/// <para>
/// <b>Input contract</b> (<see cref="EvalInput.Metadata"/> keys):
/// <list type="bullet">
///   <item>
///     <c>"run_results"</c> (required) — an <see cref="IEnumerable{EvalResult}"/> of results
///     from N independent runs of the same agent on the same input. Minimum 2 results required.
///     Also accepted: a JSON array string of objects with at minimum <c>score.value</c> (double)
///     and <c>score.passed</c> (bool) fields.
///   </item>
/// </list>
/// At least 2 run results are required; fewer returns a skipped result.
/// </para>
/// <para>
/// <b>Score components</b> (combined via weighted sum):
/// <list type="bullet">
///   <item>
///     <b>success_rate_across_runs</b> (weight 0.50): fraction of runs that passed.
///     A stable, capable agent should pass consistently.
///   </item>
///   <item>
///     <b>score_variance_inverse</b> (weight 0.30): <c>clamp(1 - variance / referenceVariance, 0, 1)</c>
///     — a linear inverse using a reference variance of 0.10 (variance 0 → 1.0; variance ≥ 0.10 → 0.0).
///     Low score variance indicates the agent produces consistently calibrated outputs rather than
///     wildly oscillating.
///   </item>
///   <item>
///     <b>failure_mode_consistency</b> (weight 0.20): when failures exist, measures whether
///     they share the same label. Consistent failure modes signal predictable (debuggable)
///     failure patterns as opposed to random degradation.
///   </item>
/// </list>
/// </para>
/// <para>
/// <b>Severity</b>: <c>medium</c> — high stochastic variance can mask both regression and
/// improvement, making evaluation results unreliable for CI gates or compliance evidence.
/// </para>
/// </summary>
public sealed class StochasticStabilityEval : IEval
{
    private const string KeyValue      = "stochastic_stability";
    private const string NameValue     = "Stochastic Stability";
    private const string CategoryValue = "operational";
    private const string VersionValue  = "1.0.0";

    /// <summary>
    /// Conventional metadata key for supplying run results via <see cref="EvalInput.Metadata"/>.
    /// </summary>
    public const string MetadataRunResultsKey = "run_results";

    // Reference variance used to normalize variance_inverse to [0, 1].
    // At variance = 0 → normalized = 1.0 (perfectly stable).
    // At variance >= ReferenceVariance → normalized = 0.0 (maximally unstable for this scale).
    private const double ReferenceVariance = 0.10;

    private readonly double _passThreshold;

    /// <inheritdoc/>
    public string Key      => KeyValue;

    /// <inheritdoc/>
    public string Name     => NameValue;

    /// <inheritdoc/>
    public string Category => CategoryValue;

    /// <inheritdoc/>
    public string Version  => VersionValue;

    /// <summary>
    /// Initialises a new <see cref="StochasticStabilityEval"/>.
    /// </summary>
    /// <param name="passThreshold">Score threshold for a passing verdict. Default: 0.80.</param>
    public StochasticStabilityEval(double passThreshold = 0.80)
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

        var runResults = ExtractRunResults(input);
        if (runResults.Count < 2)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                $"StochasticStabilityEval requires at least 2 run results in " +
                $"EvalInput.Metadata[\"{MetadataRunResultsKey}\"]. Got {runResults.Count}."));
        }

        // ── Compute the three sub-dimensions ─────────────────────────────────────

        // 1. Success rate across runs.
        double successRate = (double)runResults.Count(r => r.Passed) / runResults.Count;

        // 2. Score variance inverse (normalized).
        var scores = runResults.Select(r => r.Score).ToList();
        var mean = scores.Average();
        var variance = scores.Sum(s => (s - mean) * (s - mean)) / runResults.Count;
        // Normalize: at variance=0, normalized=1.0; at variance>=ReferenceVariance, normalized=0.0.
        var normalizedVarianceInverse = Math.Clamp(1.0 - variance / ReferenceVariance, 0.0, 1.0);

        // 3. Failure-mode consistency.
        var failedLabels = runResults.Where(r => !r.Passed).Select(r => r.Label).ToList();
        double failureModeConsistency;
        if (failedLabels.Count == 0)
        {
            // No failures — perfect consistency.
            failureModeConsistency = 1.0;
        }
        else
        {
            // Fraction of failed runs sharing the most common failure label.
            var mostCommonCount = failedLabels
                .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
                .Max(g => g.Count());
            failureModeConsistency = (double)mostCommonCount / failedLabels.Count;
        }

        // ── Weighted sum ──────────────────────────────────────────────────────────
        const double weightSuccessRate         = 0.50;
        const double weightVarianceInverse     = 0.30;
        const double weightFailureModeConsistency = 0.20;

        var compositeScore =
            weightSuccessRate * successRate +
            weightVarianceInverse * normalizedVarianceInverse +
            weightFailureModeConsistency * failureModeConsistency;

        var finalScore = Math.Clamp(compositeScore, 0.0, 1.0);
        var passed = finalScore >= _passThreshold;
        var severity = passed ? "none" : "medium";

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(finalScore, null, passed ? "pass" : "fail", passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["run_count"]                    = runResults.Count,
                    ["success_rate_across_runs"]     = successRate,
                    ["score_mean"]                   = mean,
                    ["score_variance"]               = variance,
                    ["score_variance_inverse_norm"]  = normalizedVarianceInverse,
                    ["failure_mode_consistency"]     = failureModeConsistency,
                    ["failed_run_count"]             = failedLabels.Count,
                },
                Evidence:
                [
                    new EvalEvidence(
                        Source: "deterministic",
                        Reference: "stochastic_stability",
                        Message: $"{runResults.Count} runs: success_rate={successRate:P0}, " +
                                 $"score_variance={variance:F4} (normalized_inv={normalizedVarianceInverse:F3}), " +
                                 $"failure_mode_consistency={failureModeConsistency:P0}. " +
                                 $"Composite={finalScore:F3}."),
                ],
                Recommendations: passed ? null : BuildRecommendations(successRate, normalizedVarianceInverse, failureModeConsistency),
                SubResults: null,
                AggregationStrategy: "weighted_sum(success_rate=0.50, variance_inverse=0.30, failure_mode_consistency=0.20)"),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed record RunSummary(double Score, bool Passed, string Label);

    private static IReadOnlyList<RunSummary> ExtractRunResults(EvalInput input)
    {
        if (input.Metadata is null || !input.Metadata.TryGetValue(MetadataRunResultsKey, out var raw))
            return Array.Empty<RunSummary>();

        return raw switch
        {
            IEnumerable<EvalResult> results  => results.Select(r => new RunSummary(r.Score.Value, r.Score.Passed, r.Score.Label)).ToList(),
            IEnumerable<RunSummary> summaries => summaries.ToList(),
            string json                       => ParseJsonRunResults(json),
            _                                 => TryConvertEnumerable(raw),
        };
    }

    private static IReadOnlyList<RunSummary> ParseJsonRunResults(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<RunSummary>();

            var results = new List<RunSummary>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                double scoreValue = 0;
                bool passed = false;
                string label = "unknown";

                // Support both flat {"value":..., "passed":...} and nested {"score":{"value":...}}
                if (el.TryGetProperty("score", out var scoreEl))
                {
                    if (scoreEl.TryGetProperty("value",  out var v))  scoreValue = v.GetDouble();
                    if (scoreEl.TryGetProperty("passed", out var p))  passed     = p.GetBoolean();
                    if (scoreEl.TryGetProperty("label",  out var l))  label      = l.GetString() ?? "unknown";
                }
                else
                {
                    if (el.TryGetProperty("value",  out var v))  scoreValue = v.GetDouble();
                    if (el.TryGetProperty("passed", out var p))  passed     = p.GetBoolean();
                    if (el.TryGetProperty("label",  out var l))  label      = l.GetString() ?? "unknown";
                }

                results.Add(new RunSummary(scoreValue, passed, label));
            }
            return results;
        }
        catch (JsonException)
        {
            return Array.Empty<RunSummary>();
        }
    }

    private static IReadOnlyList<RunSummary> TryConvertEnumerable(object raw)
    {
        if (raw is global::System.Collections.IEnumerable enumerable)
        {
            var result = new List<RunSummary>();
            foreach (var item in enumerable)
            {
                if (item is EvalResult er)
                    result.Add(new RunSummary(er.Score.Value, er.Score.Passed, er.Score.Label));
            }
            return result;
        }
        return Array.Empty<RunSummary>();
    }

    private static IReadOnlyList<string> BuildRecommendations(
        double successRate,
        double normalizedVarianceInverse,
        double failureModeConsistency)
    {
        var recs = new List<string>();

        if (successRate < 0.80)
            recs.Add($"Success rate across runs ({successRate:P0}) is below 80%. The agent fails more than 1 in 5 times on this input — investigate systemic failure causes.");

        if (normalizedVarianceInverse < 0.70)
            recs.Add("High score variance detected. The agent produces inconsistent output quality across runs — review nondeterministic prompt templates, temperature settings, or tool response variability.");

        if (failureModeConsistency < 0.70)
            recs.Add("Failures are inconsistent across runs (different failure modes). This is harder to debug than consistent failure — consider adding structured failure classification to tool results.");

        if (recs.Count == 0)
            recs.Add("Stability is marginal. Collect more runs to confirm the trend before making CI gate decisions.");

        return recs;
    }
}
