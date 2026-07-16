// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Xunit;

namespace AgentEval.Tests.Guardrails;

public class IntentActionMismatchRubricTests
{
    private readonly IntentActionMismatchRubric _rubric = new();

    [Fact]
    public void Axis_IsIntentActionMismatch() => Assert.Equal("intent-action-mismatch", _rubric.Axis);

    [Fact]
    public void Prefilter_NonEmptyText_ReturnsTrue() =>
        Assert.True(_rubric.Prefilter(IntentActionMismatchRubric.FormatCase("checking status", "delete_account", "{}")));

    [Fact]
    public void Prefilter_EmptyText_ReturnsFalse() => Assert.False(_rubric.Prefilter(""));

    [Fact]
    public void Parse_MismatchTrue_ReturnsBlocked()
    {
        var verdict = _rubric.Parse("""{"mismatch": true, "confidence": 0.9, "evidence": "delete_account"}""");
        Assert.Equal(JudgeDecision.Blocked, verdict.Decision);
    }

    [Fact]
    public void Parse_MismatchFalse_ReturnsAllowed()
    {
        var verdict = _rubric.Parse("""{"mismatch": false, "confidence": 0.9}""");
        Assert.Equal(JudgeDecision.Allowed, verdict.Decision);
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
    public void FormatCase_CombinesNarrationAndAction()
    {
        var text = IntentActionMismatchRubric.FormatCase("checking status", "delete_account", "{\"id\":1}");
        Assert.Contains("checking status", text);
        Assert.Contains("delete_account", text);
    }

    [Fact]
    public void GoldSet_IsBothDirections_AboveMinCasesPerDirection()
    {
        var goldSet = IntentActionMismatchJudge.GoldSet();
        Assert.True(goldSet.AttackCount >= 20, $"expected >=20 mismatch cases, got {goldSet.AttackCount}");
        Assert.True(goldSet.BenignCount >= 20, $"expected >=20 consistent cases, got {goldSet.BenignCount}");
    }

    [Fact]
    public void GoldSet_HasNoDuplicateCaseText()
    {
        var goldSet = IntentActionMismatchJudge.GoldSet();
        var texts = goldSet.Cases.Select(c => c.Text).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }
}
