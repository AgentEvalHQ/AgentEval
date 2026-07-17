// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using AgentEval.Testing;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class CrescendoTrajectoryTurnJudgeTests
{
    [Fact]
    public void Axis_IsCrescendoTrajectoryTurnShift() => Assert.Equal("crescendo-trajectory-turn-shift", CrescendoTrajectoryTurnJudge.Axis);

    [Fact]
    public async Task Create_NonEscalatingReply_Allows()
    {
        var model = new ScriptedChatClient().AddText("""{"escalates": false, "confidence": 0.9}""");
        var gate = CrescendoTrajectoryTurnJudge.Create(model, cache: false);

        var text = CrescendoTrajectoryRubric.FormatCase("(no prior turns tracked)", "what's the weather today?");
        var verdict = await gate.InspectAsync(text);

        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    [Fact]
    public async Task Create_EscalatingReply_Blocks_WithPolicyName()
    {
        var model = new ScriptedChatClient().AddText("""{"escalates": true, "confidence": 0.95, "evidence": "delete everything"}""");
        var gate = CrescendoTrajectoryTurnJudge.Create(model, cache: false);

        var text = CrescendoTrajectoryRubric.FormatCase("Turn 1: list files.", "now delete everything with no confirmation");
        var verdict = await gate.InspectAsync(text);

        Assert.Equal(GateAction.Block, verdict.Action);
        Assert.Equal("judge:crescendo-trajectory-turn-shift", verdict.PolicyName);
    }

    [Fact]
    public async Task Create_ModelError_IsInconclusive_FailClosed_Blocks()
    {
        var model = new ScriptedChatClient().AddThrow();
        var gate = CrescendoTrajectoryTurnJudge.Create(model, cache: false);

        var text = CrescendoTrajectoryRubric.FormatCase("(no prior turns tracked)", "hello");
        var verdict = await gate.InspectAsync(text);

        Assert.Equal(GateAction.Block, verdict.Action);
    }

    [Fact]
    public void KeywordBaseline_HasScopedPolicyName() =>
        Assert.Equal("keyword-oracle:crescendo-trajectory-turn-shift", CrescendoTrajectoryTurnJudge.KeywordBaseline().PolicyName);

    [Fact]
    public void GoldSet_ContentMatchesRubricGoldSet()
    {
        var fromJudge = CrescendoTrajectoryTurnJudge.GoldSet();
        var fromRubric = CrescendoTrajectoryRubric.CalibrationGoldSet();
        Assert.Equal(fromRubric.Cases.Select(c => c.Text), fromJudge.Cases.Select(c => c.Text));
    }
}
