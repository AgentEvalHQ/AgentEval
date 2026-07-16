// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using AgentEval.Guardrails.Judges.Rubrics;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>
/// Deterministic tests for <see cref="SkillInjectionGoldSet"/> and the calibration-harness machinery it
/// feeds — NOT a live-model test (see <see cref="SkillInjectionGoldSetCalibrationLiveCheck"/> for the real,
/// env-gated live answer this session measured: <c>IndirectInjectionRubric</c> does NOT clear calibration
/// on this gold set, so the skill-injection judge stays SHADOW-ONLY, per the design doc's own documented
/// contingency). This file proves (a) the gold set shape is correct and (b) the promotion gate itself is
/// honestly enforced — a judge is never "promoted" without earning it, regardless of the model behind it.
/// </summary>
public class SkillInjectionGoldSetCalibrationTests
{
    // A gate that blocks iff the predicate says so — a stand-in for "some judge with known behavior".
    private sealed class PredicateGate : IChatGate
    {
        private readonly Func<string, bool> _block;
        public string PolicyName { get; }
        public PredicateGate(Func<string, bool> block, string name = "pred") { _block = block; PolicyName = name; }
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(_block(text) ? GateVerdict.Block(PolicyName, "blocked") : GateVerdict.Allow(PolicyName));
    }

    [Fact]
    public void GoldSet_IsBothDirections_AboveMinCasesPerDirection()
    {
        var goldSet = SkillInjectionGoldSet.CalibrationGoldSet();

        Assert.True(goldSet.AttackCount >= 20, $"expected >=20 attack cases, got {goldSet.AttackCount}");
        Assert.True(goldSet.BenignCount >= 20, $"expected >=20 benign cases, got {goldSet.BenignCount}");
        Assert.Equal("indirect-injection", goldSet.Axis);   // reuses the flagship rubric's axis, per the design doc
    }

    [Fact]
    public void GoldSet_HasNoDuplicateCaseText()
    {
        var goldSet = SkillInjectionGoldSet.CalibrationGoldSet();
        var texts = goldSet.Cases.Select(c => c.Text).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void GoldSet_AllCasesCarryANote()
    {
        var goldSet = SkillInjectionGoldSet.CalibrationGoldSet();
        Assert.All(goldSet.Cases, c => Assert.False(string.IsNullOrWhiteSpace(c.Note)));
    }

    [Fact]
    public async Task CalibrationHarness_PerfectScriptedJudge_OnSkillGoldSet_IsInlineReady()
    {
        var goldSet = SkillInjectionGoldSet.CalibrationGoldSet();
        // A "perfect" oracle for this specific gold set: every attack case's text is known ahead of time.
        var attackTexts = new HashSet<string>(goldSet.Cases.Where(c => c.ShouldBlock).Select(c => c.Text), StringComparer.Ordinal);
        var perfect = new PredicateGate(t => attackTexts.Contains(t), "perfect-oracle");

        var report = await GateCalibrationHarness.EvaluateAsync(perfect, goldSet, new CalibrationOptions
        {
            MaxDangerousErrors = 0,
            MinCasesPerDirection = 20,
        });

        Assert.Equal(0, report.DangerousErrorCount);
        Assert.Equal(1.0, report.DecisiveAccuracy);
        Assert.True(report.IsInlineReady);
    }

    [Fact]
    public async Task CalibrationHarness_AllowEverythingJudge_OnSkillGoldSet_IsNotInlineReady()
    {
        // The honesty gate: a judge that never blocks anything must NEVER be reported inline-ready,
        // regardless of how the promotion bar is configured — this is the "no promotion by omission" invariant.
        var goldSet = SkillInjectionGoldSet.CalibrationGoldSet();
        var neverBlocks = new PredicateGate(_ => false, "allow-all");

        var report = await GateCalibrationHarness.EvaluateAsync(neverBlocks, goldSet, new CalibrationOptions
        {
            MaxDangerousErrors = 0,
            MinCasesPerDirection = 20,
        });

        Assert.Equal(goldSet.AttackCount, report.DangerousErrorCount);
        Assert.False(report.IsInlineReady);
        Assert.Throws<InvalidOperationException>(report.AssertInlineReady);
    }

    [Fact]
    public async Task CalibrationHarness_NoPromotionCriteria_NeverInlineReady_EvenIfPerfect()
    {
        // Promotion is never granted by omission: default CalibrationOptions sets no bar, so even a
        // perfect judge is not reported inline-ready without the caller explicitly configuring one.
        var goldSet = SkillInjectionGoldSet.CalibrationGoldSet();
        var attackTexts = new HashSet<string>(goldSet.Cases.Where(c => c.ShouldBlock).Select(c => c.Text), StringComparer.Ordinal);
        var perfect = new PredicateGate(t => attackTexts.Contains(t), "perfect-oracle");

        var report = await GateCalibrationHarness.EvaluateAsync(perfect, goldSet);   // default options — no bar set

        Assert.False(report.PromotionCriteriaConfigured);
        Assert.False(report.IsInlineReady);
    }
}
