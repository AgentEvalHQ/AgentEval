// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 7.4 coverage for immutable, bounded operator-to-session binding.</summary>
public sealed class SessionIdentityDriftGateTests
{
    private const string SessionIdKey = "test.session.id";

    [Fact]
    public async Task FirstAndRepeatedAdmittedOperator_Allow()
    {
        var gate = new SessionIdentityDriftGate(["alice", "bob"]);
        var session = await NewSessionAsync();
        SetOperator(session, "alice");

        var first = await InspectAsync(gate, session);
        var second = await InspectAsync(gate, session);

        Assert.Equal(GateAction.Allow, first.Action);
        Assert.Equal(GateAction.Allow, second.Action);
    }

    [Fact]
    public async Task DifferentAdmittedOperator_BlocksWithoutIdentityDisclosure()
    {
        var gate = new SessionIdentityDriftGate(["alice", "bob"]);
        var session = await NewSessionAsync();
        SetOperator(session, "alice");
        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, session)).Action);

        SetOperator(session, "bob");
        var drift = await InspectAsync(gate, session);

        Assert.Equal(GateAction.Block, drift.Action);
        Assert.Equal(
            "session_identity_drift:operator_changed",
            drift.Reason);
        Assert.DoesNotContain(
            "alice",
            drift.Reason,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "bob",
            drift.Reason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedOperator_CannotPoisonBaseline()
    {
        var gate = new SessionIdentityDriftGate(["alice"]);
        var session = await NewSessionAsync();
        SetOperator(session, "mallory");

        var unauthorized = await InspectAsync(gate, session);
        SetOperator(session, "alice");
        var admitted = await InspectAsync(gate, session);

        Assert.Equal(
            "session_identity_drift:unauthorized_operator",
            unauthorized.Reason);
        Assert.Equal(GateAction.Allow, admitted.Action);
    }

    [Theory]
    [InlineData(null, "missing_operator")]
    [InlineData("", "malformed_operator")]
    [InlineData(" ", "malformed_operator")]
    [InlineData("\u202Ealice", "malformed_operator")]
    [InlineData("alice\nforged", "malformed_operator")]
    public async Task MissingOrMalformedOperator_FailsClosedWithoutBinding(
        string? operatorIdentity,
        string reason)
    {
        var gate = new SessionIdentityDriftGate(["alice"]);
        var session = await NewSessionAsync();
        if (operatorIdentity is not null)
        {
            SetOperator(session, operatorIdentity);
        }

        var malformed = await InspectAsync(gate, session);
        SetOperator(session, "alice");
        var admitted = await InspectAsync(gate, session);

        Assert.Equal(
            $"session_identity_drift:{reason}",
            malformed.Reason);
        Assert.Equal(GateAction.Allow, admitted.Action);
    }

    [Fact]
    public async Task ObjectScopedSessions_AreIndependent()
    {
        var gate = new SessionIdentityDriftGate(["alice", "bob"]);
        var first = await NewSessionAsync();
        var second = await NewSessionAsync();
        SetOperator(first, "alice");
        SetOperator(second, "bob");

        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, first)).Action);
        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, second)).Action);
    }

    [Fact]
    public async Task StableSessionIdentity_DetectsDriftAcrossReload()
    {
        var gate = new SessionIdentityDriftGate(
            ["alice", "bob"],
            sessionKeySelector:
                SessionIdentity.FromStateBag(SessionIdKey));
        var first = await NewSessionAsync();
        var reloaded = await NewSessionAsync();
        SetSessionId(first, "logical-42");
        SetSessionId(reloaded, "logical-42");
        SetOperator(first, "alice");
        SetOperator(reloaded, "bob");

        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, first)).Action);
        Assert.Equal(
            "session_identity_drift:operator_changed",
            (await InspectAsync(gate, reloaded)).Reason);
    }

    [Fact]
    public async Task NoStableSessionIdentity_ReloadIsAnHonestBoundary()
    {
        var gate = new SessionIdentityDriftGate(["alice", "bob"]);
        var first = await NewSessionAsync();
        var reloaded = await NewSessionAsync();
        SetOperator(first, "alice");
        SetOperator(reloaded, "bob");

        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, first)).Action);
        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, reloaded)).Action);
    }

    [Fact]
    public async Task ResolverModeOrValueChangeInsideObject_FailsClosed()
    {
        var gate = new SessionIdentityDriftGate(
            ["alice"],
            sessionKeySelector:
                SessionIdentity.FromStateBag(SessionIdKey));
        var session = await NewSessionAsync();
        SetOperator(session, "alice");
        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, session)).Action);

        SetSessionId(session, "now-present");
        var changed = await InspectAsync(gate, session);

        Assert.Equal(
            "session_identity_drift:session_identity_changed",
            changed.Reason);
    }

    [Fact]
    public async Task ResolverRotationInsideObject_FailsClosed()
    {
        var gate = new SessionIdentityDriftGate(
            ["alice"],
            sessionKeySelector:
                SessionIdentity.FromStateBag(SessionIdKey));
        var session = await NewSessionAsync();
        SetOperator(session, "alice");
        SetSessionId(session, "first-id");
        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, session)).Action);

        SetSessionId(session, "second-id");
        var changed = await InspectAsync(gate, session);

        Assert.Equal(
            "session_identity_drift:session_identity_changed",
            changed.Reason);
    }

    [Fact]
    public async Task ResolverFailureOrMalformedValue_FailsClosed()
    {
        var throwing = new SessionIdentityDriftGate(
            ["alice"],
            sessionKeySelector:
                _ => throw new InvalidOperationException("sensitive"));
        var malformed = new SessionIdentityDriftGate(
            ["alice"],
            sessionKeySelector:
                _ => "\u202Eforged");
        var session = await NewSessionAsync();
        SetOperator(session, "alice");

        Assert.Equal(
            "session_identity_drift:session_identity_unavailable",
            (await InspectAsync(throwing, session)).Reason);
        Assert.Equal(
            "session_identity_drift:malformed_session_identity",
            (await InspectAsync(malformed, session)).Reason);
    }

    [Fact]
    public async Task DurableCapacity_IsAtomicAndExistingBindingContinues()
    {
        var gate = new SessionIdentityDriftGate(
            ["alice"],
            maxTrackedSessions: 1,
            sessionKeySelector:
                SessionIdentity.FromStateBag(SessionIdKey));
        var first = await NewSessionAsync();
        var second = await NewSessionAsync();
        SetOperator(first, "alice");
        SetOperator(second, "alice");
        SetSessionId(first, "first");
        SetSessionId(second, "second");

        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, first)).Action);
        Assert.Equal(
            "session_identity_drift:capacity_exhausted",
            (await InspectAsync(gate, second)).Reason);
        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, first)).Action);
    }

    [Fact]
    public async Task ConcurrentConflictingFirstUse_AdmitsExactlyOneBaseline()
    {
        var gate = new SessionIdentityDriftGate(
            ["alice", "bob"],
            sessionKeySelector:
                SessionIdentity.FromStateBag(SessionIdKey));
        var alice = await NewSessionAsync();
        var bob = await NewSessionAsync();
        SetSessionId(alice, "shared");
        SetSessionId(bob, "shared");
        SetOperator(alice, "alice");
        SetOperator(bob, "bob");

        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var aliceTask = Task.Run(
            async () =>
            {
                await start.Task;
                return await InspectAsync(gate, alice);
            });
        var bobTask = Task.Run(
            async () =>
            {
                await start.Task;
                return await InspectAsync(gate, bob);
            });
        start.TrySetResult();
        var results = await Task.WhenAll(
            aliceTask,
            bobTask);

        Assert.Single(
            results,
            verdict => verdict.Action == GateAction.Allow);
        Assert.Single(
            results,
            verdict =>
                string.Equals(
                    verdict.Reason,
                    "session_identity_drift:operator_changed",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitResolver_WinsOverInjectedDefault()
    {
        var gate = new SessionIdentityDriftGate(
            ["alice", "bob"],
            sessionKeySelector:
                SessionIdentity.FromStateBag("explicit"));
        ((ISessionIdentityAware)gate).UseSessionIdentityDefault(
            SessionIdentity.FromStateBag("shared-default"));
        var first = await NewSessionAsync();
        var reloaded = await NewSessionAsync();
        SetOperator(first, "alice");
        SetOperator(reloaded, "bob");
        first.StateBag.SetValue(
            "explicit",
            "same",
            JsonSerializerOptions.Default);
        reloaded.StateBag.SetValue(
            "explicit",
            "same",
            JsonSerializerOptions.Default);
        first.StateBag.SetValue(
            "shared-default",
            "different-1",
            JsonSerializerOptions.Default);
        reloaded.StateBag.SetValue(
            "shared-default",
            "different-2",
            JsonSerializerOptions.Default);

        Assert.Equal(
            GateAction.Allow,
            (await InspectAsync(gate, first)).Action);
        Assert.Equal(
            "session_identity_drift:operator_changed",
            (await InspectAsync(gate, reloaded)).Reason);
    }

    [Fact]
    public void ConstructorAndFingerprint_AreBoundedDeterministicAndSecretFree()
    {
        const string secretIdentity = "secret-operator";
        var first = new SessionIdentityDriftGate(
            ["bob", secretIdentity],
            maxTrackedSessions: 20);
        var equivalent = new SessionIdentityDriftGate(
            [secretIdentity, "bob"],
            maxTrackedSessions: 20);
        var different = new SessionIdentityDriftGate(
            [secretIdentity, "bob"],
            maxTrackedSessions: 21);
        var explicitResolver = new SessionIdentityDriftGate(
            [secretIdentity, "bob"],
            maxTrackedSessions: 20,
            sessionKeySelector: _ => "session");
        var injectedResolver = new SessionIdentityDriftGate(
            [secretIdentity, "bob"],
            maxTrackedSessions: 20);
        var beforeInjection = GateConfigFingerprint.Compute(
            chatGates: [injectedResolver]);
        ((ISessionIdentityAware)injectedResolver)
            .UseSessionIdentityDefault(_ => "session");
        var afterInjection = GateConfigFingerprint.Compute(
            chatGates: [injectedResolver]);

        Assert.Equal(
            first.ConfigurationFingerprint,
            equivalent.ConfigurationFingerprint);
        Assert.NotEqual(
            first.ConfigurationFingerprint,
            different.ConfigurationFingerprint);
        Assert.NotEqual(
            first.ConfigurationFingerprint,
            explicitResolver.ConfigurationFingerprint);
        Assert.NotEqual(beforeInjection, afterInjection);
        Assert.NotEqual(
            explicitResolver.ConfigurationFingerprint,
            injectedResolver.ConfigurationFingerprint);
        Assert.Equal(64, first.ConfigurationFingerprint.Length);
        Assert.DoesNotContain(
            secretIdentity,
            first.ConfigurationFingerprint,
            StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(
            () => new SessionIdentityDriftGate(null!));
        Assert.Throws<ArgumentException>(
            () => new SessionIdentityDriftGate([]));
        Assert.Throws<ArgumentException>(
            () => new SessionIdentityDriftGate(["alice", "alice"]));
        Assert.Throws<ArgumentException>(
            () => new SessionIdentityDriftGate(["\u202Ealice"]));
        Assert.Throws<ArgumentException>(
            () => new SessionIdentityDriftGate(
                Enumerable.Range(0, 1_025)
                    .Select(index => $"operator-{index}")));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SessionIdentityDriftGate(
                ["alice"],
                maxTrackedSessions: 0));
    }

    [Fact]
    public async Task UseGatekeeper_InjectedResolver_BlocksBeforeModel()
    {
        var scripted = new ScriptedChatClient()
            .AddText("first")
            .AddText("must-not-run");
        var gate = new SessionIdentityDriftGate(["alice", "bob"]);
        var agent = new ChatClientAgent(
                scripted,
                new ChatClientAgentOptions
                {
                    Name = "T",
                })
            .AsBuilder()
            .UseGatekeeper(
                GatekeeperEnforcement.Terminate,
                options =>
                {
                    options.AddPreGate(gate);
                    options.SessionIdentity =
                        SessionIdentity.FromStateBag(
                            SessionIdKey);
                })
            .Build();
        var first = await agent.CreateSessionAsync();
        var reloaded = await agent.CreateSessionAsync();
        SetSessionId(first, "logical-session");
        SetSessionId(reloaded, "logical-session");
        SetOperator(first, "alice");
        SetOperator(reloaded, "bob");

        Assert.Equal(
            "first",
            (await agent.RunAsync("go", first)).Text);
        var exception = await Record.ExceptionAsync(
            () => agent.RunAsync("go", reloaded));

        var refusal =
            Assert.IsType<EvalGateRefusalException>(exception);
        Assert.Equal(
            "session_identity_drift:operator_changed",
            refusal.Reason);
        Assert.Equal(1, scripted.CallCount);
    }

    private static async Task<GateVerdict> InspectAsync(
        SessionIdentityDriftGate gate,
        AgentSession session)
    {
        using var scope = AgentRunScope.Begin(
            session,
            "test-agent",
            trace: null);
        return await gate.InspectAsync(string.Empty);
    }

    private static async Task<AgentSession> NewSessionAsync()
        => await new ChatClientAgent(
                new ScriptedChatClient(),
                new ChatClientAgentOptions
                {
                    Name = "T",
                })
            .CreateSessionAsync();

    private static void SetOperator(
        AgentSession session,
        string operatorIdentity)
        => session.StateBag.SetValue(
            OperatorAuthGate.OperatorMetadataKey,
            operatorIdentity,
            JsonSerializerOptions.Default);

    private static void SetSessionId(
        AgentSession session,
        string sessionId)
        => session.StateBag.SetValue(
            SessionIdKey,
            sessionId,
            JsonSerializerOptions.Default);
}
