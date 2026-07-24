// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Gatekeeper;
using Microsoft.Extensions.AI;
using Xunit;

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
}
