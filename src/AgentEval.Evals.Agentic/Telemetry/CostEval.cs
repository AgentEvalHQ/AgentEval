// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Telemetry;

/// <summary>
/// Pure-code evaluator that measures the estimated monetary cost of a run against a configurable
/// USD budget.
/// <para>
/// <b>Score formula</b>:
/// <list type="bullet">
///   <item>1.0 if cost &lt;= <c>budgetUsd</c></item>
///   <item>0.0 if cost &gt; 2 × <c>budgetUsd</c></item>
///   <item>Linear interpolation between budget and 2× budget.</item>
/// </list>
/// </para>
/// <para>
/// <b>Severity</b>: <c>low</c> — cost overruns are a financial signal, not a quality failure.
/// </para>
/// <para>
/// <b>Input contract</b>: telemetry data must be provided either via the constructor or via
/// <see cref="EvalInput.Metadata"/> under key <c>"agentic_telemetry"</c>
/// (an <see cref="AgenticTelemetry"/> instance). Per-input metadata takes precedence.
/// </para>
/// <para>
/// Cost estimation is the caller's responsibility. A run whose cost was not tracked carries
/// <see cref="AgenticTelemetry.EstimatedCostUsd"/> <see langword="null"/>, and the evaluator returns
/// <see cref="EvalResult.Skipped(IEval, string)"/> (not measured) for it. It used to be 0 for "not tracked", which this
/// evaluator scored 1.0 unconditionally: an untracked cost passed every budget (T82). A reported 0 is a measured zero
/// and is scored.
/// </para>
/// </summary>
public sealed class CostEval : IEvalConfigurationSource
{
    private const string KeyValue      = "cost";
    private const string NameValue     = "Cost";
    private const string CategoryValue = "operational";
    // 1.1.0: a cost that was not tracked (EstimatedCostUsd null) is skipped, not scored as $0 within budget (T82, D-31).
    // Keep EvaluatorCards/cost.json and tools/gen_evaluator_cards.py on the same version.
    private const string VersionValue  = "1.1.0";

    private readonly AgenticTelemetry? _data;
    private readonly double _budgetUsd;
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
    /// Initialises a <see cref="CostEval"/> that reads telemetry from
    /// <see cref="EvalInput.Metadata"/> at evaluation time.
    /// </summary>
    /// <param name="budgetUsd">Maximum acceptable estimated cost per run in USD. Default: $1.00.</param>
    /// <param name="passThreshold">Score threshold for a passing verdict. Default: 0.80.</param>
    public CostEval(double budgetUsd = 1.00, double passThreshold = 0.80)
        : this(null, budgetUsd, passThreshold) { }

    /// <summary>
    /// Initialises a <see cref="CostEval"/> with telemetry data supplied directly.
    /// The constructor-supplied data is used when <see cref="EvalInput.Metadata"/> does not
    /// contain the <c>"agentic_telemetry"</c> key.
    /// </summary>
    /// <param name="data">Telemetry data to use as a fallback when metadata is absent.</param>
    /// <param name="budgetUsd">Maximum acceptable estimated cost per run in USD. Default: $1.00.</param>
    /// <param name="passThreshold">Score threshold for a passing verdict. Default: 0.80.</param>
    public CostEval(AgenticTelemetry? data, double budgetUsd = 1.00, double passThreshold = 0.80)
    {
        _data          = data;
        _budgetUsd     = budgetUsd;
        _passThreshold = passThreshold;
    }

    /// <summary>Captures evaluation policy; measured telemetry and prior results remain subject evidence.</summary>
    public global::System.Text.Json.Nodes.JsonObject CaptureConfiguration() => new()
    {
        ["budgetUsd"] = _budgetUsd,
        ["passThreshold"] = _passThreshold,
    };

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var telemetry = TelemetryHelper.Resolve(input, _data);
        if (telemetry is null)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                $"CostEval requires telemetry data via EvalInput.Metadata[\"{LatencyEval.MetadataKey}\"] or constructor parameter."));
        }

        // Not tracked is not measured, never $0: a cost nobody tracked is not within budget, and not over it either.
        if (telemetry.EstimatedCostUsd is not { } costUsd)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "The run's cost was not tracked (AgenticTelemetry.EstimatedCostUsd is null), so Cost was not measured: " +
                "an untracked cost is not $0 and is never read as within budget."));
        }

        var score = TelemetryHelper.LinearScore(costUsd, _budgetUsd);
        var passed = score >= _passThreshold;
        var severity = passed ? "none" : "low";

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["cost_usd"]      = costUsd,
                    ["budget_usd"]    = _budgetUsd,
                    ["overage_ratio"] = _budgetUsd > 0 ? costUsd / _budgetUsd : 0,
                },
                Evidence:
                [
                    new EvalEvidence(
                        Source: "telemetry",
                        Reference: "cost",
                        Message: $"Estimated cost: ${costUsd:F4} vs budget ${_budgetUsd:F2} " +
                                 $"({costUsd / Math.Max(1e-9, _budgetUsd):P0} of budget)."),
                ],
                Recommendations: passed ? null :
                [
                    $"Estimated cost (${costUsd:F4}) exceeds budget (${_budgetUsd:F2}). " +
                    "Consider reducing token usage, switching to a cheaper model tier, or caching repeated prompts."
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
