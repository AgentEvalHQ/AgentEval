// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Telemetry;

/// <summary>
/// Pure-code evaluator that measures end-to-end agent latency against configurable P95 and P99
/// thresholds.
/// <para>
/// <b>Score formula</b>:
/// <list type="bullet">
///   <item>1.0 if P99 &lt;= <c>p99ThresholdMs</c></item>
///   <item>0.0 if P99 &gt; 2 × <c>p99ThresholdMs</c></item>
///   <item>Linear interpolation between threshold and 2× threshold.</item>
/// </list>
/// </para>
/// <para>
/// <b>Severity</b>: <c>medium</c> when score is below pass threshold; <c>high</c> when P99
/// exceeds the threshold (even if still passing P95).
/// </para>
/// <para>
/// <b>Input contract</b>: telemetry data must be provided either via the constructor or via
/// <see cref="EvalInput.Metadata"/> under key <c>"agentic_telemetry"</c>
/// (an <see cref="AgenticTelemetry"/> instance). Per-input metadata takes precedence.
/// </para>
/// <para>
/// Foundry reference: no direct equivalent — AgentEval-original operational metric.
/// </para>
/// </summary>
public sealed class LatencyEval : IEval
{
    private const string KeyValue      = "latency";
    private const string NameValue     = "Latency";
    private const string CategoryValue = "operational";
    private const string VersionValue  = "1.0.0";

    /// <summary>
    /// Conventional metadata key for supplying <see cref="AgenticTelemetry"/> via
    /// <see cref="EvalInput.Metadata"/>.
    /// </summary>
    public const string MetadataKey = "agentic_telemetry";

    private readonly AgenticTelemetry? _data;
    private readonly double _p95ThresholdMs;
    private readonly double _p99ThresholdMs;
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
    /// Initialises a <see cref="LatencyEval"/> that reads telemetry from
    /// <see cref="EvalInput.Metadata"/> at evaluation time.
    /// </summary>
    /// <param name="p95ThresholdMs">P95 latency threshold in milliseconds. Default: 5,000 ms.</param>
    /// <param name="p99ThresholdMs">P99 latency threshold in milliseconds. Default: 10,000 ms.</param>
    /// <param name="passThreshold">Score threshold for a passing verdict. Default: 0.80.</param>
    public LatencyEval(double p95ThresholdMs = 5000, double p99ThresholdMs = 10000, double passThreshold = 0.80)
        : this(null, p95ThresholdMs, p99ThresholdMs, passThreshold) { }

    /// <summary>
    /// Initialises a <see cref="LatencyEval"/> with telemetry data supplied directly.
    /// The constructor-supplied data is used when <see cref="EvalInput.Metadata"/> does not
    /// contain the <c>"agentic_telemetry"</c> key.
    /// </summary>
    /// <param name="data">Telemetry data to use as a fallback when metadata is absent.</param>
    /// <param name="p95ThresholdMs">P95 latency threshold in milliseconds. Default: 5,000 ms.</param>
    /// <param name="p99ThresholdMs">P99 latency threshold in milliseconds. Default: 10,000 ms.</param>
    /// <param name="passThreshold">Score threshold for a passing verdict. Default: 0.80.</param>
    public LatencyEval(AgenticTelemetry? data, double p95ThresholdMs = 5000, double p99ThresholdMs = 10000, double passThreshold = 0.80)
    {
        _data = data;
        _p95ThresholdMs = p95ThresholdMs;
        _p99ThresholdMs = p99ThresholdMs;
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
                $"LatencyEval requires telemetry data via EvalInput.Metadata[\"{MetadataKey}\"] or constructor parameter."));
        }

        // Linear score: 1.0 at or below threshold, 0.0 at or above 2× threshold.
        var score = TelemetryHelper.LinearScore(telemetry.LatencyMsP99, _p99ThresholdMs);
        var passed = score >= _passThreshold;

        // Severity: high when P99 exceeds the threshold (regardless of pass/fail score band).
        var severity = telemetry.LatencyMsP99 > _p99ThresholdMs ? "high" : (passed ? "none" : "medium");

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["p50_ms"] = telemetry.LatencyMsP50,
                    ["p95_ms"] = telemetry.LatencyMsP95,
                    ["p99_ms"] = telemetry.LatencyMsP99,
                    ["p95_threshold_ms"] = _p95ThresholdMs,
                    ["p99_threshold_ms"] = _p99ThresholdMs,
                },
                Evidence:
                [
                    new EvalEvidence(
                        Source: "telemetry",
                        Reference: "latency",
                        Message: $"P99={telemetry.LatencyMsP99:F0} ms vs threshold {_p99ThresholdMs:F0} ms. " +
                                 $"P95={telemetry.LatencyMsP95:F0} ms vs threshold {_p95ThresholdMs:F0} ms."),
                ],
                Recommendations: passed ? null :
                [
                    $"P99 latency ({telemetry.LatencyMsP99:F0} ms) exceeds threshold ({_p99ThresholdMs:F0} ms). " +
                    "Investigate slow tool calls, LLM timeout configuration, or retry storms."
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
