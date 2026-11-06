// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Models;
using AgentEval.Testing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentEval.Tests.MAF;

/// <summary>
/// The turn after an unanswered tool approval, read through <see cref="MAFAgentAdapter"/> on a real
/// <see cref="ChatClientAgent"/> over a scripted client (no network). The adapter reuses one session, so the second
/// <see cref="MAFAgentAdapter.InvokeAsync"/> runs while the first turn's approval request is still unanswered.
/// </summary>
/// <remarks>
/// Pins MAF #9152 (1.25+): MAF rejects the saved request on that run, and the run's messages hold, in order, the
/// generated <see cref="ToolApprovalResponseContent"/> (not approved), the <see cref="FunctionCallContent"/> that
/// <c>FunctionInvokingChatClient</c> re-creates for it, its "rejected" <see cref="FunctionResultContent"/>, then the
/// reply. On MAF 1.24 the second turn threw. The tool body never runs.
/// </remarks>
public sealed class MAFApprovalContinuationTests
{
    private const string FirstPrompt = "refund $5000";
    private const string SecondPrompt = "Never mind. What are your opening hours?";

    // Fails on HEAD 143696a9: the default path recorded the re-created call as a plain call and paired it with the
    // rejection result, so a tool that never ran read as executed. Passes once the extractor keys the re-created call
    // to the rejected decision's CallId and name.
    [Fact]
    public async Task SecondTurn_AfterUnansweredApproval_DefaultToolUsage_HasNoExecutedCall()
    {
        var (agent, executed) = BuildEscalatingAgent();
        var adapter = new MAFAgentAdapter(agent);

        var paused = await adapter.InvokeAsync(FirstPrompt);
        var next = await adapter.InvokeAsync(SecondPrompt);

        Assert.Equal(0, executed());
        AssertAutoRejectionShape(next.RawMessages);

        var first = ToolUsageExtractor.Extract(paused);
        Assert.Empty(first.Calls);
        Assert.Equal(1, first.DroppedApprovalRequestCount);

        var usage = ToolUsageExtractor.Extract(next);
        Assert.DoesNotContain(usage.Calls, call => call.WasExecuted);
        Assert.Empty(usage.Calls);                          // the gated call is counted as dropped, not recorded
        Assert.Equal(1, usage.DroppedApprovalRequestCount);
        Assert.Equal(new[] { "issue_refund" }, ToolUsageExtractor.ApprovalGatedToolNames(next.RawMessages));
    }

    // Passes on HEAD 143696a9: the opt-in path already folds the re-created call into the rejected decision.
    [Fact]
    public async Task SecondTurn_AfterUnansweredApproval_OptInToolUsage_RecordsOneRejectedCall()
    {
        var (agent, executed) = BuildEscalatingAgent();
        var adapter = new MAFAgentAdapter(agent);

        await adapter.InvokeAsync(FirstPrompt);
        var next = await adapter.InvokeAsync(SecondPrompt);

        Assert.Equal(0, executed());
        AssertAutoRejectionShape(next.RawMessages);

        var usage = ToolUsageExtractor.Extract(next, includeApprovalGatedCalls: true);
        var call = Assert.Single(usage.Calls);
        Assert.Equal("issue_refund", call.Name);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal(ToolCallRecord.ApprovalRejected, call.ApprovalState);
        Assert.False(call.WasExecuted);
    }

    // The second turn must really carry MAF's auto-rejection, or the tool-usage assertions above would pass on a
    // reply with no tool content at all.
    private static void AssertAutoRejectionShape(IReadOnlyList<object>? rawMessages)
    {
        Assert.NotNull(rawMessages);
        var contents = rawMessages.OfType<ChatMessage>().SelectMany(m => m.Contents).ToList();
        var decision = Assert.Single(contents.OfType<ToolApprovalResponseContent>());
        Assert.False(decision.Approved);
        var decided = Assert.IsType<FunctionCallContent>(decision.ToolCall);
        Assert.Equal("call_1", decided.CallId);
        Assert.Equal("issue_refund", decided.Name);
        Assert.Contains(contents, c => c is FunctionCallContent { CallId: "call_1", Name: "issue_refund" });
        Assert.Contains(contents, c => c is FunctionResultContent { CallId: "call_1" });
        Assert.DoesNotContain(contents, c => c is ToolApprovalRequestContent);
    }

    private static (AIAgent Agent, Func<int> Executed) BuildEscalatingAgent()
    {
        var executed = 0;
        var refund = AIFunctionFactory.Create(
            (int amount) => { Interlocked.Increment(ref executed); return $"refunded {amount}"; }, "issue_refund");

        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", "issue_refund", new Dictionary<string, object?> { ["amount"] = 5000 })
            .AddText("We are open from 9 to 5.");

        // ToolNameApprovalGate escalates every call to the named tool, so the first turn always pauses for a human.
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "Support",
            ChatOptions = new ChatOptions { Tools = [refund.RequiresApproval()] },
        })
            .AsBuilder()
            .UseAgentEvalToolApproval([new ToolNameApprovalGate(["issue_refund"])])
            .Build();

        return (agent, () => executed);
    }
}
