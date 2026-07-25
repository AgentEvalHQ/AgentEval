// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A signed, time-bounded quarantine record (Phase 2, P2-9) — the recoverable replacement for the boolean-forever
/// quarantine flag <see cref="QuarantineGate"/> reads. A quarantine that can never be lifted turns a single
/// (possibly injected / false-positive) escalation into a permanent denial of service on a legitimate session; a
/// lease bounds that in three ways: it <b>expires</b> (auto-recovery), it can be <b>lifted by an authorized
/// operator</b> (manual recovery), and each arming is bound to a distinct <see cref="EvidenceId"/> so the same
/// stale evidence cannot silently re-arm/extend it forever.
/// </summary>
/// <param name="Reason">Why the session was quarantined (audit-visible).</param>
/// <param name="EvidenceId">The id of the evidence that justified arming — re-arming to EXTEND requires FRESH evidence (a new id).</param>
/// <param name="ArmedAtUtc">When the lease was armed.</param>
/// <param name="ExpiresAtUtc">When the lease auto-expires (time-bound recovery).</param>
/// <param name="LiftedByOperator">The operator identity that lifted the lease early, or null if still armed.</param>
/// <param name="Signature">An HMAC over the fields above, proving the lease was issued by the key-holder (<see cref="QuarantineLeaseAuthority"/>) and not tampered with.</param>
public sealed record QuarantineLease(
    string Reason,
    string EvidenceId,
    DateTimeOffset ArmedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string? LiftedByOperator,
    string Signature)
{
    /// <summary>
    /// True when the lease still quarantines the session at <paramref name="nowUtc"/> — i.e. it has NOT been
    /// operator-lifted and has NOT yet expired. Signature authenticity is a separate check
    /// (<see cref="QuarantineLeaseAuthority.Verify"/>) the gate performs first.
    /// </summary>
    public bool IsActiveAt(DateTimeOffset nowUtc) => LiftedByOperator is null && nowUtc < ExpiresAtUtc;
}
