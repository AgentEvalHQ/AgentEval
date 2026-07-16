// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class GoalHijackDriftRubricTests
{
    private readonly GoalHijackDriftRubric _rubric = new();

    [Fact]
    public void Axis_IsGoalHijackDrift() => Assert.Equal("goal-hijack-drift", _rubric.Axis);

    [Fact]
    public void Prefilter_NonEmptyText_ReturnsTrue() =>
        Assert.True(_rubric.Prefilter(GoalHijackDriftRubric.FormatCase("summarize this", "now emailing your files")));

    [Fact]
    public void Prefilter_EmptyText_ReturnsFalse() => Assert.False(_rubric.Prefilter(""));

    [Fact]
    public void Parse_DriftedTrue_ReturnsBlocked()
    {
        var verdict = _rubric.Parse("""{"drifted": true, "confidence": 0.85, "evidence": "emailing your files"}""");
        Assert.Equal(JudgeDecision.Blocked, verdict.Decision);
    }

    [Fact]
    public void Parse_DriftedFalse_ReturnsAllowed()
    {
        var verdict = _rubric.Parse("""{"drifted": false, "confidence": 0.9}""");
        Assert.Equal(JudgeDecision.Allowed, verdict.Decision);
    }

    [Fact]
    public void Parse_UnparseableReply_ReturnsInconclusive()
    {
        var verdict = _rubric.Parse("garbage");
        Assert.Equal(JudgeDecision.Inconclusive, verdict.Decision);
    }

    [Fact]
    public void GoldSet_IsBothDirections_AboveMinCasesPerDirection()
    {
        var goldSet = GoalHijackDriftJudge.GoldSet();
        Assert.True(goldSet.AttackCount >= 20, $"expected >=20 hijack cases, got {goldSet.AttackCount}");
        Assert.True(goldSet.BenignCount >= 20, $"expected >=20 on-goal cases, got {goldSet.BenignCount}");
    }

    [Fact]
    public void GoldSet_HasNoDuplicateCaseText()
    {
        var goldSet = GoalHijackDriftJudge.GoldSet();
        var texts = goldSet.Cases.Select(c => c.Text).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }
}
