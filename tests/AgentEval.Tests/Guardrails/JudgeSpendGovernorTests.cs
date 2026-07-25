// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>P5-2: the shared token+call wallet judges reserve against — window refill, both caps, thread-safety.</summary>
public class JudgeSpendGovernorTests
{
    // A hand-driven clock so the window is deterministic (no wall-clock flake, no Task.Delay).
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void CallCap_ExhaustsAfterMaxCalls()
    {
        var gov = new JudgeSpendGovernor(maxCalls: 2, maxTokens: 1_000_000);
        Assert.True(gov.TryReserve(1));
        Assert.True(gov.TryReserve(1));
        Assert.False(gov.TryReserve(1));   // third call over the 2-call cap
    }

    [Fact]
    public void TokenCap_RefusesReservationThatWouldOverrun_ButNotOne_ThatExactlyFits()
    {
        var gov = new JudgeSpendGovernor(maxCalls: 1_000, maxTokens: 10);
        Assert.True(gov.TryReserve(6));    // 6 ≤ 10
        Assert.False(gov.TryReserve(6));   // 6 + 6 = 12 > 10 ⇒ refused, and does NOT consume
        Assert.True(gov.TryReserve(4));    // 6 + 4 = 10, exactly at the cap ⇒ allowed
        Assert.False(gov.TryReserve(1));   // now at 10 ⇒ nothing more fits
    }

    [Fact]
    public void Window_Refills_AfterElapsed()
    {
        var clock = new FakeClock();
        var gov = new JudgeSpendGovernor(maxCalls: 1, maxTokens: 1_000_000, window: TimeSpan.FromMinutes(1), timeProvider: clock);

        Assert.True(gov.TryReserve(1));
        Assert.False(gov.TryReserve(1));   // call cap hit within the window

        clock.Now = clock.Now.AddSeconds(61);   // window elapsed
        Assert.True(gov.TryReserve(1));    // refilled
    }

    [Fact]
    public void Window_DoesNotRefill_BeforeElapsed()
    {
        var clock = new FakeClock();
        var gov = new JudgeSpendGovernor(maxCalls: 1, maxTokens: 1_000_000, window: TimeSpan.FromMinutes(1), timeProvider: clock);

        Assert.True(gov.TryReserve(1));
        clock.Now = clock.Now.AddSeconds(59);   // still inside the window
        Assert.False(gov.TryReserve(1));
    }

    [Fact]
    public void NegativeEstimate_ClampedToZero_NeitherCreditsNorOverruns()
    {
        var gov = new JudgeSpendGovernor(maxCalls: 100, maxTokens: 10);
        Assert.True(gov.TryReserve(-100));   // clamped to 0 — a negative estimate must not manufacture headroom
        Assert.True(gov.TryReserve(10));     // 0 + 10 = 10 ⇒ still fits (the -100 added nothing)
        Assert.False(gov.TryReserve(1));     // now full
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void Ctor_RejectsNonPositiveCaps(int maxCalls, long maxTokens)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new JudgeSpendGovernor(maxCalls, maxTokens));

    [Fact]
    public void Ctor_RejectsNonPositiveWindow()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new JudgeSpendGovernor(1, 1, window: TimeSpan.Zero));

    [Fact]
    public void TryReserve_IsThreadSafe_NeverOverGrantsUnderContention()
    {
        // 8 threads racing 1000 reservations each against a 500-call budget must grant EXACTLY 500 — no torn
        // increment past the cap. (Token budget kept huge so only the call cap binds.)
        const int budget = 500;
        var gov = new JudgeSpendGovernor(maxCalls: budget, maxTokens: long.MaxValue);
        int granted = 0;

        var threads = new Thread[8];
        for (int t = 0; t < threads.Length; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    if (gov.TryReserve(1))
                    {
                        Interlocked.Increment(ref granted);
                    }
                }
            });
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Equal(budget, granted);   // exactly the budget — never more
    }
}
