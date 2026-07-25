// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Issues and verifies <see cref="QuarantineLease"/> records (Phase 2, P2-9). Holds the HMAC signing key that
/// proves a lease was armed/lifted by the host — NOT forged by an injected escalation or tampered to extend its
/// expiry or fake an operator lift. The same authority instance (same key) must be given to the
/// <see cref="QuarantineLeaseGate"/> that enforces the leases it issues.
/// <para><b>Key handling.</b> The signing key is a caller-supplied secret (env-var / key vault, never a literal);
/// it is copied defensively on construction. It is never written to the session, the trace, or a lease — only the
/// resulting HMAC is.</para>
/// <para><b>Threat model.</b> The signature stops an attacker WITHOUT the key from FORGING a lease (arming a false
/// quarantine, or fabricating an operator-lift / extended expiry). It does NOT — and cannot — defend against an
/// attacker who can freely REWRITE the session <c>StateBag</c> itself: such an attacker can already escape an
/// active quarantine simply by DELETING the lease, or roll back to a captured older signed lease. Quarantine
/// state lives in the StateBag, so its integrity is the host's responsibility (the StateBag must not be
/// model-writable); the lease adds forgery resistance and bounded, auditable recovery (expiry + operator lift +
/// <see cref="ClearSession"/>) on top of that assumption, not a defense against a StateBag-level compromise.</para>
/// </summary>
public sealed class QuarantineLeaseAuthority
{
    /// <summary>The session <c>StateBag</c> key the lease is stored under.</summary>
    public const string LeaseStateKey = "gatekeeper.quarantine.lease";

    private readonly byte[] _key;

    /// <summary>Creates the authority over a signing key (at least 16 bytes). The key is copied.</summary>
    public QuarantineLeaseAuthority(byte[] signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        if (signingKey.Length < 16)
        {
            throw new ArgumentException("signing key must be at least 16 bytes.", nameof(signingKey));
        }

        _key = (byte[])signingKey.Clone();
    }

    private string Sign(string reason, string evidenceId, DateTimeOffset armedAtUtc, DateTimeOffset expiresAtUtc, string? liftedByOperator)
    {
        // Length-PREFIXED canonical form so a newline (or any delimiter) inside the caller-supplied string fields
        // (reason / evidenceId / operator) cannot shift the field boundaries and make two distinct field sets hash
        // the same. The security-relevant expiry/lift fields are typed and positionally fixed regardless; this is
        // audit-integrity hardening (review finding). "O" timestamps round-trip exactly in UTC.
        var sb = new StringBuilder();
        AppendField(sb, reason);
        AppendField(sb, evidenceId);
        sb.Append(armedAtUtc.ToUniversalTime().ToString("O")).Append('\n');
        sb.Append(expiresAtUtc.ToUniversalTime().ToString("O")).Append('\n');
        AppendField(sb, liftedByOperator);
        return Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(sb.ToString())));

        static void AppendField(StringBuilder sb, string? value)
            => sb.Append(value?.Length ?? -1).Append(':').Append(value).Append('\n');
    }

    /// <summary>True if <paramref name="lease"/>'s signature matches this authority's key (constant-time compare).</summary>
    public bool Verify(QuarantineLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var expected = Sign(lease.Reason, lease.EvidenceId, lease.ArmedAtUtc, lease.ExpiresAtUtc, lease.LiftedByOperator);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(lease.Signature));
    }

    /// <summary>Issues a fresh signed lease armed at <paramref name="nowUtc"/> for <paramref name="timeToLive"/>.</summary>
    public QuarantineLease Arm(string reason, string evidenceId, DateTimeOffset nowUtc, TimeSpan timeToLive)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("reason must be non-empty.", nameof(reason));
        }

        if (string.IsNullOrWhiteSpace(evidenceId))
        {
            throw new ArgumentException("evidenceId must be non-empty (re-arming to extend requires fresh evidence).", nameof(evidenceId));
        }

        if (timeToLive <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeToLive), "lease TTL must be positive.");
        }

        var expiresAt = nowUtc + timeToLive;
        return new QuarantineLease(reason, evidenceId, nowUtc, expiresAt, LiftedByOperator: null,
            Signature: Sign(reason, evidenceId, nowUtc, expiresAt, null));
    }

    /// <summary>
    /// Re-arm semantics (P2-9): a still-active, authentic lease armed by the SAME <paramref name="evidenceId"/>
    /// cannot re-arm — returned unchanged, so stale evidence (e.g. the same injected escalation firing again)
    /// can never extend the quarantine. FRESH evidence (a new id), or an already-expired/lifted/absent lease,
    /// issues a new lease.
    /// </summary>
    public QuarantineLease Rearm(QuarantineLease? current, string reason, string evidenceId, DateTimeOffset nowUtc, TimeSpan timeToLive)
    {
        if (current is not null
            && Verify(current)
            && current.IsActiveAt(nowUtc)
            && string.Equals(current.EvidenceId, evidenceId, StringComparison.Ordinal))
        {
            return current;   // stale evidence — refuse to extend
        }

        return Arm(reason, evidenceId, nowUtc, timeToLive);
    }

    /// <summary>Lifts <paramref name="lease"/> early on behalf of <paramref name="operatorId"/>, re-signing it so the lift is authentic.</summary>
    public QuarantineLease LiftByOperator(QuarantineLease lease, string operatorId)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            throw new ArgumentException("operatorId must be non-empty.", nameof(operatorId));
        }

        return lease with
        {
            LiftedByOperator = operatorId,
            Signature = Sign(lease.Reason, lease.EvidenceId, lease.ArmedAtUtc, lease.ExpiresAtUtc, operatorId),
        };
    }

    /// <summary>Reads the current lease from the session's <c>StateBag</c>, or null if none is stored.</summary>
    public static QuarantineLease? ReadLease(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.StateBag.TryGetValue<QuarantineLease>(LeaseStateKey, out var lease, JsonSerializerOptions.Default) ? lease : null;
    }

    /// <summary>
    /// Arms (or re-arms, per <see cref="Rearm"/>) the quarantine lease on <paramref name="session"/> and stores it.
    /// Returns the effective lease (which is the pre-existing one when stale evidence tried to extend an active lease).
    /// </summary>
    public QuarantineLease ArmSession(AgentSession session, string reason, string evidenceId, DateTimeOffset nowUtc, TimeSpan timeToLive)
    {
        ArgumentNullException.ThrowIfNull(session);
        var lease = Rearm(ReadLease(session), reason, evidenceId, nowUtc, timeToLive);
        session.StateBag.SetValue(LeaseStateKey, lease, JsonSerializerOptions.Default);
        return lease;
    }

    /// <summary>Lifts the session's lease on behalf of <paramref name="operatorId"/> and persists it. Returns false if there is no authentic lease to lift.</summary>
    public bool TryLiftSession(AgentSession session, string operatorId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var lease = ReadLease(session);
        if (lease is null || !Verify(lease))
        {
            return false;
        }

        session.StateBag.SetValue(LeaseStateKey, LiftByOperator(lease, operatorId), JsonSerializerOptions.Default);
        return true;
    }

    /// <summary>
    /// Operator recovery of last resort (review finding): UNCONDITIONALLY removes any quarantine lease — including
    /// a FORGED or otherwise unverifiable one that <see cref="TryLiftSession"/> refuses to touch and the gate
    /// fail-closes on. The caller is responsible for authenticating the operator out-of-band (e.g. behind an
    /// <see cref="OperatorAuthGate"/>) before invoking this — it is not itself signature-gated, precisely so a
    /// tampered lease that broke verification can still be cleared. Returns true if a lease was present.
    /// </summary>
    public static bool ClearSession(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.StateBag.TryRemoveValue(LeaseStateKey);
    }
}
