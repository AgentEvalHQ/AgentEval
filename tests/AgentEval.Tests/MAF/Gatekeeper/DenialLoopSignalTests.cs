// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 4, P4-3 — the per-(tool + arg-shape) denial tally, surfaced to the model as the refusal envelope's "attempts".</summary>
public class DenialLoopSignalTests
{
    [Fact]
    public void RunLedger_RecordDenial_CountsEquivalentRetries_DistinctKeysSeparate()
    {
        var ledger = new RunLedger();
        Assert.Equal(1, ledger.RecordDenial("k1"));
        Assert.Equal(2, ledger.RecordDenial("k1"));
        Assert.Equal(1, ledger.RecordDenial("k2"));
        Assert.Equal(2, ledger.DenialCount("k1"));
        Assert.Equal(0, ledger.DenialCount("never"));
    }

    [Fact]
    public async Task ToolBlock_SurfacesAttempts_IncrementingPerEquivalentRetry()
    {
        // The model repeatedly tries the SAME forbidden call; each denial's envelope reports a rising "attempts".
        var tool = AIFunctionFactory.Create((string path) => "ok", "delete_file");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" })
            .AddToolCall("c2", "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" })   // equivalent retry
            .AddToolCall("c3", "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" })   // equivalent retry
            .AddText("done");
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, g => g.Add(new ForbiddenToolGate("delete_file")))
            .Build();

        await gated.RunAsync("delete it");

        // The final turn's message list carries every refusal exactly once, in order (earlier turns' inputs would
        // re-list the same results as the conversation grows).
        Assert.Equal(new int?[] { 1, 2, 3 }, AttemptsInOrder(model));   // "tried N times" rises with each equivalent retry
    }

    private static List<int?> AttemptsInOrder(ScriptedChatClient model) =>
        model.ReceivedMessages[^1]
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString())
            .Where(t => t is not null && GatekeeperRefusalContract.TryParse(t, out _, out _, out _))
            .Select(t => { GatekeeperRefusalContract.TryParse(t, out _, out _, out var a); return a; })
            .ToList();

    [Fact]
    public async Task DifferentArgShapes_TalliedSeparately()
    {
        var tool = AIFunctionFactory.Create((string path) => "ok", "delete_file");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "delete_file", new Dictionary<string, object?> { ["path"] = "/a" })
            .AddToolCall("c2", "delete_file", new Dictionary<string, object?> { ["path"] = "/b" })   // different args → separate tally
            .AddText("done");
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });

        var gated = agent.AsBuilder()
            .UseGatekeeper(GatekeeperEnforcement.ReplaceResult, g => g.Add(new ForbiddenToolGate("delete_file")))
            .Build();

        await gated.RunAsync("delete both");

        Assert.Equal(new int?[] { 1, 1 }, AttemptsInOrder(model));   // each distinct arg-shape starts its own count
    }

    [Fact]
    public async Task NoRunScope_OmitsAttempts_NoCrossRunBleed()
    {
        // Review #2: a direct tool-gate caller with NO run scope must not tally denials in the process-wide
        // fallback ledger (which would bleed the count across runs/sessions). The refusal simply omits "attempts".
        var tool = AIFunctionFactory.Create((string path) => "ok", "delete_file");
        var model = new ScriptedChatClient()
            .AddToolCall("c1", "delete_file", new Dictionary<string, object?> { ["path"] = "/etc" })
            .AddText("done");
        var agent = new ChatClientAgent(model, new ChatClientAgentOptions { Name = "A", ChatOptions = new ChatOptions { Tools = [tool] } });

        // NO UseAgentEvalGate → no AgentRunScope established.
        var gated = agent.AsBuilder().UseAgentEvalToolGate([new ForbiddenToolGate("delete_file")], ToolGatePolicy.ReplaceResult).Build();

        await gated.RunAsync("go");

        var refusal = model.ReceivedMessages[^1].SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Select(r => r.Result?.ToString()).First(t => t is not null && GatekeeperRefusalContract.TryParse(t, out _, out _, out _));
        Assert.True(GatekeeperRefusalContract.TryParse(refusal, out _, out _, out var attempts));
        Assert.Null(attempts);   // no scope → no attempts
    }
}
