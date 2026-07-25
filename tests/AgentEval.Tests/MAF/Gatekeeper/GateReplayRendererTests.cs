// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>Phase 3, P3-9 — the fixture-capture → replayer bridge and the human-readable allow/block/mutate diff.</summary>
public class GateReplayRendererTests
{
    private static GatedToolCall MakeCall(string name) =>
        new(name, null, AgentName: "A", Iteration: 0, FunctionCallIndex: 0, FunctionCount: 1, IsStreaming: false, Messages: null);

    private sealed class BlockByNameGate(string target) : IToolGate
    {
        public string PolicyName => $"block-{target}";
        public GateCost Cost => GateCost.PureCode;
        public ValueTask<ToolGateVerdict> InspectAsync(GatedToolCall call, CancellationToken ct = default)
            => new(call.FunctionName == target ? ToolGateVerdict.Block(PolicyName, "forbidden") : ToolGateVerdict.Allow(PolicyName));
    }

    [Fact]
    public async Task Render_ShowsPerCallDiff_DivergenceMarker_AndSummary()
    {
        var calls = new[] { MakeCall("read_file"), MakeCall("send_email") };
        var comparison = await GateReplayer.CompareAsync(calls, baseline: [], candidate: [new BlockByNameGate("send_email")]);

        var text = GateReplayRenderer.Render(comparison);

        Assert.Contains("2 call(s), 1 diverged", text);
        Assert.Contains("read_file: baseline=Allow  candidate=Allow", text);
        Assert.Contains("DIVERGED", text);
        Assert.Contains("candidate=Block(block-send_email)", text);
    }

    [Fact]
    public void ExtractToolCalls_ReadsFunctionCallContent_InOrder()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "a" }),
                new FunctionCallContent("c2", "send_email", new Dictionary<string, object?> { ["to"] = "x" }),
            }),
            new ChatMessage(ChatRole.Assistant, "just text, no calls"),
        };

        var calls = GateReplayRenderer.ExtractToolCalls(messages, agentName: "A");

        Assert.Equal(2, calls.Count);
        Assert.Equal("read_file", calls[0].FunctionName);
        Assert.Equal("send_email", calls[1].FunctionName);
        Assert.Equal(2, calls[0].FunctionCount);
        Assert.Equal("a", calls[0].Arguments!["path"]);
    }

    [Fact]
    public async Task ExtractedCalls_FeedTheReplayer_EndToEnd()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("c1", "send_email", new Dictionary<string, object?> { ["to"] = "x" }),
            }),
        };
        var calls = GateReplayRenderer.ExtractToolCalls(messages);

        var comparison = await GateReplayer.CompareAsync(calls, baseline: [], candidate: [new BlockByNameGate("send_email")]);
        Assert.Single(comparison.Diverged);
        Assert.Contains("send_email", GateReplayRenderer.Render(comparison));
    }
}
