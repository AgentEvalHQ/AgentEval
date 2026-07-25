// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>ParallelJudgeFanOut (the panel — fail-closed OR) and JudgeVerdictCache (precedent — allow-only cache).</summary>
public class JudgeCompositionTests
{
    private sealed class StubGate : IChatGate
    {
        private readonly GateVerdict _verdict;
        public int Calls;
        public string PolicyName { get; }
        public StubGate(GateVerdict verdict, string name = "stub") { _verdict = verdict; PolicyName = name; }
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return new ValueTask<GateVerdict>(_verdict);
        }
    }

    private sealed class ThrowingGate : IChatGate
    {
        public string PolicyName => "throwing";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class DelayingGate : IChatGate
    {
        public string PolicyName => "delaying";
        public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return GateVerdict.Allow(PolicyName);
        }
    }

    // ── ParallelJudgeFanOut ──

    [Fact]
    public async Task FanOut_AllAllow_Allows()
    {
        var panel = new ParallelJudgeFanOut([new StubGate(GateVerdict.Allow("a")), new StubGate(GateVerdict.Allow("b"))]);
        Assert.Equal(GateAction.Allow, (await panel.InspectAsync("x")).Action);
    }

    [Fact]
    public async Task FanOut_AnyBlock_Blocks_AggregatingEvidence()
    {
        var panel = new ParallelJudgeFanOut(
        [
            new StubGate(GateVerdict.Allow("clean")),
            new StubGate(GateVerdict.Block("pi", "injection found", ["do this instead"]), "pi"),
        ]);

        var v = await panel.InspectAsync("x");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("pi", v.Reason!);
        Assert.Contains("do this instead", v.Matches!);
    }

    [Fact]
    public async Task FanOut_ThrowingJudge_IsTreatedAsBlock_FailClosed()
    {
        var panel = new ParallelJudgeFanOut([new StubGate(GateVerdict.Allow("ok")), new ThrowingGate()]);
        Assert.Equal(GateAction.Block, (await panel.InspectAsync("x")).Action);
    }

    [Fact]
    public async Task FanOut_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var panel = new ParallelJudgeFanOut([new DelayingGate()]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await panel.InspectAsync("x", cts.Token));
    }

    [Fact]
    public void FanOut_EmptyJudges_Throws()
        => Assert.Throws<ArgumentException>(() => new ParallelJudgeFanOut([]));

    // ── JudgeVerdictCache ──

    [Fact]
    public async Task Cache_IdenticalAllow_CallsInnerOnce()
    {
        var inner = new StubGate(GateVerdict.Allow("ok"));
        var cache = new JudgeVerdictCache(inner);

        await cache.InspectAsync("same input");
        await cache.InspectAsync("same input");

        Assert.Equal(1, inner.Calls);   // second hit served from cache
    }

    [Fact]
    public async Task Cache_Block_IsNotCached_ReEvaluatedEachTime()
    {
        // A transient/fail-closed block must never be cached into a permanent one.
        var inner = new StubGate(GateVerdict.Block("j", "nope"));
        var cache = new JudgeVerdictCache(inner);

        await cache.InspectAsync("same input");
        await cache.InspectAsync("same input");

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Cache_DifferentInputs_NotConflated()
    {
        var inner = new StubGate(GateVerdict.Allow("ok"));
        var cache = new JudgeVerdictCache(inner);

        await cache.InspectAsync("input one");
        await cache.InspectAsync("input two");

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Cache_NullInner_Throws()
        => Assert.Throws<ArgumentNullException>(() => new JudgeVerdictCache(null!));

    [Fact]
    public void Cache_ZeroMaxEntries_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new JudgeVerdictCache(new StubGate(GateVerdict.Allow("x")), 0));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Cache_EmitsPrecedentEvents_HitCarriesOriginalTimestamp()
    {
        // P3-7: the cache surfaces hit/miss precedent events; a HIT reports when the served allow was FIRST
        // evaluated (its precedent age), so a caller can emit gate.cache.* evidence instead of a silent memoize.
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var events = new List<JudgeCacheEvent>();
        var inner = new StubGate(GateVerdict.Allow("j"), "judge:x");
        var cache = new JudgeVerdictCache(inner, onLookup: events.Add, timeProvider: clock);

        await cache.InspectAsync("same");                 // miss → evaluates, stamps the original timestamp
        clock.Advance(TimeSpan.FromMinutes(5));
        await cache.InspectAsync("same");                 // hit → served from cache

        Assert.Equal(1, inner.Calls);                     // the inner judge ran exactly once
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);

        Assert.Equal(2, events.Count);
        Assert.False(events[0].Hit);
        Assert.Null(events[0].OriginalTimestampUtc);
        Assert.True(events[1].Hit);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), events[1].OriginalTimestampUtc);
    }
}
