// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;
using AgentEval.MAF.Gatekeeper;
using AgentEval.Testing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// The approval-flow half of the tool-argument/goal-coherence judge: the only seam that can act on this
/// judge's verdict for a specific pending tool call today (see the judge's own doc comment for why the
/// deterministic hard-block seam is not available).
/// </summary>
public class ToolArgumentGoalCoherenceApprovalGateTests
{
    private static FunctionCallContent Call(string name, IDictionary<string, object?>? args = null) =>
        new("call_1", name, args ?? new Dictionary<string, object?>());

    [Fact]
    public void Constructor_NullFastModel_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new ToolArgumentGoalCoherenceApprovalGate(null!, "refund $12"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankGoal_Throws(string? goal) =>
        Assert.Throws<ArgumentException>(() => new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), goal!));

    [Fact]
    public void Constructor_FailClosedOnInconclusiveFalse_Throws()
    {
        // This gate's whole safety story is "uncertainty always escalates" — a caller-supplied options that
        // fails OPEN on inconclusive would silently undermine that (and, since caching defaults to true, make
        // the mistaken auto-approval sticky). Reject it outright rather than accept it silently.
        var options = new JudgeGateOptions { FailClosedOnInconclusive = false };
        var ex = Assert.Throws<ArgumentException>(() => new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "refund $12", options));
        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Constructor_FailClosedOnInconclusiveTrueOrDefault_Accepted()
    {
        // The default JudgeGateOptions (FailClosedOnInconclusive == true) must NOT trip the new guard.
        _ = new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "refund $12", new JudgeGateOptions());
        _ = new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "refund $12", options: null);
    }

    [Fact]
    public void PolicyName_DefaultsToJudgeAxisName()
    {
        var gate = new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "refund $12");
        Assert.Equal("judge:tool-argument-goal-coherence", gate.PolicyName);
    }

    [Fact]
    public void PolicyName_CustomOverride_Honored()
    {
        var gate = new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "refund $12", policyName: "custom-policy");
        Assert.Equal("custom-policy", gate.PolicyName);
    }

    [Fact]
    public async Task IsAutoApprovableAsync_CoherentVerdict_ReturnsTrue()
    {
        var model = new ScriptedChatClient().AddText("""{"incoherent": false, "confidence": 0.9}""");
        var gate = new ToolArgumentGoalCoherenceApprovalGate(model, "refund $12 for order A-2");

        var approvable = await gate.IsAutoApprovableAsync(Call("process_refund", new Dictionary<string, object?> { ["amount"] = 1200 }));

        Assert.True(approvable);
    }

    [Fact]
    public async Task IsAutoApprovableAsync_IncoherentVerdict_ReturnsFalse_Escalates()
    {
        var model = new ScriptedChatClient().AddText("""{"incoherent": true, "confidence": 0.95, "evidence": "amount"}""");
        var gate = new ToolArgumentGoalCoherenceApprovalGate(model, "refund $12 for order A-2");

        var approvable = await gate.IsAutoApprovableAsync(Call("process_refund", new Dictionary<string, object?> { ["amount"] = 99999 }));

        Assert.False(approvable);
    }

    [Fact]
    public async Task IsAutoApprovableAsync_JudgeInconclusive_ReturnsFalse_Escalates()
    {
        // Fail-closed default (JudgeGateOptions.FailClosedOnInconclusive == true): an error/timeout never
        // auto-approves — it escalates to a human, exactly like a confident-incoherent verdict.
        var model = new ScriptedChatClient().AddThrow();
        var gate = new ToolArgumentGoalCoherenceApprovalGate(model, "refund $12 for order A-2");

        var approvable = await gate.IsAutoApprovableAsync(Call("process_refund", new Dictionary<string, object?> { ["amount"] = 1200 }));

        Assert.False(approvable);
    }

    [Fact]
    public async Task IsAutoApprovableAsync_ParameterlessCall_StillJudged_NotAutomaticallyEscalated()
    {
        // Unlike ArgumentPatternApprovalGate (which escalates any zero-argument call — a regex has no basis to
        // vouch for it), this judge CAN reason about a parameterless call against the stated goal, so a
        // coherent parameterless call auto-approves rather than being escalated on argument-count alone.
        var model = new ScriptedChatClient().AddText("""{"incoherent": false, "confidence": 0.9}""");
        var gate = new ToolArgumentGoalCoherenceApprovalGate(model, "get the current return policy");

        var approvable = await gate.IsAutoApprovableAsync(Call("get_return_policy"));

        Assert.True(approvable);
    }

    [Fact]
    public async Task IsAutoApprovableAsync_UnserializableArguments_NonNotSupportedException_StillEscalates()
    {
        // The catch around JsonSerializer.Serialize must be broad, not just NotSupportedException — a custom
        // getter can throw any exception type. The judge is never consulted in this path (would ALSO block if
        // it were), so the test only needs to prove escalation, not that the model was skipped.
        var model = new ScriptedChatClient().AddText("""{"incoherent": false, "confidence": 0.9}""");
        var gate = new ToolArgumentGoalCoherenceApprovalGate(model, "refund $12");
        var args = new Dictionary<string, object?> { ["value"] = new ThrowingOnSerialize() };

        var approvable = await gate.IsAutoApprovableAsync(Call("process_refund", args));

        Assert.False(approvable);
    }

    private sealed class ThrowingOnSerialize
    {
        public string Boom => throw new InvalidOperationException("getter boom — not NotSupportedException");
    }

    [Fact]
    public async Task IsAutoApprovableAsync_NullCall_Throws()
    {
        var gate = new ToolArgumentGoalCoherenceApprovalGate(new ScriptedChatClient(), "refund $12");
        await Assert.ThrowsAsync<ArgumentNullException>(() => gate.IsAutoApprovableAsync(null!).AsTask());
    }

    [Fact]
    public async Task IsAutoApprovableAsync_SendsGoalAndCallToTheModel()
    {
        var model = new ScriptedChatClient().AddText("""{"incoherent": false, "confidence": 0.9}""");
        var gate = new ToolArgumentGoalCoherenceApprovalGate(model, "refund $12 for order A-2");

        await gate.IsAutoApprovableAsync(Call("process_refund", new Dictionary<string, object?> { ["amount"] = 1200 }));

        var sent = string.Concat(model.ReceivedMessages.SelectMany(batch => batch).Select(m => m.Text));
        Assert.Contains("refund $12 for order A-2", sent);
        Assert.Contains("process_refund", sent);
    }
}
