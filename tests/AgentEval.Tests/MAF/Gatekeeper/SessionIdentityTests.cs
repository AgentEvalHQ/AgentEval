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

/// <summary>
/// F-A / P1-4 — the shared durable-session-identity primitive: the <see cref="SessionIdentity"/> resolvers, and
/// <c>UseGatekeeper</c> injecting <see cref="GatekeeperOptions.SessionIdentity"/> into <see cref="ISessionIdentityAware"/>
/// gates so a per-session cap survives a persisted-session reload configured ONCE, not per gate.
/// </summary>
public class SessionIdentityTests
{
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<AgentSession> NewSessionAsync()
        => await new ChatClientAgent(new ScriptedChatClient(), new ChatClientAgentOptions { Name = "T" }).CreateSessionAsync();

    // ── SessionIdentity.FromStateBag ──

    [Fact]
    public async Task FromStateBag_ResolvesId_WhenPresent()
    {
        var session = await NewSessionAsync();
        session.StateBag.SetValue("sid", "abc-123", JsonSerializerOptions.Default);

        Assert.Equal("abc-123", SessionIdentity.FromStateBag("sid")(session));
    }

    [Fact]
    public async Task FromStateBag_ReturnsNull_WhenAbsent()
    {
        var session = await NewSessionAsync();
        Assert.Null(SessionIdentity.FromStateBag("sid")(session));
    }

    [Fact]
    public void FromStateBag_EmptyKey_Throws()
        => Assert.Throws<ArgumentException>(() => SessionIdentity.FromStateBag(""));

    // ── SessionIdentity.Combine ──

    [Fact]
    public async Task Combine_ReturnsFirstNonEmpty()
    {
        var session = await NewSessionAsync();
        session.StateBag.SetValue("b", "from-b", JsonSerializerOptions.Default);   // 'a' absent, 'b' present
        var resolver = SessionIdentity.Combine(SessionIdentity.FromStateBag("a"), SessionIdentity.FromStateBag("b"));

        Assert.Equal("from-b", resolver(session));
    }

    [Fact]
    public async Task Combine_ReturnsNull_WhenAllEmpty()
    {
        var session = await NewSessionAsync();
        var resolver = SessionIdentity.Combine(SessionIdentity.FromStateBag("a"), SessionIdentity.FromStateBag("b"));

        Assert.Null(resolver(session));
    }

    [Fact]
    public void Combine_Validates()
    {
        Assert.Throws<ArgumentException>(() => SessionIdentity.Combine());                                        // empty
        Assert.Throws<ArgumentException>(() => SessionIdentity.Combine(SessionIdentity.FromStateBag("a"), null!)); // null element
    }

    // ── UseGatekeeper injection: configure durable identity ONCE, reload survives ──

    private const string SidKey = "test.session.id";

    private static AIAgent GatedWithIdentity(ScriptedChatClient scripted, RateLimitGate gate, Func<AgentSession, string?>? sessionIdentity)
        => new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T" })
            .AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.Terminate, o =>
            {
                o.AddPreGate(gate);
                o.SessionIdentity = sessionIdentity;
            })
            .Build();

    [Fact]
    public async Task Injected_SessionIdentity_MakesRateLimitSurviveReload()
    {
        const string stableId = "logical-session-77";
        var clock = new FakeClock();
        var scripted = new ScriptedChatClient().AddText("a").AddText("b").AddText("c");
        // The gate has NO explicit selector — the shared GatekeeperOptions.SessionIdentity is what wires it.
        var gate = new RateLimitGate(maxRuns: 1, window: TimeSpan.FromMinutes(1), timeProvider: clock);
        var agent = GatedWithIdentity(scripted, gate, SessionIdentity.FromStateBag(SidKey));

        var session1 = await agent.CreateSessionAsync();
        session1.StateBag.SetValue(SidKey, stableId, JsonSerializerOptions.Default);
        Assert.Equal("a", (await agent.RunAsync("go", session1)).Text);   // 1st run → allowed

        var session2 = await agent.CreateSessionAsync();   // reload: fresh object, same logical id
        session2.StateBag.SetValue(SidKey, stableId, JsonSerializerOptions.Default);
        var ex = await Record.ExceptionAsync(() => agent.RunAsync("go", session2));
        Assert.IsType<EvalGateRefusalException>(ex);   // counter carried over ⇒ injected resolver worked
    }

    [Fact]
    public async Task NoInjectedIdentity_ReloadResets_DefaultObjectIdentity()
    {
        // Contrast: without a shared resolver, the gate keys on object identity, so a reload resets the counter.
        var clock = new FakeClock();
        var scripted = new ScriptedChatClient().AddText("a").AddText("b").AddText("c");
        var gate = new RateLimitGate(maxRuns: 1, window: TimeSpan.FromMinutes(1), timeProvider: clock);
        var agent = GatedWithIdentity(scripted, gate, sessionIdentity: null);

        var session1 = await agent.CreateSessionAsync();
        Assert.Equal("a", (await agent.RunAsync("go", session1)).Text);

        var session2 = await agent.CreateSessionAsync();   // fresh object → new counter
        Assert.Equal("b", (await agent.RunAsync("go", session2)).Text);   // reset → allowed
    }

    [Fact]
    public async Task ExplicitPerGateSelector_WinsOver_InjectedDefault()
    {
        // An explicit per-gate selector must win over the shared default (UseSessionIdentityDefault is set-once).
        const string id = "logical-99";
        var clock = new FakeClock();
        var scripted = new ScriptedChatClient().AddText("a").AddText("b").AddText("c");
        var gate = new RateLimitGate(maxRuns: 1, window: TimeSpan.FromMinutes(1), timeProvider: clock,
            sessionKeySelector: SessionIdentity.FromStateBag("explicit.key"));
        // The shared default points at a DIFFERENT key that is never populated — if it won, the reload would reset.
        var agent = GatedWithIdentity(scripted, gate, SessionIdentity.FromStateBag("shared.key"));

        var session1 = await agent.CreateSessionAsync();
        session1.StateBag.SetValue("explicit.key", id, JsonSerializerOptions.Default);
        Assert.Equal("a", (await agent.RunAsync("go", session1)).Text);

        var session2 = await agent.CreateSessionAsync();   // reload
        session2.StateBag.SetValue("explicit.key", id, JsonSerializerOptions.Default);
        var ex = await Record.ExceptionAsync(() => agent.RunAsync("go", session2));
        Assert.IsType<EvalGateRefusalException>(ex);   // blocked ⇒ the EXPLICIT selector was used, not the shared default
    }
}
