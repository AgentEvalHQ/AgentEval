// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// How urgently a gate finding should be triaged (Phase 3, P3-5) — a field on the unified
/// <see cref="GateEvidence"/> record, stamped at record time so alerting / triage can route without re-deriving
/// intent from the policy name.
/// </summary>
public enum GateSeverity
{
    /// <summary>Expected, low-stakes enforcement — a budget cap, a rate limit, an ordinary policy block.</summary>
    Routine,

    /// <summary>A finding that suggests probing or manipulation — taint/exfiltration, out-of-order sequencing, referential-integrity mismatch.</summary>
    Suspicious,

    /// <summary>A high-signal event demanding attention — a tripped canary/honeypot, or a gate that failed closed because it could not even evaluate (a defect).</summary>
    Incident,
}
