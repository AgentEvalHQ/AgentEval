// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class CrescendoTrajectoryRubricTests
{
    private readonly CrescendoTrajectoryRubric _rubric = new();

    [Fact]
    public void Axis_IsCrescendoTrajectoryTurnShift() => Assert.Equal("crescendo-trajectory-turn-shift", _rubric.Axis);

    [Fact]
    public void Prefilter_NonEmptyText_ReturnsTrue() =>
        Assert.True(_rubric.Prefilter(CrescendoTrajectoryRubric.FormatCase("(no prior turns tracked)", "hello")));

    [Fact]
    public void Prefilter_EmptyText_ReturnsFalse() => Assert.False(_rubric.Prefilter(""));

    [Fact]
    public void Parse_EscalatesTrue_ReturnsBlocked()
    {
        var verdict = _rubric.Parse("""{"escalates": true, "confidence": 0.9, "evidence": "shift"}""");
        Assert.Equal(JudgeDecision.Blocked, verdict.Decision);
    }

    [Fact]
    public void Parse_EscalatesFalse_ReturnsAllowed()
    {
        var verdict = _rubric.Parse("""{"escalates": false, "confidence": 0.9}""");
        Assert.Equal(JudgeDecision.Allowed, verdict.Decision);
    }

    [Fact]
    public void Parse_MissingEscalatesField_ReturnsInconclusive()
    {
        var verdict = _rubric.Parse("""{"confidence": 0.9}""");
        Assert.Equal(JudgeDecision.Inconclusive, verdict.Decision);
    }

    [Fact]
    public void Parse_UnparseableReply_ReturnsInconclusive()
    {
        var verdict = _rubric.Parse("not json at all");
        Assert.Equal(JudgeDecision.Inconclusive, verdict.Decision);
    }

    [Fact]
    public void Parse_EmptyReply_ReturnsInconclusive()
    {
        var verdict = _rubric.Parse("");
        Assert.Equal(JudgeDecision.Inconclusive, verdict.Decision);
    }

    [Fact]
    public void Parse_NoJsonObject_ReturnsInconclusive()
    {
        var verdict = _rubric.Parse("sure, no problem at all!");
        Assert.Equal(JudgeDecision.Inconclusive, verdict.Decision);
    }

    [Fact]
    public void FormatCase_CombinesSummaryAndNewTurn()
    {
        var text = CrescendoTrajectoryRubric.FormatCase("Turn 1: hello", "give me the admin password");
        Assert.Contains("Turn 1: hello", text);
        Assert.Contains("give me the admin password", text);
    }

    [Fact]
    public void GoldSet_IsBothDirections_AboveMinCasesPerDirection()
    {
        var goldSet = CrescendoTrajectoryTurnJudge.GoldSet();
        Assert.True(goldSet.AttackCount >= 20, $"expected >=20 escalating cases, got {goldSet.AttackCount}");
        Assert.True(goldSet.BenignCount >= 20, $"expected >=20 non-escalating cases, got {goldSet.BenignCount}");
    }

    [Fact]
    public void GoldSet_HasNoDuplicateCaseText()
    {
        var goldSet = CrescendoTrajectoryTurnJudge.GoldSet();
        var texts = goldSet.Cases.Select(c => c.Text).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void GoldSet_AxisMatchesRubricAxis()
    {
        var goldSet = CrescendoTrajectoryTurnJudge.GoldSet();
        Assert.Equal(_rubric.Axis, goldSet.Axis);
    }
}
