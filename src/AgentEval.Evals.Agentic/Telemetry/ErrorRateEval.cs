// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Telemetry;

/// <summary>
/// Pure-code evaluator that measures the error rate of agent calls (LLM + tool) in a run.
/// <para>
/// <b>Score formula</b>: <c>1.0 - (ErrorCount / TotalCalls)</c>, clamped to [0, 1].
/// A run with zero errors scores 1.0; a run with all calls erroring scores 0.0.
/// </para>
/// <para>
/// <b>Default pass threshold</b>: 0.95 — the agent passes when the error rate is at most 5%.
/// </para>
/// <para>
/// <b>Severity</b>: <c>medium</c> when failing. Errors in agentic workflows can compound:
/// a single tool failure often cascades into downstream quality degradation.
/// </para>
/// <para>
/// <b>Input contract</b>: telemetry data must be provided either via the constructor or via
/// <see cref="EvalInput.Metadata"/> under key <c>"agentic_telemetry"</c>
/// (an <see cref="AgenticTelemetry"/> instance). Per-input metadata takes precedence.
/// </para>
/// <para>
/// If <see cref="AgenticTelemetry.TotalCalls"/> is zero, the evaluator returns a skipped result
/// to avoid division by zero.
/// </para>
/// </summary>
public sealed class ErrorRateEval : IEval
{
    private const string KeyValue      = "error_rate";
    private const string NameValue     = "Error Rate";
    private const string CategoryValue = "operational";
    private const string VersionValue  = "1.0.0";

    private readonly AgenticTelemetry? _data;
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
    /// Initialises an <see cref="ErrorRateEval"/> that reads telemetry from
    /// <see cref="EvalInput.Metadata"/> at evaluation time.
    /// </summary>
    /// <param name="passThreshold">
    /// Score threshold for a passing verdict. Default: 0.95, meaning at most 5% error rate.
    /// </param>
    public ErrorRateEval(double passThreshold = 0.95)
        : this(null, passThreshold) { }

    /// <summary>
    /// Initialises an <see cref="ErrorRateEval"/> with telemetry data supplied directly.
    /// The constructor-supplied data is used when <see cref="EvalInput.Metadata"/> does not
    /// contain the <c>"agentic_telemetry"</c> key.
    /// </summary>
    /// <param name="data">Telemetry data to use as a fallback when metadata is absent.</param>
    /// <param name="passThreshold">
    /// Score threshold for a passing verdict. Default: 0.95, meaning at most 5% error rate.
    /// </param>
    public ErrorRateEval(AgenticTelemetry? data, double passThreshold = 0.95)
    {
        _data          = data;
        _passThreshold = passThreshold;
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var telemetry = TelemetryHelper.Resolve(input, _data);
        if (telemetry is null)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                $"ErrorRateEval requires telemetry data via EvalInput.Metadata[\"{LatencyEval.MetadataKey}\"] or constructor parameter."));
        }

        if (telemetry.TotalCalls == 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "ErrorRateEval: TotalCalls is 0 — cannot compute error rate."));
        }

        var errorRate = Math.Clamp((double)telemetry.ErrorCount / telemetry.TotalCalls, 0.0, 1.0);
        var score = Math.Clamp(1.0 - errorRate, 0.0, 1.0);
        var passed = score >= _passThreshold;
        var severity = passed ? "none" : "medium";

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["error_count"]  = telemetry.ErrorCount,
                    ["total_calls"]  = telemetry.TotalCalls,
                    ["error_rate"]   = errorRate,
                    ["success_rate"] = score,
                },
                Evidence:
                [
                    new EvalEvidence(
                        Source: "telemetry",
                        Reference: "error_rate",
                        Message: $"{telemetry.ErrorCount} error(s) in {telemetry.TotalCalls} call(s) " +
                                 $"(error rate: {errorRate:P1})."),
                ],
                Recommendations: passed ? null :
                [
                    $"Error rate ({errorRate:P1}) exceeds the acceptable threshold (>{1.0 - _passThreshold:P0}). " +
                    "Review tool failure logs and consider adding retry budgets or circuit-breaker patterns."
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
