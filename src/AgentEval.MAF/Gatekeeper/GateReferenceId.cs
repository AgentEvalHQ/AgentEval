// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
    /// <summary>A short, opaque, unpredictable reference id — correlates the model-visible refusal with the full trace evidence.</summary>
    public static string New() => "gk_" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>The stable, non-revealing model-visible refusal body for <paramref name="referenceId"/>.</summary>
    public static string RefusalBody(string referenceId) => $$"""{"error":"ACTION_NOT_AUTHORIZED","referenceId":"{{referenceId}}"}""";
}
