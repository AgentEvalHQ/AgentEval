// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Agentic.Calibration;

namespace AgentEval.Evals.Agentic.Adjudication;

/// <summary>
/// Wraps a panel of judges (via <see cref="MultiJudgeWrapper"/>) and conditionally invokes
/// an adjudicator judge when inter-rater agreement falls below a configurable threshold.
/// <para>
/// <b>Adjudication flow:</b>
/// <list type="number">
///   <item>All panel judges run in parallel (same as <see cref="MultiJudgeWrapper"/>).</item>
///   <item>Pairwise agreement is computed across each judge pair's <c>Score.Label</c> strings
///     (e.g. <c>"pass"</c>, <c>"fail"</c>, <c>"warn"</c>).
///     Cohen's kappa is used when there are 3 or more judges; simple pairwise agreement rate
///     (mean over all pairs) is used for exactly 2 judges.</item>
///   <item>If agreement &gt;= <c>agreementThreshold</c>: aggregate the panel via
///     <c>aggregation</c> and return.</item>
///   <item>If agreement &lt; <c>agreementThreshold</c>: invoke the
///     <c>adjudicator</c> judge. Its verdict becomes the final result.
///     Panel disagreement is surfaced in <c>Details.Metadata</c>; <c>Disputed = true</c>
///     is recorded.</item>
/// </list>
/// </para>
/// <para>
/// <b>Output metadata keys</b> emitted in <c>Details.Dimensions</c>
/// (<see cref="System.Collections.Generic.IReadOnlyDictionary{TKey, TValue}"/> of
/// <c>string</c>→<c>double</c>):
/// <list type="bullet">
///   <item><c>agreement</c> — numeric agreement score (kappa or pairwise rate).</item>
///   <item><c>disputed</c> — <c>1.0</c> when adjudication was triggered, <c>0.0</c> otherwise.</item>
///   <item><c>adjudicated</c> — same as <c>disputed</c>.</item>
/// </list>
/// The canonical agreement-method signal (<c>"cohens_kappa"</c> vs <c>"pairwise"</c>)
/// is NOT emitted to <c>Dimensions</c> — that bag is <c>string</c>→<c>double</c> and
/// cannot carry a string value. Until a follow-up persists it to <c>Evidence</c>, the
/// nearest canonical signal is <see cref="EvalDetails.AggregationStrategy"/>
/// (e.g. <c>"WeightedMedian"</c> for adjudicated multi-judge runs, pinned by
/// <c>AgreementMethodSerialisationTests</c>).
/// </para>
/// </summary>
public sealed class AdjudicatedMultiJudgeWrapper : IEval
{
    private readonly IReadOnlyList<EvalComponent> _judges;
    private readonly IEval _adjudicator;
    private readonly double _agreementThreshold;
    private readonly IAggregationStrategy _aggregation;

    /// <inheritdoc/>
    public string Key { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string Category { get; }

    /// <inheritdoc/>
    public string Version { get; }

    /// <summary>
    /// Initialises a new <see cref="AdjudicatedMultiJudgeWrapper"/>.
    /// </summary>
    /// <param name="key">Eval key (carried through to result).</param>
    /// <param name="name">Human-readable name.</param>
    /// <param name="category">Category (typically <c>"agentic"</c> or a sub-category).</param>
    /// <param name="version">Eval version.</param>
    /// <param name="judges">Panel judges with weights. Passed directly to inner aggregation.</param>
    /// <param name="adjudicator">Adjudicator <see cref="IEval"/> invoked when agreement is below threshold.</param>
    /// <param name="agreementThreshold">
    /// If kappa or agreement rate is below this value, adjudication is triggered. Default 0.70.
    /// </param>
    /// <param name="aggregation">
    /// Inner aggregation strategy used when panel agrees. Defaults to
    /// <see cref="WeightedMedianAggregation.Instance"/>.
    /// </param>
    public AdjudicatedMultiJudgeWrapper(
        string key,
        string name,
        string category,
        string version,
        IReadOnlyList<EvalComponent> judges,
        IEval adjudicator,
        double agreementThreshold = 0.70,
        IAggregationStrategy? aggregation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(judges);
        ArgumentNullException.ThrowIfNull(adjudicator);
        if (judges.Count == 0)
            throw new ArgumentException(
                "AdjudicatedMultiJudgeWrapper must have at least one judge.", nameof(judges));
        if (agreementThreshold is < 0.0 or > 1.0)
            throw new ArgumentOutOfRangeException(
                nameof(agreementThreshold), agreementThreshold, "Agreement threshold must be in [0, 1].");

        Key = key;
        Name = name;
        Category = category;
        Version = version;
        _judges = judges;
        _adjudicator = adjudicator;
        _agreementThreshold = agreementThreshold;
        _aggregation = aggregation ?? WeightedMedianAggregation.Instance;
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // ── 1. Run all panel judges in parallel ───────────────────────────────
        var subTasks = _judges.Select(j => j.Eval.EvaluateAsync(input, ct)).ToArray();
        var panelResults = await Task.WhenAll(subTasks);

        // ── 2. Compute inter-rater agreement ──────────────────────────────────
        var (agreement, agreementMethod) = ComputeAgreement(panelResults);

        // ── 3. Decide: aggregate panel or adjudicate ──────────────────────────
        bool disputed = agreement < _agreementThreshold;
        EvalResult? adjudicatorResult = null;

        EvalScore finalScore;
        string finalAggStrategy;
        IReadOnlyList<EvalResult> allSubResults;

        if (!disputed)
        {
            // Agreement is sufficient — use panel aggregation
            var (score, severity) = _aggregation.Aggregate(panelResults, _judges);
            var label = severity switch
            {
                "critical" or "high" => "fail",
                "medium" => "warn",
                _ => "pass"
            };
            finalScore = new EvalScore(score, null, label, label == "pass", _agreementThreshold, severity, null);
            finalAggStrategy = _aggregation.Name;
            allSubResults = panelResults;
        }
        else
        {
            // Disagreement — invoke adjudicator
            adjudicatorResult = await _adjudicator.EvaluateAsync(input, ct);
            finalScore = adjudicatorResult.Score;
            finalAggStrategy = "Adjudicated";
            allSubResults = [.. panelResults, adjudicatorResult];
        }

        // ── 4. Build result metadata ──────────────────────────────────────────
        var dimensions = new Dictionary<string, double>
        {
            ["agreement"]   = agreement,
            ["disputed"]    = disputed ? 1.0 : 0.0,
            ["adjudicated"] = disputed ? 1.0 : 0.0,
        };

        var recommendations = disputed
            ? new[] { $"Panel disagreement detected (agreement={agreement:F3} < threshold={_agreementThreshold:F3}). Adjudicator verdict applied." }
            : null;

        var cost = panelResults.Sum(r => r.Provenance.EstimatedCost)
                 + (adjudicatorResult?.Provenance.EstimatedCost ?? 0.0);

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: finalScore,
            Details: new(
                Dimensions: dimensions,
                Evidence: null,
                Recommendations: recommendations,
                SubResults: allSubResults,
                AggregationStrategy: finalAggStrategy),
            Provenance: new("multi-judge-adjudicated", null, null, null, null, cost, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // ── Agreement computation helpers ─────────────────────────────────────────

    /// <summary>
    /// Returns (agreementScore, methodName) using Cohen's kappa for ≥3 judges
    /// and pairwise agreement rate for exactly 2 judges.
    /// Negative kappa values are clamped to 0.
    /// </summary>
    private static (double Agreement, string Method) ComputeAgreement(EvalResult[] panelResults)
    {
        if (panelResults.Length <= 1)
            return (1.0, "pairwise"); // Trivially agree with single judge

        if (panelResults.Length == 2)
        {
            // Simple pairwise agreement
            var agree = string.Equals(
                panelResults[0].Score.Label,
                panelResults[1].Score.Label,
                StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
            return (agree, "pairwise");
        }

        // 3+ judges: use Cohen's kappa across all pairs, averaged
        // We compute all pairwise kappas and return the mean.
        var kappas = new List<double>();
        for (int i = 0; i < panelResults.Length - 1; i++)
        {
            for (int j = i + 1; j < panelResults.Length; j++)
            {
                // Each judge pair is treated as (rater1, rater2) with one observation each.
                // For multi-item (cross-run) kappa you would use CalibrationMetrics.CohensKappa
                // over a dataset. Here the panel evaluates the SAME single input, so we compute
                // single-observation pairwise kappa for each pair and average.
                var pair = new (string Expected, string Actual)[]
                {
                    (panelResults[i].Score.Label, panelResults[j].Score.Label)
                };
                var k = CalibrationMetrics.CohensKappa(pair);
                // F-004 split semantics (2026-05-24): NaN here means "the two raters returned
                // the same single label" — pe=1.0 (degenerate marginals on a one-observation
                // pair). For pair-agreement this IS perfect agreement, not "undefined". The
                // honest-NaN signal exists for calibration-dataset use where single-class
                // means the golden lacks balance; for in-panel scoring we want the historical
                // 1.0 behaviour. Normalise here, keep NaN at the calibration boundary.
                if (double.IsNaN(k)) k = 1.0;
                kappas.Add(k);
            }
        }

        var meanKappa = kappas.Count > 0 ? kappas.Average() : 1.0;
        // Clamp negative kappa to 0 (worse-than-chance → treat as zero agreement)
        return (Math.Max(0.0, meanKappa), "cohens_kappa");
    }
}
