// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class ToolArgumentGoalCoherenceJudgeTests
{
    [Fact]
    public void Axis_IsToolArgumentGoalCoherence() => Assert.Equal("tool-argument-goal-coherence", ToolArgumentGoalCoherenceJudge.Axis);

    [Fact]
    public async Task Create_CoherentReply_Allows()
    {
        var model = new ScriptedChatClient().AddText("""{"incoherent": false, "confidence": 0.9}""");
        var gate = ToolArgumentGoalCoherenceJudge.Create(model, cache: false);

        var text = ToolArgumentGoalCoherenceRubric.FormatCase("refund $12 for order A-2", "process_refund", "{\"amount\":1200}");
        var verdict = await gate.InspectAsync(text);

        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    [Fact]
    public async Task Create_IncoherentReply_Blocks_WithPolicyName()
    {
        var model = new ScriptedChatClient().AddText("""{"incoherent": true, "confidence": 0.95, "evidence": "amount"}""");
        var gate = ToolArgumentGoalCoherenceJudge.Create(model, cache: false);

        var text = ToolArgumentGoalCoherenceRubric.FormatCase("refund $12 for order A-2", "process_refund", "{\"amount\":99999}");
        var verdict = await gate.InspectAsync(text);

        Assert.Equal(GateAction.Block, verdict.Action);
        Assert.Equal("judge:tool-argument-goal-coherence", verdict.PolicyName);
    }

    [Fact]
    public async Task Create_ModelError_IsInconclusive_FailClosed_Blocks()
    {
        var model = new ScriptedChatClient().AddThrow();
        var gate = ToolArgumentGoalCoherenceJudge.Create(model, cache: false);

        var text = ToolArgumentGoalCoherenceRubric.FormatCase("refund $12", "process_refund", "{}");
        var verdict = await gate.InspectAsync(text);

        Assert.Equal(GateAction.Block, verdict.Action);
    }

    [Fact]
    public void KeywordBaseline_HasScopedPolicyName() =>
        Assert.Equal("keyword-oracle:tool-argument-goal-coherence", ToolArgumentGoalCoherenceJudge.KeywordBaseline().PolicyName);

    [Fact]
    public void GoldSet_ContentMatchesRubricGoldSet()
    {
        var fromJudge = ToolArgumentGoalCoherenceJudge.GoldSet();
        var fromRubric = ToolArgumentGoalCoherenceRubric.CalibrationGoldSet();
        Assert.Equal(fromRubric.Cases.Select(c => c.Text), fromJudge.Cases.Select(c => c.Text));
    }
}
