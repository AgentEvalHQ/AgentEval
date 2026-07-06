// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>The Bar — calibrating a judge against a both-directions gold set + the inline-promotion barrier.</summary>
public class GateCalibrationHarnessTests
{
    // A gate that blocks iff the predicate says so — a stand-in for a judge with a known accuracy on the gold set.
    private sealed class PredicateGate : IChatGate
    {
        private readonly Func<string, bool> _block;
        public string PolicyName { get; }
        public PredicateGate(Func<string, bool> block, string name = "pred") { _block = block; PolicyName = name; }
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => new(_block(text) ? GateVerdict.Block(PolicyName, "blocked") : GateVerdict.Allow(PolicyName));
    }

    // Attack cases contain "attack"; benign cases do not. A predicate on "attack" is therefore a perfect judge.
    private static JudgeGoldSet Gold() => new("test-axis",
    [
        new JudgeGoldCase("this is attack one", ShouldBlock: true),
        new JudgeGoldCase("this is attack two", ShouldBlock: true),
        new JudgeGoldCase("a normal benign request", ShouldBlock: false),
        new JudgeGoldCase("another normal request", ShouldBlock: false),
    ]);

    private static readonly PredicateGate Perfect = new(t => t.Contains("attack"), "perfect");
    private static readonly PredicateGate BlocksNothing = new(_ => false, "allow-all");
    private static readonly PredicateGate BlocksEverything = new(_ => true, "block-all");

    [Fact]
    public async Task PerfectJudge_ScoresCleanly_AndIsInlineReady()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold());

        Assert.Equal(1.0, r.DecisiveAccuracy);
        Assert.Equal(0, r.DangerousErrorCount);
        Assert.Equal(0.0, r.FalsePositiveRate);
        Assert.Equal(1.0, r.KappaVsGold, 3);
        Assert.True(r.IsInlineReady);
    }

    [Fact]
    public async Task JudgeThatMissesAttacks_CountsDangerousErrors()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(BlocksNothing, Gold());

        Assert.Equal(2, r.DangerousErrorCount);   // both attacks allowed
        Assert.Equal(0.5, r.DecisiveAccuracy);
    }

    [Fact]
    public async Task BlockEverything_HasNoMissesButAllFalsePositives()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(BlocksEverything, Gold());

        Assert.Equal(0, r.DangerousErrorCount);
        Assert.Equal(1.0, r.FalsePositiveRate);   // every benign case blocked
        Assert.Equal(0.5, r.DecisiveAccuracy);
    }

    [Fact]
    public async Task BeatsBaseline_TrueWhenJudgeIsBetter()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(),
            new CalibrationOptions { DeterministicBaseline = BlocksNothing });

        Assert.Equal(0.5, r.BaselineAccuracy);
        Assert.True(r.BeatsBaseline);
        Assert.True(r.IsInlineReady);
    }

    [Fact]
    public async Task BeatsBaseline_FalseWhenJudgeIsNotBetter_NotInlineReady()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(BlocksNothing, Gold(),
            new CalibrationOptions { DeterministicBaseline = Perfect });

        Assert.False(r.BeatsBaseline);
        Assert.False(r.IsInlineReady);
        Assert.Throws<InvalidOperationException>(() => r.AssertInlineReady());
    }

    [Fact]
    public async Task ZeroMissThreshold_FailsAJudgeThatMisses()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(BlocksNothing, Gold(),
            new CalibrationOptions { MaxDangerousErrors = 0 });

        Assert.False(r.MeetsThresholds);
        Assert.False(r.IsInlineReady);
    }

    [Fact]
    public async Task PerfectJudge_AssertInlineReady_DoesNotThrow()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), new CalibrationOptions { MaxDangerousErrors = 0 });
        r.AssertInlineReady();   // should not throw
    }

    [Fact]
    public void GoldSet_OneSided_Throws()
    {
        Assert.Throws<ArgumentException>(() => new JudgeGoldSet("axis", [new JudgeGoldCase("attack", true)]));            // no benign
        Assert.Throws<ArgumentException>(() => new JudgeGoldSet("axis", [new JudgeGoldCase("benign", false)]));          // no attack
    }
}
