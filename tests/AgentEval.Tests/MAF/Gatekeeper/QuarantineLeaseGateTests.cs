// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Phase 2, P2-9 — the signed, time-bounded quarantine lease. A quarantine can no longer DoS-lock a legitimate
/// session forever: it expires on its own, an authorized operator can lift it early, a forged/tampered lease is
/// not trusted, and the same stale evidence cannot re-arm/extend it.
/// </summary>
public class QuarantineLeaseGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static QuarantineLeaseAuthority Authority() => new(Encoding.UTF8.GetBytes("test-signing-key-32-bytes-long!!"));

    private sealed class BagSession : AgentSession { }

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static async Task<GateAction> RunGate(QuarantineLeaseGate gate, AgentSession session)
    {
        using var scope = AgentRunScope.Begin(session, "agent", null);
        return (await gate.InspectAsync("resume the conversation")).Action;
    }

    [Fact]
    public async Task NoLease_Allows()
    {
        var authority = Authority();
        var gate = new QuarantineLeaseGate(authority, new TestClock(T0));
        Assert.Equal(GateAction.Allow, await RunGate(gate, new BagSession()));
    }

    [Fact]
    public async Task ActiveLease_Blocks()
    {
        var authority = Authority();
        var session = new BagSession();
        authority.ArmSession(session, "compromised", "evidence-1", T0, TimeSpan.FromMinutes(10));

        var gate = new QuarantineLeaseGate(authority, new TestClock(T0));
        Assert.Equal(GateAction.Block, await RunGate(gate, session));
    }

    [Fact]
    public async Task ExpiredLease_Allows_TimeBoundRecovery()
    {
        var authority = Authority();
        var session = new BagSession();
        authority.ArmSession(session, "compromised", "evidence-1", T0, TimeSpan.FromMinutes(10));

        var clock = new TestClock(T0);
        var gate = new QuarantineLeaseGate(authority, clock);
        Assert.Equal(GateAction.Block, await RunGate(gate, session));   // still within the lease

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(GateAction.Allow, await RunGate(gate, session));   // lease expired — session recovers on its own
    }

    [Fact]
    public async Task OperatorLift_Allows_ManualRecovery()
    {
        var authority = Authority();
        var session = new BagSession();
        authority.ArmSession(session, "compromised", "evidence-1", T0, TimeSpan.FromHours(24));

        var gate = new QuarantineLeaseGate(authority, new TestClock(T0));
        Assert.Equal(GateAction.Block, await RunGate(gate, session));

        Assert.True(authority.TryLiftSession(session, "operator-alice"));
        Assert.Equal(GateAction.Allow, await RunGate(gate, session));   // authorized operator lifted it early
    }

    [Fact]
    public async Task TamperedLease_FailsClosed()
    {
        var authority = Authority();
        var session = new BagSession();
        var lease = authority.Arm("compromised", "evidence-1", T0, TimeSpan.FromMinutes(10));

        // Tamper: push the expiry far out WITHOUT re-signing (an attacker can't produce a valid signature).
        var forged = lease with { ExpiresAtUtc = T0.AddYears(-1) };   // even a "lift myself by expiring" tamper
        session.StateBag.SetValue(QuarantineLeaseAuthority.LeaseStateKey, forged, System.Text.Json.JsonSerializerOptions.Default);

        var gate = new QuarantineLeaseGate(authority, new TestClock(T0));
        Assert.Equal(GateAction.Block, await RunGate(gate, session));   // unverifiable lease is not trusted to release
    }

    [Fact]
    public async Task Rearm_WithStaleEvidence_DoesNotExtend()
    {
        // P2-9: the same evidence firing again cannot extend the quarantine. Arm at T0 for 10m; re-arm at T0+5m
        // with the SAME evidence id → expiry stays T0+10m, so by T0+11m the session recovers regardless.
        var authority = Authority();
        var session = new BagSession();
        var first = authority.ArmSession(session, "compromised", "evidence-1", T0, TimeSpan.FromMinutes(10));

        var second = authority.ArmSession(session, "compromised", "evidence-1", T0.AddMinutes(5), TimeSpan.FromMinutes(10));
        Assert.Equal(first.ExpiresAtUtc, second.ExpiresAtUtc);   // NOT extended — stale evidence refused

        var clock = new TestClock(T0.AddMinutes(11));
        var gate = new QuarantineLeaseGate(authority, clock);
        Assert.Equal(GateAction.Allow, await RunGate(gate, session));
    }

    [Fact]
    public async Task Rearm_WithFreshEvidence_Extends()
    {
        var authority = Authority();
        var session = new BagSession();
        var first = authority.ArmSession(session, "compromised", "evidence-1", T0, TimeSpan.FromMinutes(10));

        var second = authority.ArmSession(session, "compromised again", "evidence-2", T0.AddMinutes(5), TimeSpan.FromMinutes(10));
        Assert.True(second.ExpiresAtUtc > first.ExpiresAtUtc);   // fresh evidence DOES re-arm/extend

        var clock = new TestClock(T0.AddMinutes(11));   // past the FIRST expiry, before the second
        var gate = new QuarantineLeaseGate(authority, clock);
        Assert.Equal(GateAction.Block, await RunGate(gate, session));
    }

    [Fact]
    public async Task ForgedLease_FailsClosed_ButOperatorClearRecovers()
    {
        // Review Finding 3: a forged (bad-signature) lease fails closed AND TryLiftSession refuses it (can't
        // verify) — so the documented recovery must be ClearSession, which removes even an unverifiable lease.
        var authority = Authority();
        var session = new BagSession();
        session.StateBag.SetValue(QuarantineLeaseAuthority.LeaseStateKey,
            new QuarantineLease("compromised", "e1", T0, T0.AddYears(1), null, "not-a-valid-signature"),
            System.Text.Json.JsonSerializerOptions.Default);

        var gate = new QuarantineLeaseGate(authority, new TestClock(T0));
        Assert.Equal(GateAction.Block, await RunGate(gate, session));   // fail-closed on the forged lease

        Assert.False(authority.TryLiftSession(session, "operator-alice"));   // lift refuses an unverifiable lease
        Assert.True(QuarantineLeaseAuthority.ClearSession(session));         // operator clear removes it unconditionally
        Assert.Equal(GateAction.Allow, await RunGate(gate, session));        // recovered
    }

    [Fact]
    public void Verify_RejectsWrongKey_AndTamperedFields()
    {
        var authority = Authority();
        var lease = authority.Arm("compromised", "evidence-1", T0, TimeSpan.FromMinutes(10));
        Assert.True(authority.Verify(lease));

        var otherKey = new QuarantineLeaseAuthority(Encoding.UTF8.GetBytes("a-completely-different-32byte-key"));
        Assert.False(otherKey.Verify(lease));                                   // signed by a different key
        Assert.False(authority.Verify(lease with { Reason = "not what was signed" }));   // tampered field
        Assert.False(authority.Verify(lease with { LiftedByOperator = "mallory" }));     // forged lift
    }
}
