// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals.Agentic.Telemetry;

namespace AgentEval.Evals.Agentic.Efficiency;

/// <summary>
/// Pure-code evaluator that measures how efficiently a benchmark run converts cost into quality.
/// <para>
/// Computes a <b>score-per-dollar</b> ratio and normalises it against a reference point
/// so that callers get a 0..1 metric they can include in composite benchmarks.
/// </para>
/// <para>
/// <b>Formula</b>:
/// <code>
/// scorePerDollar = scenarioScore / max(estimatedCostUsd, 0.001)
/// finalScore     = clamp(scorePerDollar / ReferenceEfficiency, 0.0, 1.0)
/// </code>
/// where <see cref="ReferenceEfficiency"/> = 5.0 (a mid-point calibrated so that a "good"
/// benchmark scoring 0.85 at $0.10/run gives ~8.5 score-per-dollar → finalScore ~1.0, and a
/// "wasteful" benchmark scoring 0.85 at $1.00/run gives ~0.85 → finalScore ~0.17).
/// </para>
/// <para>
/// <b>Input contract</b>:
/// <list type="bullet">
///   <item><c>EvalInput.Metadata["agentic_telemetry"]</c> → <see cref="AgenticTelemetry"/> for cost data.</item>
///   <item><c>EvalInput.Metadata["scenario_score"]</c> → <c>double</c> in [0..1] — the quality score from the benchmark run.</item>
/// </list>
/// If either key is absent the evaluator returns <see cref="EvalResult.Skipped(IEval, string)"/>.
/// </para>
/// <para>
/// <b>Cost tier: TRIVIAL</b> — pure code; no LLM call.
/// </para>
/// <para>
/// <b>Note</b>: <see cref="PassThreshold"/> defaults to 0.50 because this is a diagnostic /
/// comparative metric rather than a binary pass/fail gate.  A score below 0.50 signals that
/// the benchmark run is significantly less cost-efficient than the reference point, which is
/// a useful operational signal but not necessarily a quality failure.
/// </para>
/// </summary>
public sealed class CostQualityEfficiencyEval : IEval
{
    /// <summary>
    /// The reference score-per-dollar at which the normalised score equals 1.0.
    /// A run achieving score 1.0 at $0.20/run has scorePerDollar = 5.0 → finalScore = 1.0.
    /// Documented here so consumers can understand and override the reference point.
    /// </summary>
    public const double ReferenceEfficiency = 5.0;

    /// <summary>Metadata key for telemetry data.</summary>
    public const string TelemetryMetadataKey = "agentic_telemetry";

    /// <summary>Metadata key for the benchmark scenario quality score.</summary>
    public const string ScenarioScoreMetadataKey = "scenario_score";

    private const string KeyValue      = "cost_quality_efficiency";
    private const string NameValue     = "Cost-Quality Efficiency";
    private const string CategoryValue = "efficiency";
    private const string VersionValue  = "1.0.0";

    private readonly double _passThreshold;

    /// <summary>The pass threshold used by this evaluator.</summary>
    public double PassThreshold => _passThreshold;

    /// <inheritdoc/>
    public string Key      => KeyValue;

    /// <inheritdoc/>
    public string Name     => NameValue;

    /// <inheritdoc/>
    public string Category => CategoryValue;

    /// <inheritdoc/>
    public string Version  => VersionValue;

    /// <summary>
    /// Initialises a new <see cref="CostQualityEfficiencyEval"/>.
    /// </summary>
    /// <param name="passThreshold">
    /// Score fraction (0..1) at or above which the eval passes.
    /// Defaults to 0.50 — this is a comparative/diagnostic metric, not a strict quality gate.
    /// </param>
    public CostQualityEfficiencyEval(double passThreshold = 0.50)
    {
        _passThreshold = passThreshold;
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Resolve telemetry from metadata.
        AgenticTelemetry? telemetry = null;
        if (input.Metadata?.TryGetValue(TelemetryMetadataKey, out var rawTelemetry) == true)
            telemetry = rawTelemetry as AgenticTelemetry;

        // Resolve scenario score from metadata.
        double? scenarioScore = null;
        if (input.Metadata?.TryGetValue(ScenarioScoreMetadataKey, out var rawScore) == true)
        {
            scenarioScore = rawScore switch
            {
                double d  => d,
                float f   => (double)f,
                int i     => (double)i,
                _         => null
            };
        }

        if (telemetry is null || scenarioScore is null)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                $"CostQualityEfficiencyEval requires both " +
                $"EvalInput.Metadata[\"{TelemetryMetadataKey}\"] (AgenticTelemetry) and " +
                $"EvalInput.Metadata[\"{ScenarioScoreMetadataKey}\"] (double 0..1)."));
        }

        var score = scenarioScore.Value;
        var costUsd = telemetry.EstimatedCostUsd;
        var effectiveCost = Math.Max(costUsd, 0.001);
        var scorePerDollar = score / effectiveCost;
        var finalScore = Math.Clamp(scorePerDollar / ReferenceEfficiency, 0.0, 1.0);

        var passed = finalScore >= _passThreshold;
        var severity = passed ? "none" : "low";
        var label = passed ? "pass" : "fail";

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(finalScore, null, label, passed, _passThreshold, severity, 1.0),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["scenario_score"]       = score,
                    ["estimated_cost_usd"]   = costUsd,
                    ["score_per_dollar"]     = scorePerDollar,
                    ["reference_efficiency"] = ReferenceEfficiency,
                    ["efficiency_ratio"]     = finalScore,
                },
                Evidence:
                [
                    new EvalEvidence(
                        Source: "telemetry",
                        Reference: "cost_quality_efficiency",
                        Message:
                            $"Scenario score {score:F3} at estimated cost ${costUsd:F4} → " +
                            $"score-per-dollar {scorePerDollar:F2} " +
                            $"(reference = {ReferenceEfficiency:F1} → normalised efficiency = {finalScore:F3})."),
                ],
                Recommendations: passed ? null :
                [
                    $"Score-per-dollar ({scorePerDollar:F2}) is below the reference efficiency " +
                    $"threshold ({ReferenceEfficiency:F1}). Consider reducing token usage, " +
                    "switching to a cheaper model tier, or caching repeated prompts to improve cost efficiency."
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
