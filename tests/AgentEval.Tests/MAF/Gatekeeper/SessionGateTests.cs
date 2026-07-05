// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Guardrails;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Gatekeeper M2c — fail-closed session gates (auth, rate). Registered as run-pre gates.</summary>
public class SessionGateTests
{
    private static AIAgent GatedWith(ScriptedChatClient scripted, IChatGate gate)
        => new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T" })
            .AsBuilder()
            .UseAgentEvalGate(pre: [gate], policy: EvalGatePolicy.ThrowOnFail)
            .Build();

    // ── OperatorAuthGate (fail-closed) ──

    [Fact]
    public async Task Auth_NoOperatorInSession_FailsClosed()
    {
        var scripted = new ScriptedChatClient().AddText("answer");
        var agent = GatedWith(scripted, new OperatorAuthGate("alice"));
        var session = await agent.CreateSessionAsync();   // no operator set

        var ex = await Record.ExceptionAsync(() => agent.RunAsync("hi", session));

        Assert.IsType<EvalGateRefusalException>(ex);       // missing identity ⇒ blocked
        Assert.Equal(0, scripted.CallCount);
    }

    [Fact]
    public async Task Auth_AllowedOperator_Passes()
    {
        var scripted = new ScriptedChatClient().AddText("answer");
        var agent = GatedWith(scripted, new OperatorAuthGate("alice", "bob"));
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(OperatorAuthGate.OperatorMetadataKey, "alice", JsonSerializerOptions.Default);

        var response = await agent.RunAsync("hi", session);

        Assert.Equal("answer", response.Text);             // authorized ⇒ the model ran
    }

    [Fact]
    public async Task Auth_UnauthorizedOperator_Blocked()
    {
        var scripted = new ScriptedChatClient().AddText("answer");
        var agent = GatedWith(scripted, new OperatorAuthGate("alice"));
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(OperatorAuthGate.OperatorMetadataKey, "mallory", JsonSerializerOptions.Default);

        var ex = await Record.ExceptionAsync(() => agent.RunAsync("hi", session));

        Assert.IsType<EvalGateRefusalException>(ex);
    }

    // ── RateLimitGate (injectable clock) ──

    [Fact]
    public async Task Rate_WithinLimit_Passes_ThenBlocks_ThenResetsAfterWindow()
    {
        var clock = new FakeClock { Now = DateTimeOffset.UnixEpoch };
        var scripted = new ScriptedChatClient().AddText("a").AddText("b").AddText("c");
        var gate = new RateLimitGate(maxRuns: 1, window: TimeSpan.FromMinutes(1), timeProvider: clock);
        var agent = GatedWith(scripted, gate);
        var session = await agent.CreateSessionAsync();

        // 1st run within the window → allowed
        var r1 = await agent.RunAsync("go", session);
        Assert.Equal("a", r1.Text);

        // 2nd run in the same window → blocked
        var ex = await Record.ExceptionAsync(() => agent.RunAsync("go", session));
        Assert.IsType<EvalGateRefusalException>(ex);

        // advance past the window → allowed again
        clock.Now = clock.Now.AddMinutes(2);
        var r3 = await agent.RunAsync("go", session);
        Assert.Equal("b", r3.Text);   // 2nd scripted turn (the blocked run never reached the model)
    }

    // ── streaming path: session gates must see the run scope on the streaming branch too ──

    [Fact]
    public async Task Auth_Streaming_AllowedOperator_StreamsThrough()
    {
        var scripted = new ScriptedChatClient().AddText("streamed answer");
        var agent = GatedWith(scripted, new OperatorAuthGate("alice"));
        var session = await agent.CreateSessionAsync();
        session.StateBag.SetValue(OperatorAuthGate.OperatorMetadataKey, "alice", JsonSerializerOptions.Default);

        var chunks = new List<string>();
        await foreach (var update in agent.RunStreamingAsync("hi", session))
        {
            chunks.Add(update.Text);
        }

        Assert.Contains("streamed answer", string.Concat(chunks));   // authorized on the STREAMING path (scope was established)
    }

    [Fact]
    public async Task Auth_Streaming_NoOperator_FailsClosed()
    {
        var scripted = new ScriptedChatClient().AddText("streamed answer");
        var agent = GatedWith(scripted, new OperatorAuthGate("alice"));
        var session = await agent.CreateSessionAsync();   // no operator

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in agent.RunStreamingAsync("hi", session)) { }
        });

        Assert.IsType<EvalGateRefusalException>(ex);   // fails closed on the streaming path too
    }

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
