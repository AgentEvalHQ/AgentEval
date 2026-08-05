// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#pragma warning disable AGENTEVAL_GATEKEEPER_PREVIEW001 // Sample intentionally demonstrates the preview identity-drift gate.

using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Samples;

/// <summary>Offline weak-object versus stable logical-session identity takeover demonstration.</summary>
public static class GatekeeperSessionIdentityTakeover
{
    private const string SessionIdKey = "sample.session.id";

    public static async Task RunAsync()
    {
        GatekeeperSampleContractRenderer.Print("26");
        Console.WriteLine("\n=== Gatekeeper — Session Identity Takeover + Reload (offline) ===\n");

        await WeakObjectIdentityBoundaryAsync();
        await StableReloadAndPoisoningDefenseAsync();
        await ConcurrentTakeoverAsync();

        Console.WriteLine("   object identity only: reload resets the binding — honest limitation");
        Console.WriteLine("   stable logical key:   actor drift after reload blocks without identity disclosure");
        Console.WriteLine("   baseline integrity:   unauthorized first use cannot poison the admitted actor");
        Console.WriteLine("   concurrent takeover:  exactly one conflicting actor wins the atomic first binding");
        Console.WriteLine("   ✅ host-attested logical identity closed the reload and race bypasses.");
    }

    private static async Task WeakObjectIdentityBoundaryAsync()
    {
        var gate = new SessionIdentityDriftGate(["alice", "bob"]);
        var first = await NewSessionAsync();
        var reloaded = await NewSessionAsync();
        SetOperator(first, "alice");
        SetOperator(reloaded, "bob");

        Require((await InspectAsync(gate, first)).Action == GateAction.Allow,
            "the first object-scoped actor must establish its baseline");
        Require((await InspectAsync(gate, reloaded)).Action == GateAction.Allow,
            "a new object has no durable identity link and must expose that honest boundary");
    }

    private static async Task StableReloadAndPoisoningDefenseAsync()
    {
        var gate = new SessionIdentityDriftGate(
            ["alice", "bob"],
            sessionKeySelector: SessionIdentity.FromStateBag(SessionIdKey));
        var first = await NewSessionAsync();
        var reloaded = await NewSessionAsync();
        SetSessionId(first, "logical-42");
        SetSessionId(reloaded, "logical-42");
        SetOperator(first, "alice");
        SetOperator(reloaded, "bob");

        Require((await InspectAsync(gate, first)).Action == GateAction.Allow,
            "the admitted actor must establish the stable logical binding");
        Require((await InspectAsync(gate, first)).Action == GateAction.Allow,
            "the same admitted actor must remain allowed on the stable logical session");
        var drift = await InspectAsync(gate, reloaded);
        Require(
            drift.Action == GateAction.Block &&
            drift.Reason == "session_identity_drift:operator_changed" &&
            !drift.Reason.Contains("alice", StringComparison.Ordinal) &&
            !drift.Reason.Contains("bob", StringComparison.Ordinal),
            "reload drift must block with a content-free reason");

        var poisoningGate = new SessionIdentityDriftGate(
            ["alice"],
            sessionKeySelector: SessionIdentity.FromStateBag(SessionIdKey));
        var poisoned = await NewSessionAsync();
        SetSessionId(poisoned, "logical-43");
        SetOperator(poisoned, "mallory");
        Require(
            (await InspectAsync(poisoningGate, poisoned)).Reason ==
            "session_identity_drift:unauthorized_operator",
            "an unauthorized actor must be rejected before baseline admission");
        SetOperator(poisoned, "alice");
        Require((await InspectAsync(poisoningGate, poisoned)).Action == GateAction.Allow,
            "the rejected actor must not poison the future admitted baseline");
    }

    private static async Task ConcurrentTakeoverAsync()
    {
        var gate = new SessionIdentityDriftGate(
            ["alice", "bob"],
            sessionKeySelector: SessionIdentity.FromStateBag(SessionIdKey));
        var alice = await NewSessionAsync();
        var bob = await NewSessionAsync();
        SetSessionId(alice, "logical-race");
        SetSessionId(bob, "logical-race");
        SetOperator(alice, "alice");
        SetOperator(bob, "bob");

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aliceTask = Task.Run(async () =>
        {
            await start.Task;
            return await InspectAsync(gate, alice);
        });
        var bobTask = Task.Run(async () =>
        {
            await start.Task;
            return await InspectAsync(gate, bob);
        });
        start.TrySetResult();
        var verdicts = await Task.WhenAll(aliceTask, bobTask);

        Require(verdicts.Count(verdict => verdict.Action == GateAction.Allow) == 1,
            "exactly one actor must establish the concurrent first-use binding");
        Require(verdicts.Count(verdict =>
            verdict.Reason == "session_identity_drift:operator_changed") == 1,
            "the losing conflicting actor must be refused as drift");
    }

    private static async Task<GateVerdict> InspectAsync(
        SessionIdentityDriftGate gate,
        AgentSession session)
    {
        using var scope = AgentRunScope.Begin(session, "identity-sample", trace: null);
        return await gate.InspectAsync(string.Empty);
    }

    private static async Task<AgentSession> NewSessionAsync() =>
        await new ChatClientAgent(
            new ScriptedChatClient(),
            new ChatClientAgentOptions
            {
                Name = "identity-session-factory",
                ChatOptions = new ChatOptions { MaxOutputTokens = 64 },
            })
            .CreateSessionAsync();

    private static void SetOperator(AgentSession session, string actor) =>
        session.StateBag.SetValue(
            OperatorAuthGate.OperatorMetadataKey,
            actor,
            JsonSerializerOptions.Default);

    private static void SetSessionId(AgentSession session, string identity) =>
        session.StateBag.SetValue(
            SessionIdKey,
            identity,
            JsonSerializerOptions.Default);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Session-identity sample failed: " + message + ".");
        }
    }
}
