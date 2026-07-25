// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, #12) — the stable, non-revealing shape a blocked call's model-visible refusal takes.
/// A prior version of <c>SynthesizedRefusal</c>/<c>SynthRefusal</c> returned
/// <c>"BLOCKED by policy '{policyName}': {reason}. Choose a different action."</c> directly as tool-result
/// content — leaking the exact policy name and block reason to the model, which can use that as a signal to
/// probe for a bypass (does the retry get a different reason? does rephrasing avoid the named policy?). The
/// full policy name and reason still go into the trace evidence (<c>gate.tool.*</c>/<c>gate.run-*.*</c>) —
/// audit-visible, never model-visible.
/// </summary>
internal static class GateReferenceId
{
    /// <summary>A short, opaque, unpredictable reference id — correlates the model-visible refusal with the full trace evidence. Delegates to the shared, dependency-free <see cref="GateCorrelationId"/> (also used by <c>EvalGateRefusalException</c> in Core) so the two never drift.</summary>
    public static string New() => GateCorrelationId.New();

    /// <summary>
    /// The stable, non-revealing model-visible refusal body for <paramref name="referenceId"/> — the versioned
    /// <see cref="GatekeeperRefusalContract"/> envelope (Phase 4, P4-1/P4-2/P4-3), namespaced under
    /// <c>_gatekeeper</c> so the model can tell a gate refusal from a tool's own error, carrying a coarse
    /// <paramref name="disposition"/> and — when tracked — how many equivalent <paramref name="attempts"/> were denied.
    /// </summary>
    public static string RefusalBody(string referenceId, RefusalDisposition disposition = RefusalDisposition.Denied, int? attempts = null)
        => GatekeeperRefusalContract.Build(referenceId, disposition, attempts);
}
