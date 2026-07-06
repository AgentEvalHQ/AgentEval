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
    // Escalate only large refunds (amount with 4+ digits, i.e. 1000+); a small refund is routine.
    private const string EscalateLargeRefund = "\"amount\":\\s*[0-9]{4,}";

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

    // ── Guards ──

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
}
