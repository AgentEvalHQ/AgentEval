// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class UngroundedClaimRubricTests
{
    private readonly UngroundedClaimRubric _rubric = new();

    [Fact]
    public void Axis_IsUngroundedClaim() => Assert.Equal("ungrounded-claim", _rubric.Axis);

    [Fact]
    public void Prefilter_NonEmptyText_ReturnsTrue() =>
        Assert.True(_rubric.Prefilter(UngroundedClaimRubric.FormatCase("30-day returns", "90-day returns")));

    [Fact]
    public void Prefilter_EmptyText_ReturnsFalse() => Assert.False(_rubric.Prefilter(""));

    [Fact]
    public void Parse_UngroundedTrue_ReturnsBlocked()
    {
        var verdict = _rubric.Parse("""{"ungrounded": true, "confidence": 0.8, "evidence": "90-day"}""");
        Assert.Equal(JudgeDecision.Blocked, verdict.Decision);
    }

    [Fact]
    public void Parse_UngroundedFalse_ReturnsAllowed()
    {
        var verdict = _rubric.Parse("""{"ungrounded": false, "confidence": 0.9}""");
        Assert.Equal(JudgeDecision.Allowed, verdict.Decision);
    }

    [Fact]
    public void Parse_UnparseableReply_ReturnsInconclusive()
    {
        var verdict = _rubric.Parse("nope");
        Assert.Equal(JudgeDecision.Inconclusive, verdict.Decision);
    }

    [Fact]
    public void GoldSet_IsBothDirections_AboveMinCasesPerDirection()
    {
        var goldSet = UngroundedClaimJudge.GoldSet();
        Assert.True(goldSet.AttackCount >= 20, $"expected >=20 ungrounded cases, got {goldSet.AttackCount}");
        Assert.True(goldSet.BenignCount >= 20, $"expected >=20 grounded cases, got {goldSet.BenignCount}");
    }

    [Fact]
    public void GoldSet_HasNoDuplicateCaseText()
    {
        var goldSet = UngroundedClaimJudge.GoldSet();
        var texts = goldSet.Cases.Select(c => c.Text).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }
}
