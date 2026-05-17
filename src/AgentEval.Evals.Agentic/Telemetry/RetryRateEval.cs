// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Telemetry;

/// <summary>
/// Pure-code evaluator that measures the retry rate of agent calls in a run.
/// <para>
/// <b>Score formula</b>: <c>1.0 - (RetryCount / TotalCalls)</c>, clamped to [0, 1].
/// A run with no retries scores 1.0; a run with as many retries as total calls scores 0.0.
/// </para>
/// <para>
/// <b>Default pass threshold</b>: 0.90 — the agent passes when the retry rate is at most 10%.
/// </para>
/// <para>
/// <b>Severity</b>: <c>low</c> — retries indicate transient instability but not outright
/// failure. Elevated retry rates should trigger investigation of upstream service reliability.
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
public sealed class RetryRateEval : IEval
{
    private const string KeyValue      = "retry_rate";
    private const string NameValue     = "Retry Rate";
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
    /// Initialises a <see cref="RetryRateEval"/> that reads telemetry from
    /// <see cref="EvalInput.Metadata"/> at evaluation time.
    /// </summary>
    /// <param name="passThreshold">
    /// Score threshold for a passing verdict. Default: 0.90, meaning at most 10% retry rate.
    /// </param>
    public RetryRateEval(double passThreshold = 0.90)
        : this(null, passThreshold) { }

    /// <summary>
    /// Initialises a <see cref="RetryRateEval"/> with telemetry data supplied directly.
    /// The constructor-supplied data is used when <see cref="EvalInput.Metadata"/> does not
    /// contain the <c>"agentic_telemetry"</c> key.
    /// </summary>
    /// <param name="data">Telemetry data to use as a fallback when metadata is absent.</param>
    /// <param name="passThreshold">
    /// Score threshold for a passing verdict. Default: 0.90, meaning at most 10% retry rate.
    /// </param>
    public RetryRateEval(AgenticTelemetry? data, double passThreshold = 0.90)
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
                $"RetryRateEval requires telemetry data via EvalInput.Metadata[\"{LatencyEval.MetadataKey}\"] or constructor parameter."));
        }

        if (telemetry.TotalCalls == 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "RetryRateEval: TotalCalls is 0 — cannot compute retry rate."));
        }

        var retryRate = Math.Clamp((double)telemetry.RetryCount / telemetry.TotalCalls, 0.0, 1.0);
        var score = Math.Clamp(1.0 - retryRate, 0.0, 1.0);
        var passed = score >= _passThreshold;
        var severity = passed ? "none" : "low";

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["retry_count"]  = telemetry.RetryCount,
                    ["total_calls"]  = telemetry.TotalCalls,
                    ["retry_rate"]   = retryRate,
                    ["success_rate"] = score,
                },
                Evidence:
                [
                    new EvalEvidence(
                        Source: "telemetry",
                        Reference: "retry_rate",
                        Message: $"{telemetry.RetryCount} retry(ies) in {telemetry.TotalCalls} call(s) " +
                                 $"(retry rate: {retryRate:P1})."),
                ],
                Recommendations: passed ? null :
                [
                    $"Retry rate ({retryRate:P1}) exceeds the acceptable threshold (>{1.0 - _passThreshold:P0}). " +
                    "Investigate upstream service reliability, timeout settings, and transient error patterns."
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
