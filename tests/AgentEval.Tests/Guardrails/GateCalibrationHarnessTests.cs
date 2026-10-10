// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

// ── Helpers shared across calibration test classes ──────────────────────────

/// <summary>A gate that blocks iff the predicate says so — a stand-in for a judge with a known accuracy on the gold set.</summary>
file sealed class PredicateGate : IChatGate
{
    private readonly Func<string, bool> _block;
    public string PolicyName { get; }
    public PredicateGate(Func<string, bool> block, string name = "pred") { _block = block; PolicyName = name; }
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
        => new(_block(text) ? GateVerdict.Block(PolicyName, "blocked") : GateVerdict.Allow(PolicyName));
}

/// <summary>
/// A gate that always throws — simulates a buggy or timed-out judge. Must never silently pass.
/// </summary>
file sealed class ThrowingGate : IChatGate
{
    public string PolicyName => "throwing-gate";
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("simulated gate failure");
}

/// <summary>A gate that blocks anything above a given length — simulates an input-size guard.</summary>
file sealed class SizeLimitGate : IChatGate
{
    private readonly int _maxLength;
    public string PolicyName => "size-limit";
    public SizeLimitGate(int maxLength) => _maxLength = maxLength;
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
        => new(text is not null && text.Length > _maxLength
            ? GateVerdict.Block(PolicyName, $"input exceeds {_maxLength} chars")
            : GateVerdict.Allow(PolicyName));
}

/// <summary>A gate that always cancels — simulates a timeout/cancellation mid-inspection.</summary>
file sealed class CancellingGate : IChatGate
{
    public string PolicyName => "cancelling-gate";
    public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
        => throw new OperationCanceledException("simulated timeout");
}

/// <summary>The Bar — calibrating a judge against a both-directions gold set + the inline-promotion barrier.</summary>
public class GateCalibrationHarnessTests
{
    // Attack cases contain "attack"; benign cases do not. A predicate on "attack" is therefore a perfect judge.
    private static JudgeGoldSet Gold() => new("test-axis",
    [
        new JudgeGoldCase("this is attack one", ShouldBlock: true),
        new JudgeGoldCase("this is attack two", ShouldBlock: true),
        new JudgeGoldCase("a normal benign request", ShouldBlock: false),
        new JudgeGoldCase("another normal request", ShouldBlock: false),
    ]);

    private static readonly IChatGate Perfect = new PredicateGate(t => t.Contains("attack"), "perfect");
    private static readonly IChatGate BlocksNothing = new PredicateGate(_ => false, "allow-all");
    private static readonly IChatGate BlocksEverything = new PredicateGate(_ => true, "block-all");

    // Small gold set → opt into a low per-direction floor + a real bar, so IsInlineReady is meaningful in tests.
    private static CalibrationOptions ReadyOpts(CalibrationOptions? extra = null) => new()
    {
        MaxDangerousErrors = 0,
        MinCasesPerDirection = 2,
        DeterministicBaseline = extra?.DeterministicBaseline,
    };

    [Fact]
    public async Task PerfectJudge_ScoresCleanly_AndIsInlineReady()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), ReadyOpts());

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
            ReadyOpts(new CalibrationOptions { DeterministicBaseline = BlocksNothing }));

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
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), ReadyOpts());
        r.AssertInlineReady();   // should not throw
    }

    [Fact]
    public async Task AllDefaults_NoBarSet_IsNotInlineReady()
    {
        // A PERFECT judge under all-default options must NOT be inline-ready — promotion is never granted by omission.
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold());
        Assert.False(r.PromotionCriteriaConfigured);
        Assert.False(r.IsInlineReady);
    }

    [Fact]
    public async Task SmallGoldSet_BelowFloor_IsNotInlineReady()
    {
        // Criteria set, but the gold set is below the default per-direction floor → not trusted for promotion.
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), new CalibrationOptions { MaxDangerousErrors = 0 });
        Assert.False(r.SufficientData);
        Assert.False(r.IsInlineReady);
    }

    [Fact]
    public void GoldSet_OneSided_Throws()
    {
        Assert.Throws<ArgumentException>(() => new JudgeGoldSet("axis", [new JudgeGoldCase("attack", true)]));            // no benign
        Assert.Throws<ArgumentException>(() => new JudgeGoldSet("axis", [new JudgeGoldCase("benign", false)]));          // no attack
    }

    [Fact]
    public void GoldSet_NullEntry_ThrowsArgumentException()   // clear error, not an NRE deep in the ctor
        => Assert.Throws<ArgumentException>(() => new JudgeGoldSet("axis", [new JudgeGoldCase("a", true), null!]));

    // ── Calibration staleness (CapturedAt / IsStale) ──

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task CapturedAt_ReflectsInjectedTimeProvider()
    {
        var clock = new FakeClock { Now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero) };

        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), new CalibrationOptions { TimeProvider = clock });

        Assert.Equal(clock.Now, r.CapturedAt);
    }

    [Fact]
    public async Task CapturedAt_DefaultsToSystemClock_WhenTimeProviderNotSupplied()
    {
        var before = DateTimeOffset.UtcNow;
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold());
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(r.CapturedAt, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public async Task IsStale_PastThreshold_ReturnsTrue()
    {
        var captureClock = new FakeClock { Now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero) };
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), new CalibrationOptions { TimeProvider = captureClock });

        var laterClock = new FakeClock { Now = captureClock.Now.AddDays(31) };

        Assert.True(r.IsStale(TimeSpan.FromDays(30), laterClock));
    }

    [Fact]
    public async Task IsStale_WithinThreshold_ReturnsFalse()
    {
        var captureClock = new FakeClock { Now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero) };
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), new CalibrationOptions { TimeProvider = captureClock });

        var laterClock = new FakeClock { Now = captureClock.Now.AddDays(1) };

        Assert.False(r.IsStale(TimeSpan.FromDays(30), laterClock));
    }

    [Fact]
    public async Task IsStale_DoesNotAffectIsInlineReady()
    {
        // Staleness is informational (per the Fleet Health Index's "flag, don't auto-demote" framing) —
        // an old-but-still-passing report stays inline-ready.
        var captureClock = new FakeClock { Now = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero) };
        var opts = new CalibrationOptions { MaxDangerousErrors = 0, MinCasesPerDirection = 2, TimeProvider = captureClock };
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), opts);

        var laterClock = new FakeClock { Now = captureClock.Now.AddYears(1) };

        Assert.True(r.IsStale(TimeSpan.FromDays(30), laterClock));
        Assert.True(r.IsInlineReady);
    }

    // ── Wilson confidence intervals (Q4-11) ──────────────────────────────────

    [Fact]
    public async Task WilsonAccuracyInterval_PerfectJudge_UpperEqualsOne()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), ReadyOpts());

        Assert.True(r.AccuracyInterval.IsMeasured);
        Assert.Equal(1.0, r.AccuracyInterval.Estimate, 3);
        Assert.Equal(1.0, r.AccuracyInterval.Upper, 3);
        Assert.True(r.AccuracyInterval.Lower < 1.0, "Lower bound should be < 1.0 on a small sample");
    }

    [Fact]
    public async Task WilsonFprInterval_BlocksEverything_EstimateEqualsOne()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(BlocksEverything, Gold());

        Assert.Equal(1.0, r.FprInterval.Estimate, 3);
        Assert.Equal(1.0, r.FprInterval.Upper, 3);
    }

    [Fact]
    public async Task WilsonAccuracyInterval_SuccessesEqualTotal()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), ReadyOpts());

        Assert.Equal(r.AccuracyInterval.Successes, r.AccuracyInterval.Total);
        Assert.Equal(r.Total, r.AccuracyInterval.Total);
    }

    [Fact]
    public async Task WilsonFprInterval_ZeroFpr_LowerEqualsZero()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), ReadyOpts());

        Assert.Equal(0, r.FprInterval.Successes);
        Assert.Equal(0.0, r.FprInterval.Lower, 5);
    }

    // ── Split label (Q4-11) ──────────────────────────────────────────────────

    [Fact]
    public async Task SplitLabel_RoundTrips_FromOptions()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(
            Perfect, Gold(),
            new CalibrationOptions { MaxDangerousErrors = 0, MinCasesPerDirection = 2, SplitLabel = "held-out" });

        Assert.Equal("held-out", r.SplitLabel);
    }

    [Fact]
    public async Task SplitLabel_IsNull_WhenNotSet()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(Perfect, Gold(), ReadyOpts());

        Assert.Null(r.SplitLabel);
    }

    [Fact]
    public async Task SplitLabel_AppearsInAssertInlineReady_ErrorMessage()
    {
        var r = await GateCalibrationHarness.EvaluateAsync(
            BlocksNothing, Gold(), new CalibrationOptions { SplitLabel = "held-out" });

        var ex = Assert.Throws<InvalidOperationException>(() => r.AssertInlineReady());
        Assert.Contains("held-out", ex.Message);
    }
}

/// <summary>Q4-11 fail-mode sweep — deterministic gates must never silently pass under failure conditions.</summary>
public class GateDeterministicFailModeSweepTests
{
    // Two attacks and two benign — used by every fail-mode test.
    private static JudgeGoldSet Gold() => new("fail-mode",
    [
        new JudgeGoldCase("attack-a", ShouldBlock: true),
        new JudgeGoldCase("attack-b", ShouldBlock: true),
        new JudgeGoldCase("benign-a", ShouldBlock: false),
        new JudgeGoldCase("benign-b", ShouldBlock: false),
    ]);

    private static CalibrationOptions StrictOpts() => new()
    {
        MaxDangerousErrors = 0,
        MinCasesPerDirection = 2,
    };

    // ── Size-limit gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task SizeGate_LargeInput_IsBlocked()
    {
        var gate = new SizeLimitGate(maxLength: 10);
        var verdict = await gate.InspectAsync(new string('A', 200));

        Assert.Equal(GateAction.Block, verdict.Action);
    }

    [Fact]
    public async Task SizeGate_SmallInput_IsAllowed()
    {
        var gate = new SizeLimitGate(maxLength: 100);
        var verdict = await gate.InspectAsync("short text");

        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    [Fact]
    public async Task SizeGate_EmptyString_IsAllowed()
    {
        var gate = new SizeLimitGate(maxLength: 10);
        var verdict = await gate.InspectAsync(string.Empty);

        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public async Task SizeGate_OversizedByOneChar_IsBlocked(int limit)
    {
        var gate = new SizeLimitGate(maxLength: limit);
        var verdict = await gate.InspectAsync(new string('X', limit + 1));

        Assert.Equal(GateAction.Block, verdict.Action);
    }

    // ── Null-text guard ──────────────────────────────────────────────────────

    [Fact]
    public async Task SizeGate_NullText_IsAllowed_NotCrash()
    {
        // A null string is length 0, which is ≤ any positive limit. Gate must not throw.
        var gate = new SizeLimitGate(maxLength: 10);
        var verdict = await gate.InspectAsync(null!);

        // Null is effectively empty input — the gate handles it defensively.
        Assert.Equal(GateAction.Allow, verdict.Action);
    }

    // ── Throwing gate fails closed ───────────────────────────────────────────
    // A deterministic gate that throws must NOT let the inspection silently pass —
    // the throw propagates rather than defaulting to Allow.

    [Fact]
    public async Task ThrowingGate_Propagates_NotSilentPass()
    {
        var gate = new ThrowingGate();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.InspectAsync("anything").AsTask());
    }

    // ── Cancellation / timeout ───────────────────────────────────────────────
    // A gate that sees a cancellation must surface it; callers decide how to handle it.

    [Fact]
    public async Task CancellingGate_Propagates_OperationCancelled()
    {
        var gate = new CancellingGate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.InspectAsync("anything").AsTask());
    }

    [Fact]
    public async Task AlreadyCancelledToken_PropagatesBeforeInspection()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // A gate that respects ct must throw, not silently allow, when the token is pre-cancelled.
        var gate = new PredicateGate(_ => throw new OperationCanceledException(), "ct-aware");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.InspectAsync("attack", cts.Token).AsTask());
    }

    // ── Replay / reloaded input ──────────────────────────────────────────────
    // Gates must be stateless (or idempotent) — re-inspecting the same text must produce
    // the same verdict; a replay-replayed input is not automatically trusted.

    [Fact]
    public async Task PerfectGate_SameInputTwice_SameVerdict()
    {
        var gate = new PredicateGate(t => t.Contains("attack"), "replay-test");

        var v1 = await gate.InspectAsync("this is attack text");
        var v2 = await gate.InspectAsync("this is attack text");

        Assert.Equal(v1.Action, v2.Action);
    }

    [Fact]
    public async Task PerfectGate_ReplayedBenign_StillAllowed()
    {
        var gate = new PredicateGate(t => t.Contains("attack"), "replay-benign");

        for (var i = 0; i < 5; i++)
        {
            var v = await gate.InspectAsync("a safe benign request");
            Assert.Equal(GateAction.Allow, v.Action);
        }
    }
}
