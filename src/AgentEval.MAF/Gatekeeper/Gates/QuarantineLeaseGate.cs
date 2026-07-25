// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 2, P2-9) — the recoverable successor to <see cref="QuarantineGate"/>. A fail-closed run-pre
/// session gate that refuses to resume a session while a signed <see cref="QuarantineLease"/> is active, but —
/// unlike the boolean-forever flag — the lease <b>expires</b> on its own (time-bound recovery) and can be
/// <b>lifted early by an authorized operator</b>, so a single (possibly injected / false-positive) escalation can
/// no longer DoS-lock a legitimate session with no code recovery path. Register as a <c>pre</c> gate on
/// <c>UseAgentEvalGate</c>; arm/lift leases through the shared <see cref="QuarantineLeaseAuthority"/>.
/// <para><b>Signature is authority.</b> Only a lease whose HMAC verifies against the authority's key is honored;
/// a tampered or forged lease fails closed (Block) rather than being trusted to lift or expire itself.</para>
/// </summary>
public sealed class QuarantineLeaseGate : SessionContextGate
{
    private readonly QuarantineLeaseAuthority _authority;
    private readonly TimeProvider _timeProvider;

    /// <inheritdoc/>
    public override string PolicyName => "QuarantineLeaseGate";

    /// <summary>Creates the gate over the <paramref name="authority"/> that issues its leases (same signing key).</summary>
    /// <param name="authority">The lease authority whose key verifies the stored lease's signature.</param>
    /// <param name="timeProvider">Clock used to test expiry; defaults to <see cref="TimeProvider.System"/>.</param>
    public QuarantineLeaseGate(QuarantineLeaseAuthority authority, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        _authority = authority;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    protected override ValueTask<GateVerdict> CheckSessionAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var lease = QuarantineLeaseAuthority.ReadLease(session);
        if (lease is null)
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));   // no quarantine
        }

        if (!_authority.Verify(lease))
        {
            // A lease we cannot authenticate is a tampering/forgery signal — fail closed. Recovery is via an
            // authenticated operator calling QuarantineLeaseAuthority.ClearSession (which removes even an
            // unverifiable lease), so a forged lease is not a permanent, unrecoverable DoS.
            return new ValueTask<GateVerdict>(
                GateVerdict.Block(PolicyName, "quarantine lease failed signature verification — failing closed"));
        }

        if (lease.LiftedByOperator is not null)
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));   // operator lifted — manual recovery
        }

        var now = _timeProvider.GetUtcNow();
        if (now >= lease.ExpiresAtUtc)
        {
            return new ValueTask<GateVerdict>(GateVerdict.Allow(PolicyName));   // expired — time-bound recovery
        }

        return new ValueTask<GateVerdict>(GateVerdict.Block(PolicyName,
            $"session quarantined until {lease.ExpiresAtUtc:O} ({lease.Reason}) — lease active"));
    }
}
