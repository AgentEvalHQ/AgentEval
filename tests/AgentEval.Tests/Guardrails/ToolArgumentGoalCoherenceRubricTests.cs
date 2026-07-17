// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class ToolArgumentGoalCoherenceRubricTests
{
    private readonly ToolArgumentGoalCoherenceRubric _rubric = new();

    [Fact]
    public void Axis_IsToolArgumentGoalCoherence() => Assert.Equal("tool-argument-goal-coherence", _rubric.Axis);

    [Fact]
    public void Prefilter_NonEmptyText_ReturnsTrue() =>
        Assert.True(_rubric.Prefilter(ToolArgumentGoalCoherenceRubric.FormatCase("refund $12", "process_refund", "{}")));

    [Fact]
    public void Prefilter_EmptyText_ReturnsFalse() => Assert.False(_rubric.Prefilter(""));

    [Fact]
    public void Parse_IncoherentTrue_ReturnsBlocked()
    {
        var verdict = _rubric.Parse("""{"incoherent": true, "confidence": 0.9, "evidence": "amount"}""");
        Assert.Equal(JudgeDecision.Blocked, verdict.Decision);
    }

    [Fact]
    public void Parse_IncoherentFalse_ReturnsAllowed()
    {
        var verdict = _rubric.Parse("""{"incoherent": false, "confidence": 0.9}""");
        Assert.Equal(JudgeDecision.Allowed, verdict.Decision);
    }

    [Fact]
    public void Parse_MissingIncoherentField_ReturnsInconclusive()
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
    public void FormatCase_CombinesGoalAndToolCall()
    {
        var text = ToolArgumentGoalCoherenceRubric.FormatCase("refund $12 for order A-2", "process_refund", "{\"amount\":1200}");
        Assert.Contains("refund $12 for order A-2", text);
        Assert.Contains("process_refund", text);
        Assert.Contains("1200", text);
    }

    [Fact]
    public void GoldSet_IsBothDirections_AboveMinCasesPerDirection()
    {
        var goldSet = ToolArgumentGoalCoherenceJudge.GoldSet();
        Assert.True(goldSet.AttackCount >= 20, $"expected >=20 incoherent cases, got {goldSet.AttackCount}");
        Assert.True(goldSet.BenignCount >= 20, $"expected >=20 coherent cases, got {goldSet.BenignCount}");
    }

    [Fact]
    public void GoldSet_HasNoDuplicateCaseText()
    {
        var goldSet = ToolArgumentGoalCoherenceJudge.GoldSet();
        var texts = goldSet.Cases.Select(c => c.Text).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void GoldSet_AxisMatchesRubricAxis()
    {
        var goldSet = ToolArgumentGoalCoherenceJudge.GoldSet();
        Assert.Equal(_rubric.Axis, goldSet.Axis);
    }
}
