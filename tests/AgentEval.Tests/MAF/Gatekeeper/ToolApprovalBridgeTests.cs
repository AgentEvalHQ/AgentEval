// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using AgentEval.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentTrace = AgentEval.Tracing.AgentTrace;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper ⇄ MAF <c>UseToolApproval</c> interop: a routine call auto-approves (the tool runs), a borderline
/// call escalates to human approval (the run pauses and surfaces a <see cref="ToolApprovalRequestContent"/>).
/// </summary>
[Experimental("AEGK001")]
public class ToolApprovalBridgeTests
{
    // Escalate unless the amount is a plain number below 1000 (and when there is none). The gate auto-approves what its
    // pattern does not match, so the pattern names what is routine: the earlier "amount":\s*[0-9]{4,} let
    // "amount":"5000" (a JSON string) through.
    private const string EscalateLargeRefund =
        @"^(?![\s\S]*""amount""\s*:)|""amount""\s*:(?!\s*[0-9]{1,3}(?:\.[0-9]+)?\s*[,}])";

    private static (AIAgent Agent, Func<int> Executed) BuildGatedAgent(
        IReadOnlyList<IToolApprovalGate> gates, IDictionary<string, object?> callArgs, AgentTrace? trace = null)
    {
        var executed = 0;
        var refund = AIFunctionFactory.Create(
            (int amount) => { Interlocked.Increment(ref executed); return $"refunded {amount}"; }, "issue_refund");

        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", "issue_refund", callArgs)
            .AddText("done");

        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "Support",
            ChatOptions = new ChatOptions { Tools = [refund.RequiresApproval()] },
        })
            .AsBuilder()
            .UseAgentEvalToolApproval(gates, trace)
            .Build();

        return (agent, () => executed);
    }

    [Fact]
    public async Task RoutineCall_AllGatesAutoApprove_ToolRuns()
    {
        var gate = new ArgumentPatternApprovalGate(EscalateLargeRefund);
        var (agent, executed) = BuildGatedAgent([gate], new Dictionary<string, object?> { ["amount"] = 20 });

        var response = await agent.RunAsync("refund $20");

        Assert.Equal(1, executed());   // routine ⇒ auto-approved ⇒ the tool ran
        Assert.DoesNotContain(response.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
    }

    [Theory]
    [InlineData(5000)]
    [InlineData("5000")]
    [InlineData("5,000")]
    public async Task LargeAmount_InAnySpelling_Escalates(object amount)
    {
        // A large amount sent as a JSON string auto-approved under the old pattern, and the tool still read it as 5000.
        var gate = new ArgumentPatternApprovalGate(EscalateLargeRefund);

        Assert.False(await gate.IsAutoApprovableAsync(
            new FunctionCallContent("c1", "issue_refund", new Dictionary<string, object?> { ["amount"] = amount })));
    }

    [Fact]
    public async Task BorderlineCall_GateEscalates_SurfacesApprovalRequest_ToolDoesNotRun()
    {
        var trace = new AgentTrace();
        var gate = new ArgumentPatternApprovalGate(EscalateLargeRefund);
        var (agent, executed) = BuildGatedAgent([gate], new Dictionary<string, object?> { ["amount"] = 5000 }, trace);

        var response = await agent.RunAsync("refund $5000");

        Assert.Equal(0, executed());   // paused for a human — the tool did NOT run
        Assert.Contains(response.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
        // The escalation is recorded as gate.approval.* evidence (NOT a gate.tool.* block).
        Assert.Contains(trace.Metadata!.Keys, k => k.StartsWith("gate.approval.", StringComparison.Ordinal));
        Assert.Equal(0, GlassBoxEvidence.FromTrace(trace)?.GateBlockCount ?? 0);
    }

    [Fact]
    public async Task ApproveThenResume_ToolRunsAfterHumanApproval()
    {
        var executed = 0;
        var refund = AIFunctionFactory.Create(
            (int amount) => { Interlocked.Increment(ref executed); return $"refunded {amount}"; }, "issue_refund");
        var scripted = new ScriptedChatClient()
            .AddToolCall("call_1", "issue_refund", new Dictionary<string, object?> { ["amount"] = 5000 })
            .AddText("refund issued");
        var agent = new ChatClientAgent(scripted, new ChatClientAgentOptions
        {
            Name = "Support",
            ChatOptions = new ChatOptions { Tools = [refund.RequiresApproval()] },
        })
            .AsBuilder()
            .UseAgentEvalToolApproval([new ArgumentPatternApprovalGate(EscalateLargeRefund)])
            .Build();

        var session = await agent.CreateSessionAsync();
        var paused = await agent.RunAsync("refund $5000", session);
        Assert.Equal(0, executed);   // escalated — paused for the human
        var request = paused.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();

        // The human approves → resume on the SAME session with the approval response.
        await agent.RunAsync([new ChatMessage(ChatRole.User, [request.CreateResponse(true)])], session);

        Assert.Equal(1, executed);   // approved ⇒ the tool finally ran
    }

    // Pins MAF #9152 (1.25+): the session runs again while a saved approval request was never answered. MAF's
    // ApprovalResponseBindingChatClient (on unless DisableApprovalResponseBinding) rejects it and returns the generated
    // decision in the response. On MAF 1.24 this second run threw InvalidOperationException.
    [Fact]
    public async Task UnansweredApproval_NextRunOnSameSession_IsRejectedByMaf_ToolNeverRuns()
    {
        var gate = new CountingApprovalGate(new ArgumentPatternApprovalGate(EscalateLargeRefund));
        var (agent, executed) = BuildGatedAgent([gate], new Dictionary<string, object?> { ["amount"] = 5000 });
        var session = await agent.CreateSessionAsync();

        var paused = await agent.RunAsync("refund $5000", session);
        var request = paused.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();
        Assert.Equal(1, gate.Calls);

        // No decision: the caller sends new text on the same session.
        var next = await agent.RunAsync("Never mind. What are your opening hours?", session);

        Assert.Equal(0, executed());   // the unanswered call never ran
        var decision = Assert.Single(next.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalResponseContent>());
        Assert.False(decision.Approved);
        Assert.Equal(request.RequestId, decision.RequestId);
        var call = Assert.IsType<FunctionCallContent>(decision.ToolCall);
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("issue_refund", call.Name);
        Assert.Equal(1, gate.Calls);   // MAF rejected the request itself; the Gatekeeper rule was not asked again
    }

    // The human rejects, and the run resumes on the same session with that decision: the tool never runs. Guards the
    // reject path of MAF 1.27's rewritten approval binding (the saved call is copied and bound to the decision).
    [Fact]
    public async Task RejectThenResume_ToolNeverRuns()
    {
        var gate = new CountingApprovalGate(new ArgumentPatternApprovalGate(EscalateLargeRefund));
        var (agent, executed) = BuildGatedAgent([gate], new Dictionary<string, object?> { ["amount"] = 5000 });
        var session = await agent.CreateSessionAsync();

        var paused = await agent.RunAsync("refund $5000", session);
        Assert.Equal(0, executed());   // escalated: paused for the human
        var request = paused.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().Single();

        var decisionMessage = new ChatMessage(ChatRole.User, [request.CreateResponse(approved: false, reason: "Amount too large.")]);
        var resumed = await agent.RunAsync([decisionMessage], session);

        Assert.Equal(0, executed());   // rejected: the tool never ran
        Assert.DoesNotContain(resumed.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
        Assert.Equal(1, gate.Calls);   // the decision came from the human; the rule ran only for the first request

        // Known limitation, pinned so it is not mistaken for fixed: MAF does not return a decision the caller sent as
        // input, only the call it re-created and that call's "rejected" result. Read alone, the response gives the
        // extractor nothing to key on, so it reports the call as executed, as on MAF 1.24.
        var contents = resumed.Messages.SelectMany(m => m.Contents).ToList();
        Assert.DoesNotContain(contents, c => c is ToolApprovalResponseContent);
        Assert.Contains(contents, c => c is FunctionCallContent { CallId: "call_1" });
        Assert.Contains(contents, c => c is FunctionResultContent { CallId: "call_1" });
        var responseOnly = resumed.Messages.Cast<object>().ToList();
        Assert.True(Assert.Single(AgentEval.Core.ToolUsageExtractor.Extract(responseOnly).Calls).WasExecuted);

        // With the caller's decision passed ahead of the response, the rule applies. Fails on HEAD 143696a9, where the
        // default path recorded the re-created call as executed even then.
        var withDecision = new List<object> { decisionMessage };
        withDecision.AddRange(responseOnly);
        var blind = AgentEval.Core.ToolUsageExtractor.Extract(withDecision);
        Assert.Empty(blind.Calls);
        Assert.Equal(1, blind.DroppedApprovalRequestCount);
        var opted = Assert.Single(AgentEval.Core.ToolUsageExtractor.Extract(withDecision, includeApprovalGatedCalls: true).Calls);
        Assert.Equal(AgentEval.Models.ToolCallRecord.ApprovalRejected, opted.ApprovalState);
        Assert.False(opted.WasExecuted);
    }

    [Fact]
    public async Task GateThrows_FailsClosed_EscalatesToHuman_ToolDoesNotRun()
    {
        var trace = new AgentTrace();
        var (agent, executed) = BuildGatedAgent([new ThrowingApprovalGate()], new Dictionary<string, object?> { ["amount"] = 20 }, trace);

        var response = await agent.RunAsync("refund $20");

        Assert.Equal(0, executed());   // a gate that throws cannot prove the call routine ⇒ escalate (fail-closed)
        Assert.Contains(response.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
        Assert.Contains(trace.Metadata!.Keys, k => k.StartsWith("gate.approval.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ArgumentPatternGate_ParameterlessCall_Escalates_NotSilentlyApproved()
    {
        // An argument gate has no basis to vouch for a NO-ARG call ⇒ escalate (fail-closed). A parameterless
        // .RequiresApproval() tool must never be silently auto-approved just because there are no args to match.
        var executed = 0;
        var wipe = AIFunctionFactory.Create(() => { Interlocked.Increment(ref executed); return "wiped"; }, "wipe_all_data");
        var agent = new ChatClientAgent(
            new ScriptedChatClient().AddToolCall("c1", "wipe_all_data", new Dictionary<string, object?>()).AddText("done"),
            new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [wipe.RequiresApproval()] } })
            .AsBuilder().UseAgentEvalToolApproval([new ArgumentPatternApprovalGate(EscalateLargeRefund)]).Build();

        var response = await agent.RunAsync("wipe everything");

        Assert.Equal(0, executed);
        Assert.Contains(response.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
    }

    [Fact]
    public async Task ToolNameApprovalGate_EscalatesListedTool_RegardlessOfArgs()
    {
        var executed = 0;
        var transfer = AIFunctionFactory.Create((int amount) => { Interlocked.Increment(ref executed); return "sent"; }, "wire_transfer");
        var agent = new ChatClientAgent(
            new ScriptedChatClient().AddToolCall("c1", "wire_transfer", new Dictionary<string, object?> { ["amount"] = 5 }).AddText("done"),
            new ChatClientAgentOptions { Name = "T", ChatOptions = new ChatOptions { Tools = [transfer.RequiresApproval()] } })
            .AsBuilder().UseAgentEvalToolApproval([new ToolNameApprovalGate(["wire_transfer"])]).Build();

        var response = await agent.RunAsync("wire $5");

        Assert.Equal(0, executed);   // escalated by identity — even a tiny transfer needs a human
        Assert.Contains(response.Messages.SelectMany(m => m.Contents), c => c is ToolApprovalRequestContent);
    }

    // ── Guards ──

    [Fact]
    public void UseAgentEvalToolApproval_EmptyGates_Throws()   // fail-closed: an empty list would auto-approve everything
        => Assert.Throws<ArgumentException>(() => NewBuilder().UseAgentEvalToolApproval([]));

    [Fact]
    public void RequiresApproval_NullFunction_Throws()
        => Assert.Throws<ArgumentNullException>(() => ((AIFunction)null!).RequiresApproval());

    [Fact]
    public void UseAgentEvalToolApproval_NullGates_Throws()
        => Assert.Throws<ArgumentNullException>(() => NewBuilder().UseAgentEvalToolApproval(null!));

    [Fact]
    public void UseAgentEvalToolApproval_NullElement_Throws()
        => Assert.Throws<ArgumentException>(() => NewBuilder().UseAgentEvalToolApproval([null!]));

    private static AIAgentBuilder NewBuilder()
        => new ChatClientAgent(new ScriptedChatClient().AddText("hi"), new ChatClientAgentOptions { Name = "T" }).AsBuilder();

    private sealed class ThrowingApprovalGate : IToolApprovalGate
    {
        public string PolicyName => "throwing-gate";
        public ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    // Counts how often MAF's auto-approval rule consults the gate, then defers to the wrapped gate.
    private sealed class CountingApprovalGate(IToolApprovalGate inner) : IToolApprovalGate
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string PolicyName => inner.PolicyName;

        public ValueTask<bool> IsAutoApprovableAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.IsAutoApprovableAsync(call, cancellationToken);
        }
    }
}
