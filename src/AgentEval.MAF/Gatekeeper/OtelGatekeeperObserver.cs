// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// An <see cref="IGatekeeperObserver"/> that bridges actionable Gatekeeper findings to OpenTelemetry:
/// each call to <see cref="OnFinding"/> emits an <see cref="Activity"/> span and increments the
/// <c>agenteval.gatekeeper.findings</c> counter declared on <see cref="GatekeeperInstrumentation"/>.
/// <para>
/// Wire it up via <see cref="ObserverEvidenceSink"/> and compose with any other sink using
/// <c>CompositeGateEvidenceSink</c>:
/// <code>
/// var otelObserver = new OtelGatekeeperObserver();
/// options.EvidenceSink = new CompositeGateEvidenceSink(
///     ledger,
///     new ObserverEvidenceSink(otelObserver));
/// </code>
/// </para>
/// <para>
/// Only actionable findings reach this observer (Block / Mutate / Redact, or Incident-severity) —
/// routine Warn / Allow records never leave <see cref="ObserverEvidenceSink"/>.
/// </para>
/// </summary>
public sealed class OtelGatekeeperObserver : IGatekeeperObserver
{
    /// <inheritdoc/>
    public void OnFinding(GateEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        // Emit an Activity span so the finding appears in distributed traces.
        using var activity = GatekeeperInstrumentation.ActivitySource
            .StartActivity("agenteval.gatekeeper.finding", ActivityKind.Internal);

        if (activity is not null)
        {
            activity.SetTag("agenteval.gatekeeper.reference_id", evidence.ReferenceId);
            activity.SetTag("agenteval.gatekeeper.stage", evidence.Stage);
            activity.SetTag("agenteval.gatekeeper.policy", evidence.Policy);
            activity.SetTag("agenteval.gatekeeper.action", evidence.Action);
            activity.SetTag("agenteval.gatekeeper.severity", evidence.Severity.ToString());
            if (evidence.RunId is not null) activity.SetTag("agenteval.gatekeeper.run_id", evidence.RunId);
            if (evidence.AgentName is not null) activity.SetTag("agenteval.gatekeeper.agent_name", evidence.AgentName);
            if (evidence.ToolName is not null) activity.SetTag("agenteval.gatekeeper.tool_name", evidence.ToolName);
        }

        // Increment the findings counter with the key dimensions.
        GatekeeperInstrumentation.FindingsCounter.Add(1,
            new KeyValuePair<string, object?>("axis", evidence.Policy),
            new KeyValuePair<string, object?>("action", evidence.Action),
            new KeyValuePair<string, object?>("severity", evidence.Severity.ToString()),
            new KeyValuePair<string, object?>("stage", evidence.Stage));
    }
}
