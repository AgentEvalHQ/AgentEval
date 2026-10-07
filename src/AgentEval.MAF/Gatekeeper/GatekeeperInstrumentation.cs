// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// The Gatekeeper's OpenTelemetry surface: a shared <see cref="ActivitySource"/> for distributed tracing
/// and a <see cref="Meter"/> for metrics. Both are registered under the instrumentation name
/// <c>AgentEval.Gatekeeper</c> so callers can subscribe with a single name.
/// <para>
/// Usage — add OTel to your host and subscribe to this source/meter:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(GatekeeperInstrumentation.ActivitySourceName))
///     .WithMetrics(m => m.AddMeter(GatekeeperInstrumentation.MeterName));
/// </code>
/// </para>
/// </summary>
public static class GatekeeperInstrumentation
{
    /// <summary>The OTel activity source name. Subscribe to it in your tracing pipeline.</summary>
    public const string ActivitySourceName = "AgentEval.Gatekeeper";

    /// <summary>The OTel meter name. Subscribe to it in your metrics pipeline.</summary>
    public const string MeterName = "AgentEval.Gatekeeper";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    internal static readonly Meter Meter = new(MeterName);

    // --- Metrics instruments ---

    /// <summary>
    /// Counts actionable Gatekeeper findings (Block / Mutate / Redact / Incident-severity).
    /// Tags: <c>axis</c> (gate axis/policy name), <c>action</c> (the applied action), <c>severity</c>.
    /// </summary>
    internal static readonly Counter<long> FindingsCounter =
        Meter.CreateCounter<long>(
            "agenteval.gatekeeper.findings",
            unit: "{findings}",
            description: "Actionable Gatekeeper findings (Block / Mutate / Redact / Incident).");

    /// <summary>
    /// Histogram of calibration run duration (ms). Tags: <c>axis</c>, <c>inline_ready</c>.
    /// </summary>
    internal static readonly Histogram<double> CalibrationDurationMs =
        Meter.CreateHistogram<double>(
            "agenteval.gatekeeper.calibration_duration_ms",
            unit: "ms",
            description: "Duration of a GateCalibrationHarness.EvaluateAsync run, in milliseconds.");

    /// <summary>
    /// Gauge for the current calibration decisive accuracy. Tags: <c>axis</c>, <c>split</c>.
    /// </summary>
    internal static readonly ObservableGaugeStore CalibrationAccuracyStore = new();

    internal static readonly ObservableGauge<double> CalibrationAccuracyGauge =
        Meter.CreateObservableGauge<double>(
            "agenteval.gatekeeper.calibration_accuracy",
            observeValues: CalibrationAccuracyStore.Observe,
            unit: "1",
            description: "Last-recorded decisive accuracy from calibration (0–1). Tags: axis, split.");
}

/// <summary>Backing store for the calibration accuracy observable gauge — thread-safe, last-write-wins per (axis, split) key.</summary>
internal sealed class ObservableGaugeStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string axis, string split), double> _values = new();

    internal void Record(string axis, string split, double accuracy)
        => _values[(axis, split)] = accuracy;

    internal IEnumerable<Measurement<double>> Observe()
        => _values.Select(kv => new Measurement<double>(
            kv.Value,
            new KeyValuePair<string, object?>("axis", kv.Key.axis),
            new KeyValuePair<string, object?>("split", kv.Key.split)));
}
