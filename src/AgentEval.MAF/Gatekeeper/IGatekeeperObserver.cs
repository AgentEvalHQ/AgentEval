// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// An alerting / notification hook (Phase 3, P3-4) invoked on every actionable gate finding — an enforced block,
/// an applied mutation or redaction, or anything the classifier rated <see cref="GateSeverity.Incident"/> (e.g. a
/// tripped canary or a gate that failed closed because it could not evaluate). Wire it up as a
/// <see cref="GatekeeperOptions.EvidenceSink"/> via <see cref="ObserverEvidenceSink"/> (compose with a
/// <see cref="GateReferenceLedger"/> using <see cref="CompositeGateEvidenceSink"/> to run both). The callback runs
/// on the enforcement path, so it must be cheap and non-throwing — the sink isolates a throw, but a slow observer
/// slows the run.
/// </summary>
public interface IGatekeeperObserver
{
    /// <summary>Called with the full <see cref="GateEvidence"/> for one actionable finding.</summary>
    void OnFinding(GateEvidence evidence);
}

/// <summary>
/// Adapts an <see cref="IGatekeeperObserver"/> to an <see cref="IGateEvidenceSink"/> (P3-4): forwards only the
/// ACTIONABLE findings — <c>Block</c> / <c>Mutate</c> / <c>Redact</c>, plus anything of
/// <see cref="GateSeverity.Incident"/> severity — so a routine <c>Warn</c>/<c>Allow</c> record never pages anyone,
/// but a fail-closed incident always does.
/// </summary>
public sealed class ObserverEvidenceSink : IGateEvidenceSink
{
    private readonly IGatekeeperObserver _observer;

    /// <summary>Creates the sink over <paramref name="observer"/>.</summary>
    public ObserverEvidenceSink(IGatekeeperObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _observer = observer;
    }

    /// <inheritdoc/>
    public void Record(GateEvidence evidence, int sequence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (IsActionable(evidence))
        {
            _observer.OnFinding(evidence);
        }
    }

    private static bool IsActionable(GateEvidence evidence)
        => evidence.Severity == GateSeverity.Incident
           || evidence.Action is "Block" or "Mutate" or "Redact";
}
