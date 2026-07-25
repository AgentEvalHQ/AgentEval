// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Default triage severity for a gate finding (Phase 3, P3-5), stamped onto <see cref="GateEvidence.Severity"/> at
/// record time. A heuristic over the built-in gates' policy names — the plan's mapping: budgets/rate limits are
/// <see cref="GateSeverity.Routine"/>; taint / sequence / referential-integrity / same-batch findings signal
/// probing (<see cref="GateSeverity.Suspicious"/>); a tripped canary/honeypot OR a gate that failed closed because
/// it could not even evaluate is an <see cref="GateSeverity.Incident"/>. Intentionally conservative: an unknown
/// policy defaults to <see cref="GateSeverity.Routine"/> so severity never over-alarms without cause.
/// </summary>
public static class GateSeverityClassifier
{
    /// <summary>Classifies a finding by its policy name; <paramref name="failedClosedOnThrow"/> forces Incident (a gate that threw is a defect, not an ordinary block).</summary>
    public static GateSeverity Classify(string? policyName, bool failedClosedOnThrow = false)
    {
        if (failedClosedOnThrow)
        {
            return GateSeverity.Incident;
        }

        var policy = policyName ?? string.Empty;

        if (ContainsAny(policy, "Canary", "Honeypot"))
        {
            return GateSeverity.Incident;
        }

        if (ContainsAny(policy, "Taint", "Sequence", "SameBatch", "ReferentialIntegrity", "Exfil", "Quarantine"))
        {
            return GateSeverity.Suspicious;
        }

        return GateSeverity.Routine;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
