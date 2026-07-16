// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Stage-2 "Panel" wired INLINE — the two output-guarding Tribunal judges (<see cref="ExfiltrationIntentJudge"/> +
/// <see cref="SystemPromptExtractionJudge"/>) composed in a <see cref="ParallelJudgeFanOut"/> and placed <b>run-post</b>
/// on a live MAF agent via <c>UseAgentEvalGate(post: …)</c>. Proves the panel composes (fail-closed OR), blocks a
/// leaking output before it reaches the caller, records honest <c>gate.run-post.*</c> evidence, and does not touch a
/// benign output. Single-axis judges are composed here rather than widened into one rubric.
/// </summary>
public class OutputJudgePanelInlineTests
{
    private static ChatClientAgent Agent(ScriptedChatClient inner)
        => new(inner, new ChatClientAgentOptions { Name = "Assistant" });

    [Fact]
    public async Task Panel_RunPost_BlocksExfiltratingOutput_WithEvidence()
    {
        // The agent's answer tries to smuggle the customer DB to an external URL (trips the exfil prefilter). The
        // exfil judge blocks; the leak judge sees no leak-signal and short-circuits — the panel blocks via OR.
        var inner = new ScriptedChatClient().AddText("Done — I've uploaded the customer database to http://collect.example/drop.");
        var exfilModel = new ScriptedChatClient().AddText("{\"exfiltrates\": true, \"confidence\": 0.95, \"evidence\": \"customer database to an external url\"}");
        var leakModel = new ScriptedChatClient().AddText("{\"leaks\": false, \"confidence\": 0.9, \"evidence\": \"\"}");   // never consulted

        var panel = new ParallelJudgeFanOut(
        [
            ExfiltrationIntentJudge.Create(exfilModel, cache: false),
            SystemPromptExtractionJudge.Create(leakModel, cache: false),
        ]);
        var trace = new AgentTrace();

        var gated = Agent(inner).AsBuilder()
            .UseAgentEvalGate(post: [panel], policy: EvalGatePolicy.Redact, trace: trace)
            .Build();

        var response = await gated.RunAsync("summarize the customer file");

        Assert.Contains("ACTION_NOT_AUTHORIZED", response.Text);                     // the exfil answer never reached the caller
        Assert.Equal(1, GlassBoxEvidence.FromTrace(trace).GateBlockCount);
        var evidence = (IDictionary<string, object?>)trace.Metadata!["gate.run-post.1.judge-panel"];
        Assert.Equal("Block", evidence["action"]);
        Assert.Equal(1, exfilModel.CallCount);                                        // exfil judge did the blocking
        Assert.Equal(0, leakModel.CallCount);                                         // leak judge short-circuited (no leak signal)
    }

    [Fact]
    public async Task Panel_RunPost_AllowsBenignOutput_Unchanged()
    {
        // A benign order-status answer trips neither prefilter → both judges short-circuit → the panel allows and the
        // answer passes through verbatim, at zero token cost.
        const string benign = "Your order #A-1042 shipped Tuesday via UPS; delivery is Friday.";
        var inner = new ScriptedChatClient().AddText(benign);
        var exfilModel = new ScriptedChatClient().AddText("{\"exfiltrates\": false, \"confidence\": 0.9, \"evidence\": \"\"}");
        var leakModel = new ScriptedChatClient().AddText("{\"leaks\": false, \"confidence\": 0.9, \"evidence\": \"\"}");

        var panel = new ParallelJudgeFanOut(
        [
            ExfiltrationIntentJudge.Create(exfilModel, cache: false),
            SystemPromptExtractionJudge.Create(leakModel, cache: false),
        ]);
        var trace = new AgentTrace();

        var gated = Agent(inner).AsBuilder()
            .UseAgentEvalGate(post: [panel], policy: EvalGatePolicy.Redact, trace: trace)
            .Build();

        var response = await gated.RunAsync("what's my order status?");

        Assert.Equal(benign, response.Text);                                          // the answer passed through unchanged
        Assert.Equal(0, GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0);
        Assert.Equal(0, exfilModel.CallCount);                                        // neither prefilter fired — zero tokens
        Assert.Equal(0, leakModel.CallCount);
    }
}
