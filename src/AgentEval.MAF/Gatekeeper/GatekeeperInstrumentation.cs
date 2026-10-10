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

    // Calibration runs are not on this meter: GateCalibrationHarness (AgentEval.Core) reports each run as a span on
    // its own AgentEval.Calibration source, with accuracy, dangerous errors and inline-ready as tags.
}
