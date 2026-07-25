// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// P6-2 Would-Have-Enforced evidence mode: under a non-enforcing policy (Observe / WarnOnly) the gate records what it
/// WOULD have done, and GlassBoxEvidence surfaces WouldBlock/WouldMutate/WouldRedact counts — an Observe→Enforce diff
/// that never inflates the enforced GateBlockCount.
/// </summary>
public class WouldEnforceEvidenceTests
{
    // ── Integration: a real WarnOnly tool-gate block records a would-block, not an enforced block ──

    [Fact]
    public async Task WarnOnly_BlockingToolGate_RecordsWouldBlock_NotEnforcedBlock()
    {
        var tool = AIFunctionFactory.Create((string x) => "ok", "delete_account");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "delete_account", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [tool] } });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalGate()
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.WarnOnly, trace)
            .Build();

        await gated.RunAsync("go");

        var evidence = GlassBoxEvidence.FromTrace(trace)!;
        Assert.Equal(1, evidence.WouldBlockCount);   // WarnOnly ⇒ would have blocked
        Assert.Equal(0, evidence.GateBlockCount);     // but did NOT enforce — the tool ran
    }

    [Fact]
    public async Task Enforced_BlockingToolGate_RecordsBlock_NotWouldBlock()
    {
        var tool = AIFunctionFactory.Create((string x) => "ok", "delete_account");
        var scripted = new ScriptedChatClient().AddToolCall("c1", "delete_account", new Dictionary<string, object?> { ["x"] = "1" }).AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [tool] } });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalGate()
            .UseAgentEvalToolGate([new ForbiddenToolGate("delete_account")], ToolGatePolicy.Terminate, trace)
            .Build();

        await gated.RunAsync("go");

        var evidence = GlassBoxEvidence.FromTrace(trace)!;
        Assert.Equal(1, evidence.GateBlockCount);     // enforced
        Assert.Equal(0, evidence.WouldBlockCount);    // no counterfactual — it really blocked
    }

    // ── Unit: counting logic across all three counterfactuals, plus the serialized (JsonElement) path ──

    private static AgentTrace TraceWithMixedEvidence()
    {
        var trace = new AgentTrace();
        trace.SetMetadata("gate.tool.1.PolA", new Dictionary<string, object?> { ["action"] = "Warn", ["wouldAction"] = "Block" });   // would-block
        trace.SetMetadata("gate.tool.2.PolB", new Dictionary<string, object?> { ["action"] = "Mutate", ["applied"] = false });        // would-mutate
        trace.SetMetadata("gate.tool-result.3.PolC", new Dictionary<string, object?> { ["action"] = "Redact", ["applied"] = false }); // would-redact
        trace.SetMetadata("gate.tool.4.PolD", new Dictionary<string, object?> { ["action"] = "Block" });                              // enforced block
        trace.SetMetadata("gate.tool.5.PolE", new Dictionary<string, object?> { ["action"] = "Mutate", ["applied"] = true });         // APPLIED mutate — not a counterfactual
        return trace;
    }

    [Fact]
    public void CountsEachCounterfactual_Once_AndKeepsEnforcedBlockSeparate()
    {
        var evidence = GlassBoxEvidence.FromTrace(TraceWithMixedEvidence())!;

        Assert.Equal(1, evidence.WouldBlockCount);
        Assert.Equal(1, evidence.WouldMutateCount);
        Assert.Equal(1, evidence.WouldRedactCount);
        Assert.Equal(1, evidence.GateBlockCount);   // only the real enforced block (PolD)
    }

    [Fact]
    public async Task Counterfactuals_SurviveSerializationRoundTrip()
    {
        // The reloaded trace's metadata values are JsonElement (and a bool renders "false", not "False") — the reader
        // must still count the counterfactuals, or an Observe dry run persisted to disk would silently report zero.
        var json = await TraceSerializer.SerializeToStringAsync(TraceWithMixedEvidence());
        var reloaded = await TraceSerializer.DeserializeFromStringAsync(json);

        var evidence = GlassBoxEvidence.FromTrace(reloaded)!;
        Assert.Equal(1, evidence.WouldBlockCount);
        Assert.Equal(1, evidence.WouldMutateCount);
        Assert.Equal(1, evidence.WouldRedactCount);
        Assert.Equal(1, evidence.GateBlockCount);
    }
}
